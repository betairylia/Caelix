using System;
using System.Collections.Generic;
using Caelix.Rendering.RayQuery;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.Universal.Internal;

namespace Caelix.Rendering.GiPrototypes
{
    internal sealed class CaelixGiPrototypePass : ScriptableRenderPass, IDisposable
    {
        private readonly CaelixGiPrototypeFeature feature;
        private readonly Material presentMaterial;
        private readonly CopyDepthPass depthCopy;
        private readonly Dictionary<Camera, CaelixGiResources> cameras = new();
        private readonly List<Camera> staleCameras = new();
        private readonly GraphicsBuffer[] pages = new GraphicsBuffer[CaelixBrickPool.MaxNamedPages];
        private CaelixRayQueryRenderer scene;
        private CaelixGiSettings settings;
        private Cubemap fallbackSky;

        public long AllocatedBytes
        {
            get
            {
                long bytes = 0;
                foreach (var camera in cameras.Values) bytes += camera.EstimatedBytes;
                return bytes;
            }
        }

        private sealed class PassData
        {
            public CaelixGiPrototypePass owner;
            public CaelixGiResources state;
            public CaelixRayQueryRenderer scene;
            public Matrix4x4 view, cameraToWorld;
            public Vector3 position;
            public Vector2 jitter;
            public float zoom;
            public Texture sky;
            public bool reset, resetScreen, stationary, bake, buildEmitters;
            public TextureHandle cameraColor, cameraDepth, source;
        }

        public CaelixGiPrototypePass(CaelixGiPrototypeFeature feature, Material presentMaterial)
        {
            this.feature = feature;
            this.presentMaterial = presentMaterial;
            depthCopy = new CopyDepthPass(RenderPassEvent.AfterRenderingOpaques, feature.depthCopyShader,
                customPassName: "Caelix GI depth refresh");
            ConfigureInput(ScriptableRenderPassInput.Depth);
            requiresIntermediateTexture = true;
        }

        public void Configure(CaelixRayQueryRenderer renderer, CaelixGiSettings selected)
        {
            scene = renderer;
            settings = selected;
        }

        public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
        {
            if (scene == null || !scene.HasResources || presentMaterial == null) return;
            var cameraData = frameData.Get<UniversalCameraData>();
            var camera = cameraData.camera;
            int width = Mathf.Max(1, Mathf.RoundToInt(cameraData.scaledWidth * settings.resolutionScale));
            int height = Mathf.Max(1, Mathf.RoundToInt(cameraData.scaledHeight * settings.resolutionScale));
            if (!cameras.TryGetValue(camera, out var state) || !ReferenceEquals(state.Settings, settings))
            {
                state?.Dispose();
                state = new CaelixGiResources(width, height, settings);
                cameras[camera] = state;
            }
            else state.Resize(width, height);
            state.LastUsedFrame = Time.frameCount;
            PruneCameras();
            Matrix4x4 view = cameraData.GetViewMatrix();
            float zoom = Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f);
            Texture sky = ResolveSky();
            bool reset = state.NeedsLightingReset(scene, sky);
            bool stationary = state.ValidHistory && view == state.PreviousView && Mathf.Approximately(zoom, state.PreviousZoom);
            // Reservoir reprojection assumes the previous projection has the same field of view.
            bool resetScreen = reset || !state.ValidHistory || !Mathf.Approximately(zoom, state.PreviousZoom);
            // Streaming bumps the scene revision every tick. The group table is cheap and must follow
            // every reset, but the emitter proposal reads every voxel of every brick, so it waits
            // until the scene has been stable for EmitterSettleFrames. Until then emitter sampling
            // reports no bricks and training counts emission where rays find it.
            bool buildEmitters = false;
            if (settings.UsesGroupTable && reset)
            {
                state.UpdateEmissionGroups(scene, emitters: false);
                state.EmittersStale = settings.UsesEmitterSampling;
                state.LastResetFrame = state.Frame;
            }
            else if (settings.UsesGroupTable && state.EmittersStale
                && state.Frame - state.LastResetFrame >= CaelixGiSettings.EmitterSettleFrames)
            {
                state.UpdateEmissionGroups(scene, emitters: true);
                state.EmittersStale = false;
                buildEmitters = true;
            }
            state.Scene = scene;
            state.SceneRevision = scene.GiSceneRevision;
            state.Sky = sky;
            state.SkyUpdateCount = sky.updateCount;

            var targets = frameData.Get<UniversalResourceData>();
            using (var builder = graph.AddUnsafePass<PassData>("Caelix GI " + settings.approach, out var data))
            {
                data.owner = this;
                data.state = state;
                data.scene = scene;
                data.view = view;
                data.cameraToWorld = view.inverse;
                data.position = camera.transform.position;
                data.zoom = zoom;
                data.jitter = new Vector2(Halton(state.Frame + 1, 2) - 0.5f, Halton(state.Frame + 1, 3) - 0.5f);
                data.sky = sky;
                data.reset = reset;
                data.resetScreen = resetScreen;
                data.stationary = stationary;
                data.bake = !scene.MaterialsBaked;
                data.buildEmitters = buildEmitters;
                data.cameraColor = targets.activeColorTexture;
                data.cameraDepth = targets.activeDepthTexture;
                data.source = graph.ImportTexture(state.ResolvedColor);
                foreach (var buffer in state.Buffers) builder.UseBuffer(graph.ImportBuffer(buffer), AccessFlags.ReadWrite);
                foreach (var texture in state.Textures)
                    builder.UseTexture(texture == state.ResolvedColor ? data.source : graph.ImportTexture(texture), AccessFlags.ReadWrite);
                builder.UseTexture(data.cameraColor, AccessFlags.ReadWrite);
                builder.UseTexture(data.cameraDepth, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (PassData pass, UnsafeGraphContext context) =>
                    pass.owner.Execute(pass, CommandBufferHelpers.GetNativeCommandBuffer(context.cmd)));
            }
            // URP may have copied depth before our event. Refresh its sampled depth after voxel presentation.
            if (targets.cameraDepthTexture.IsValid() && targets.cameraDepthTexture != targets.activeDepthTexture)
                depthCopy.Render(graph, frameData, targets.cameraDepthTexture, targets.activeDepthTexture,
                    bindAsCameraDepth: true, passName: "Caelix GI depth refresh");
        }

        private void Execute(PassData data, CommandBuffer cmd)
        {
            var s = data.state;
            CaelixRayQueryDispatch.FillBrickPages(data.scene.Pool, pages);
            cmd.BuildRayTracingAccelerationStructure(data.scene.voxelScene);
            if (data.bake)
            {
                CaelixRayQueryDispatch.BakeMaterials(cmd, feature.referenceShader,
                    CaelixRayQueryDispatch.FindBakeKernels(feature.referenceShader), data.scene.MaterialTable);
                data.scene.MaterialsBaked = true;
            }
            if (s.Settings.UsesCache && data.reset) ClearCache(cmd, s);
            if (s.Settings.UsesSkin && data.reset) ClearSkin(cmd, s);
            if (data.buildEmitters) BuildEmission(cmd, data);
            DispatchImage(cmd, feature.referenceShader, "GiGenerateSurfaces", data);
            switch (s.Settings.approach)
            {
                case CaelixGiApproach.RestirGi:
                    DispatchImage(cmd, feature.resamplingShader, "GiGenerateCandidates", data);
                    DispatchImage(cmd, feature.resamplingShader, "GiRestirTemporal", data);
                    DispatchImage(cmd, feature.resamplingShader, "GiRestirSpatial", data);
                    break;
                case CaelixGiApproach.NaadfInspired:
                    DispatchImage(cmd, feature.resamplingShader, "GiGenerateCandidates", data);
                    DispatchImage(cmd, feature.resamplingShader, "GiNaadfBuildBuckets", data);
                    DispatchImage(cmd, feature.resamplingShader, "GiNaadfSpatial", data);
                    break;
                case CaelixGiApproach.FaceRadianceCache:
                    DispatchImage(cmd, feature.cachePathShader, "TraceRadianceCache", data);
                    break;
                case CaelixGiApproach.FacePathGuiding:
                    DispatchImage(cmd, feature.cachePathShader, "TraceGuidedPath", data);
                    break;
                case CaelixGiApproach.BrickEmissionPathGuiding:
                    DispatchImage(cmd, feature.cachePathShader, "TraceEmissionGuidedPath", data);
                    break;
                case CaelixGiApproach.BrickSkinIrradiance:
                    DispatchImage(cmd, feature.skinShader, "GiSkinShade", data);
                    TrainSkin(cmd, data);
                    break;
                default:
                    DispatchImage(cmd, feature.referenceShader, "GiReference", data);
                    break;
            }
            if (s.Settings.UsesCache)
            {
                var cs = feature.cacheShader;
                int kernel = cs.FindKernel("ResolveGiCache");
                BindCache(cmd, cs, kernel, s);
                cmd.DispatchCompute(cs, kernel, (s.Settings.cacheCapacity + 63) / 64, 1, 1);
            }
            Resolve(cmd, data);
            cmd.SetRenderTarget(data.cameraColor, data.cameraDepth);
            cmd.SetGlobalTexture("_GiDepth", s.Depth);
            Blitter.BlitTexture(cmd, data.source, new Vector4(1, 1, 0, 0), presentMaterial, 0);
            s.FinishFrame(data.view, data.zoom, data.jitter);
        }

        private void DispatchImage(CommandBuffer cmd, ComputeShader cs, string name, PassData data)
        {
            int kernel = cs.FindKernel(name);
            BindCommon(cmd, cs, kernel, data);
            var s = data.state;
            if (cs == feature.resamplingShader) BindResampling(cmd, cs, kernel, data);
            if (cs == feature.cachePathShader)
            {
                BindCache(cmd, cs, kernel, s);
                if (s.EmissionGroups != null) BindEmission(cmd, cs, kernel, s);
            }
            if (cs == feature.skinShader)
            {
                BindSkin(cmd, cs, kernel, s);
                BindEmission(cmd, cs, kernel, s);
            }
            // Bucket generation has one thread per tile, all other image kernels have 8x8 threads.
            cmd.DispatchCompute(cs, kernel, (s.Width + 7) / 8, (s.Height + 7) / 8, 1);
        }

        private void BindCommon(CommandBuffer cmd, ComputeShader cs, int kernel, PassData data)
        {
            var s = data.state;
            CaelixRayQueryDispatch.SetGroupKeyword(cmd, cs, data.scene.GroupSize);
            CaelixRayQueryDispatch.BindSceneInputs(cmd, cs, kernel, data.scene.voxelScene, pages,
                data.scene.Instances.Buffer, data.scene.MaterialTable);
            cmd.SetComputeIntParams(cs, "g_GiResolution", s.Width, s.Height);
            cmd.SetComputeIntParam(cs, "g_GiFrameIndex", s.Frame);
            cmd.SetComputeIntParam(cs, "g_GiSamplesPerPixel", s.Settings.samplesPerPixel);
            cmd.SetComputeIntParam(cs, "g_GiMaxBounces", s.Settings.maxBounces);
            cmd.SetComputeFloatParam(cs, "g_GiSkyIntensity", s.Settings.skyIntensity);
            cmd.SetComputeFloatParam(cs, "g_GiZoom", data.zoom);
            cmd.SetComputeFloatParam(cs, "g_GiAspect", (float)s.Width / s.Height);
            cmd.SetComputeFloatParam(cs, "g_GiGuidingStrength", s.Settings.guidingStrength);
            cmd.SetComputeVectorParam(cs, "g_GiCameraPosition", data.position);
            cmd.SetComputeVectorParam(cs, "g_GiJitter", data.jitter);
            cmd.SetComputeMatrixParam(cs, "g_GiCameraToWorld", data.cameraToWorld);
            cmd.SetComputeMatrixParam(cs, "g_GiWorldToCamera", data.view);
            cmd.SetComputeTextureParam(cs, kernel, "g_Sky", data.sky);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiSurfaces", s.Surfaces);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiSurfacesOut", s.Surfaces);
            cmd.SetComputeTextureParam(cs, kernel, "g_GiOutputColor", s.RawColor);
            cmd.SetComputeTextureParam(cs, kernel, "g_GiOutputDepth", s.Depth);
        }

        private static void BindResampling(CommandBuffer cmd, ComputeShader cs, int kernel, PassData data)
        {
            var s = data.state;
            cmd.SetComputeIntParam(cs, "g_GiResetHistory", data.resetScreen ? 1 : 0);
            cmd.SetComputeIntParam(cs, "g_GiNaadfHistoryValid", !data.reset && data.stationary ? 1 : 0);
            cmd.SetComputeIntParam(cs, "g_GiSpatialSamples", s.Settings.spatialSamples);
            cmd.SetComputeIntParam(cs, "g_GiSpatialRadius", s.Settings.spatialRadius);
            cmd.SetComputeIntParam(cs, "g_GiMaxHistoryFrames", s.Settings.reservoirHistoryFrames);
            cmd.SetComputeIntParam(cs, "g_GiMaxReservoirCount", s.Settings.maxReservoirCount);
            cmd.SetComputeMatrixParam(cs, "g_GiPreviousWorldToCamera", s.PreviousView);
            cmd.SetComputeVectorParam(cs, "g_GiPreviousJitter", s.PreviousJitter);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiPreviousSurfaces", s.PreviousSurfaces);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiCandidates", s.Candidates);
            cmd.SetComputeTextureParam(cs, kernel, "g_GiFallback", s.Fallback);
            if (s.TemporalReservoirs != null)
            {
                cmd.SetComputeBufferParam(cs, kernel, "g_GiTemporalReservoirs", s.TemporalReservoirs);
                cmd.SetComputeBufferParam(cs, kernel, "g_GiReservoirHistory", s.ReservoirHistory);
                cmd.SetComputeBufferParam(cs, kernel, "g_GiReservoirOutput", s.Candidates);
            }
            if (s.Buckets != null)
            {
                cmd.SetComputeBufferParam(cs, kernel, "g_GiBuckets", s.Buckets);
                cmd.SetComputeBufferParam(cs, kernel, "g_GiPreviousBuckets", s.PreviousBuckets);
                cmd.SetComputeBufferParam(cs, kernel, "g_GiBucketSamples", s.BucketSamples);
                cmd.SetComputeBufferParam(cs, kernel, "g_GiPreviousBucketSamples", s.PreviousBucketSamples);
            }
        }

        private static void BindCache(CommandBuffer cmd, ComputeShader cs, int kernel, CaelixGiResources s)
        {
            cmd.SetComputeIntParam(cs, "g_GiCacheCapacity", s.Settings.cacheCapacity);
            cmd.SetComputeIntParam(cs, "g_GiCacheFrame", s.Frame);
            cmd.SetComputeIntParam(cs, "g_GiCacheMaxAge", s.Settings.cacheMaxAge);
            cmd.SetComputeIntParam(cs, "g_GiCacheHistoryLimit", s.Settings.cacheHistorySamples);
            cmd.SetComputeIntParam(cs, "g_GiCacheMinSamples", s.Settings.cacheMinSamples);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiCacheKeys", s.CacheKeys);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiCacheHistory", s.CacheHistory);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiCacheNextHistory", s.NextCacheHistory);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiCacheAccum", s.CacheAccum);
        }

        private void ClearCache(CommandBuffer cmd, CaelixGiResources s)
        {
            var cs = feature.cacheShader;
            int kernel = cs.FindKernel("ClearGiCache");
            BindCache(cmd, cs, kernel, s);
            cmd.DispatchCompute(cs, kernel, (s.Settings.cacheCapacity + 63) / 64, 1, 1);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiCacheNextHistory", s.CacheHistory);
            cmd.DispatchCompute(cs, kernel, (s.Settings.cacheCapacity + 63) / 64, 1, 1);
        }

        private static void BindSkin(CommandBuffer cmd, ComputeShader cs, int kernel, CaelixGiResources s)
        {
            cmd.SetComputeIntParam(cs, "g_GiSkinCapacity", s.Settings.skinBrickCapacity);
            cmd.SetComputeIntParam(cs, "g_GiSkinTrainingBudget", s.Settings.skinTrainingBudget);
            cmd.SetComputeIntParam(cs, "g_GiSkinWalks", s.Settings.skinWalksPerPixel);
            cmd.SetComputeIntParam(cs, "g_GiSkinWalkBounces", s.Settings.skinWalkBounces);
            cmd.SetComputeIntParam(cs, "g_GiSkinTrainingRays", s.Settings.skinTrainingRays);
            cmd.SetComputeIntParam(cs, "g_GiSkinTrainingWalks", s.Settings.skinTrainingWalks);
            cmd.SetComputeIntParam(cs, "g_GiSkinColdBounces", s.Settings.skinColdBounces);
            cmd.SetComputeIntParam(cs, "g_GiSkinEmitterSampling", s.Settings.skinEmitterSampling ? 1 : 0);
            cmd.SetComputeIntParam(cs, "g_GiSkinFrame", s.Frame);
            cmd.SetComputeIntParam(cs, "g_GiSkinMaxAge", s.Settings.cacheMaxAge);
            cmd.SetComputeIntParam(cs, "g_GiSkinHistoryLimit", s.Settings.cacheHistorySamples);
            cmd.SetComputeIntParam(cs, "g_GiSkinMinSamples", s.Settings.cacheMinSamples);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiSkinBricks", s.SkinBricks);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiSkinTexels", s.SkinTexels);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiSkinMarks", s.SkinMarks);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiSkinTouched", s.SkinTouched);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiSkinControl", s.SkinControl);
        }

        private void ClearSkin(CommandBuffer cmd, CaelixGiResources s)
        {
            var cs = feature.skinShader;
            int kernel = cs.FindKernel("GiSkinClear");
            BindSkin(cmd, cs, kernel, s);
            cmd.DispatchCompute(cs, kernel, (s.Settings.skinBrickCapacity + 63) / 64, 1, 1);
        }

        /// <summary>Trains the texels this frame's walks requested, then expires untouched bricks.</summary>
        private void TrainSkin(CommandBuffer cmd, PassData data)
        {
            var s = data.state;
            var cs = feature.skinShader;
            int prepare = cs.FindKernel("GiSkinPrepareTraining");
            BindSkin(cmd, cs, prepare, s);
            cmd.DispatchCompute(cs, prepare, 1, 1, 1);
            int train = cs.FindKernel("GiSkinTrain");
            BindCommon(cmd, cs, train, data);
            BindSkin(cmd, cs, train, s);
            BindEmission(cmd, cs, train, s);
            cmd.DispatchCompute(cs, train, s.SkinControl, 4 * 4);
            int expire = cs.FindKernel("GiSkinExpire");
            BindSkin(cmd, cs, expire, s);
            cmd.DispatchCompute(cs, expire, (s.Settings.skinBrickCapacity + 63) / 64, 1, 1);
        }

        private static void BindEmission(CommandBuffer cmd, ComputeShader cs, int kernel, CaelixGiResources s)
        {
            cmd.SetComputeIntParam(cs, "g_GiEmissionGroupCount", s.EmissionGroupCount);
            cmd.SetComputeIntParam(cs, "g_GiEmissionBrickCount", s.EmissionBrickCount);
            cmd.SetComputeIntParam(cs, "g_GiEmissionLeafCount", s.EmissionLeafCount);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiEmissionGroups", s.EmissionGroups);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiEmissionVoxelWeights", s.EmissionWeights);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiEmissionTree", s.EmissionTree);
        }

        private void BuildEmission(CommandBuffer cmd, PassData data)
        {
            var s = data.state;
            var cs = feature.emissionShader;
            int clear = cs.FindKernel("ClearGiEmission");
            BindEmission(cmd, cs, clear, s);
            // Clear and refit support a second dispatch dimension for large scenes.
            cmd.DispatchCompute(cs, clear, Mathf.Min(65535, (2 * s.EmissionLeafCount + 63) / 64),
                Mathf.Max(1, (2 * s.EmissionLeafCount + 64 * 65535 - 1) / (64 * 65535)), 1);
            int build = cs.FindKernel("BuildGiEmission");
            CaelixRayQueryDispatch.SetGroupKeyword(cmd, cs, data.scene.GroupSize);
            CaelixRayQueryDispatch.BindSceneInputs(cmd, cs, build, data.scene.voxelScene, pages,
                data.scene.Instances.Buffer, data.scene.MaterialTable);
            BindEmission(cmd, cs, build, s);
            for (int offset = 0; offset < s.EmissionBrickCount; offset += 65535)
            {
                int count = Mathf.Min(65535, s.EmissionBrickCount - offset);
                cmd.SetComputeIntParam(cs, "g_GiEmissionBuildOffset", offset);
                cmd.SetComputeIntParam(cs, "g_GiEmissionBuildCount", count);
                cmd.DispatchCompute(cs, build, count, 1, 1);
            }
            int refit = cs.FindKernel("RefitGiEmission");
            BindEmission(cmd, cs, refit, s);
            for (int level = s.EmissionLeafCount / 2; level > 0; level /= 2)
            {
                cmd.SetComputeIntParam(cs, "g_GiEmissionLevelOffset", level);
                cmd.SetComputeIntParam(cs, "g_GiEmissionLevelCount", level);
                cmd.DispatchCompute(cs, refit, Mathf.Min(65535, (level + 63) / 64),
                    Mathf.Max(1, (level + 64 * 65535 - 1) / (64 * 65535)), 1);
            }
        }

        private void Resolve(CommandBuffer cmd, PassData data)
        {
            var s = data.state;
            var cs = feature.resolveShader;
            int kernel = cs.FindKernel("GiResolve");
            cmd.SetComputeIntParams(cs, "g_GiResolution", s.Width, s.Height);
            cmd.SetComputeIntParam(cs, "g_GiAccumulationValid", !data.reset && s.ValidHistory ? 1 : 0);
            cmd.SetComputeIntParam(cs, "g_GiStationary", data.stationary ? 1 : 0);
            cmd.SetComputeIntParam(cs, "g_GiAccumulationLimit", s.Settings.accumulationFrames);
            cmd.SetComputeMatrixParam(cs, "g_GiPreviousWorldToCamera", s.PreviousView);
            cmd.SetComputeVectorParam(cs, "g_GiPreviousJitter", s.PreviousJitter);
            cmd.SetComputeFloatParam(cs, "g_GiPreviousZoom", s.PreviousZoom);
            cmd.SetComputeVectorParam(cs, "g_GiCameraPosition", data.position);
            cmd.SetComputeVectorParam(cs, "g_GiPreviousCameraPosition", s.PreviousView.inverse.GetColumn(3));
            cmd.SetComputeBufferParam(cs, kernel, "g_GiSurfaces", s.Surfaces);
            cmd.SetComputeBufferParam(cs, kernel, "g_GiPreviousSurfaces", s.PreviousSurfaces);
            cmd.SetComputeTextureParam(cs, kernel, "g_GiRawColor", s.RawColor);
            cmd.SetComputeTextureParam(cs, kernel, "g_GiPreviousColor", s.PreviousColor);
            cmd.SetComputeTextureParam(cs, kernel, "g_GiResolvedColor", s.ResolvedColor);
            cmd.DispatchCompute(cs, kernel, (s.Width + 7) / 8, (s.Height + 7) / 8, 1);
        }

        private Texture ResolveSky()
        {
            var sky = Shader.GetGlobalTexture(CaelixShaderIDs.GlossyEnvironmentCubeMap);
            if (sky != null) return sky;
            if (fallbackSky == null)
            {
                fallbackSky = new Cubemap(1, TextureFormat.RGBAHalf, false) { hideFlags = HideFlags.HideAndDontSave };
                for (int face = 0; face < 6; face++) fallbackSky.SetPixel((CubemapFace)face, 0, 0, Color.white);
                fallbackSky.Apply();
            }
            return fallbackSky;
        }

        private void PruneCameras()
        {
            staleCameras.Clear();
            foreach (var entry in cameras)
                if (entry.Key == null || Time.frameCount - entry.Value.LastUsedFrame > 120) staleCameras.Add(entry.Key);
            foreach (var key in staleCameras) { cameras[key].Dispose(); cameras.Remove(key); }
        }

        private static float Halton(int index, int radix)
        {
            float fraction = 1, value = 0;
            while (index > 0) { fraction /= radix; value += fraction * (index % radix); index /= radix; }
            return value;
        }

        public void Dispose()
        {
            foreach (var camera in cameras.Values) camera.Dispose();
            cameras.Clear();
            CoreUtils.Destroy(fallbackSky);
            depthCopy.Dispose();
        }
    }
}
