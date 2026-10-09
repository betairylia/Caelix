using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Caelix.Rendering;
using Caelix.Rendering.GiPrototypes;
using Caelix.Rendering.RayQuery;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Caelix.Tests
{
    /// <summary>Runs real prototype kernels on a tiny RTAS, entirely in Edit Mode.</summary>
    public class GiPrototypeGpuSmokeTests
    {
        private const int Size = 8;
        private const string ShaderRoot = "Packages/ink.irylia.caelix/Runtime/Rendering/Shaders/GiPrototypes/";

        [StructLayout(LayoutKind.Sequential)]
        private struct MaterialRecord
        {
            public Vector3 albedo, emission;
            public float smoothness, metallic, ior, extinction;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SurfaceRecord
        {
            public Vector3 position;
            public uint materialId;
            public Vector3 normal;
            public uint flags;
            public Vector3 previousPosition;
            public float depth;
            public uint token, voxelFace, reserved0, reserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SkinBrickRecord
        {
            public uint state, token, brickIndex, touchedFrame;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CacheHistoryRecord
        {
            public Vector4 irradiance, guide0, guide1;
            public uint radianceFrame, guideFrame, guideCount, reserved;
        }

        private sealed class Fixture : IDisposable
        {
            public readonly CaelixGiResources State;
            public readonly RayTracingAccelerationStructure Acceleration;
            public readonly CaelixRayQueryInstanceTable Instances;
            public readonly GraphicsBuffer Brick, Materials;
            public readonly Cubemap Sky;
            public readonly ComputeShader Reference, Resampling, Cache, CachePath, Emission, Skin;
            public Vector3 CameraPosition = new(4, 4, 12);
            public readonly Color SkyColor = new(0.25f, 0.5f, 1, 1);
            public Color ExpectedEmission;
            public Vector3 ExpectedDiffuse;
            public uint Token;
            private readonly List<GraphicsBuffer> auxiliary = new();
            private readonly List<ComputeShader> shaders = new();

            public Fixture(CaelixGiApproach approach, bool cube, int bounces)
            {
                State = new CaelixGiResources(Size, Size, new CaelixGiSettings
                {
                    approach = approach, maxBounces = bounces, samplesPerPixel = 2,
                    cacheCapacity = 1024, cacheMinSamples = 1, spatialSamples = 2, spatialRadius = 2,
                    skinBrickCapacity = 256, skinTrainingBudget = 1024
                });
                Reference = Shader("GiReference.compute");
                Resampling = Shader("GiResampling.compute");
                Cache = Shader("GiCache.compute");
                CachePath = Shader("GiCachePath.compute");
                Emission = Shader("GiEmission.compute");
                Skin = Shader("GiSkin.compute");
                Acceleration = new RayTracingAccelerationStructure(new RayTracingAccelerationStructure.Settings
                {
                    managementMode = RayTracingAccelerationStructure.ManagementMode.Manual,
                    rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.Everything,
                    layerMask = -1
                });
                Instances = new CaelixRayQueryInstanceTable();
                Brick = new GraphicsBuffer(GraphicsBuffer.Target.Raw, BrickRecordLayout.BRICK_DATA_LENGTH, 4);
                Materials = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 65536, Marshal.SizeOf<MaterialRecord>());
                Sky = new Cubemap(1, TextureFormat.RGBAFloat, false);
                for (int face = 0; face < 6; ++face) Sky.SetPixel((CubemapFace)face, 0, 0, SkyColor);
                Sky.Apply();

                using (var cmd = new CommandBuffer())
                {
                    CaelixRayQueryDispatch.BakeMaterials(cmd, Reference, CaelixRayQueryDispatch.FindBakeKernels(Reference), Materials);
                    Graphics.ExecuteCommandBuffer(cmd);
                }
                uint materialId = BlockEncoding.IsOpaque(0x4000) ? 0x7ffcu : 0x8001u;
                var material = new MaterialRecord[1];
                Materials.GetData(material, 0, (int)materialId, 1);
                if (!BlockEncoding.IsOpaque(0x4000))
                {
                    material[0] = new MaterialRecord { albedo = Vector3.one * 0.8f, emission = new Vector3(0.4f, 0.2f, 0.1f), ior = 1.5f };
                    Materials.SetData(material, 0, (int)materialId, 1);
                }
                ExpectedEmission = new Color(material[0].emission.x, material[0].emission.y, material[0].emission.z, 1);
                float eta = Mathf.Max(1, material[0].ior);
                float f0 = (eta - 1) / (eta + 1) * ((eta - 1) / (eta + 1));
                ExpectedDiffuse = Vector3.Scale(material[0].albedo, Vector3.one * ((1 - material[0].metallic) * (1 - f0)));

                var words = new int[BrickRecordLayout.BRICK_DATA_LENGTH];
                if (cube)
                {
                    words[0] = BrickRecordLayout.PackBrickInfo(0, 255);
                    words[1] = BrickRecordLayout.PackBrickTightBounds(new int3(0), new int3(7));
                    for (int i = 2; i < BrickRecordLayout.BRICK_BLOCK_DATA_OFFSET; ++i) words[i] = -1;
                    int pair = unchecked((int)(materialId | (materialId << 16)));
                    for (int i = BrickRecordLayout.BRICK_BLOCK_DATA_OFFSET; i < words.Length; ++i) words[i] = pair;
                    var bounds = Buffer(1, 24);
                    bounds.SetData(new[] { new BrickRecordLayout.BrickAABB { min = Vector3.zero, max = Vector3.one * 8 } });
                    var instanceMaterial = AssetDatabase.LoadAssetAtPath<Material>("Packages/ink.irylia.caelix/Runtime/Resources/Caelix_AabbInstance.mat");
                    Assert.That(instanceMaterial, Is.Not.Null);
                    int slot = Instances.Allocate();
                    Instances.Set(slot, Matrix4x4.identity, 0, 0, 1);
                    Token = Instances.GetLifetimeToken(slot);
                    Acceleration.AddInstance(new RayTracingAABBsInstanceConfig(bounds, 1, false, instanceMaterial), Matrix4x4.identity, (uint)slot);
                }
                Brick.SetData(words);
                Instances.Flush();
                if (State.Settings.UsesGroupTable)
                {
                    State.EmissionGroups = Buffer(1, 80);
                    State.EmissionWeights = Buffer(1, 32);
                    State.EmissionTree = Buffer(2, 4);
                    State.EmissionLeafCount = 1;
                    State.EmissionGroupCount = cube ? 1 : 0;
                    State.EmissionBrickCount = cube && State.Settings.UsesEmitterSampling ? 1 : 0;
                    State.EmissionGroups.SetData(new[]
                    {
                        new CaelixGiEmissionGroup
                        {
                            row0 = new Vector4(1, 0, 0, 0), row1 = new Vector4(0, 1, 0, 0), row2 = new Vector4(0, 0, 1, 0),
                            brickCount = cube ? 1u : 0u, groupToken = Token
                        }
                    });
                }
            }

            private ComputeShader Shader(string name)
            {
                var source = AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderRoot + name);
                Assert.That(source, Is.Not.Null, name);
                var shader = Object.Instantiate(source);
                shaders.Add(shader);
                return shader;
            }

            private GraphicsBuffer Buffer(int count, int stride)
            {
                var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, stride);
                auxiliary.Add(buffer);
                return buffer;
            }

            private void BindCommon(CommandBuffer cmd, ComputeShader shader, int kernel)
            {
                foreach (string keyword in RenderGroupPresets.Keywords) shader.DisableKeyword(keyword);
                var pages = new GraphicsBuffer[CaelixBrickPool.MaxNamedPages];
                Array.Fill(pages, Brick);
                CaelixRayQueryDispatch.BindSceneInputs(cmd, shader, kernel, Acceleration, pages, Instances.Buffer, Materials);
                cmd.SetComputeIntParams(shader, "g_GiResolution", Size, Size);
                cmd.SetComputeIntParam(shader, "g_GiFrameIndex", State.Frame);
                cmd.SetComputeIntParam(shader, "g_GiSamplesPerPixel", State.Settings.samplesPerPixel);
                cmd.SetComputeIntParam(shader, "g_GiMaxBounces", State.Settings.maxBounces);
                cmd.SetComputeFloatParam(shader, "g_GiSkyIntensity", 1);
                cmd.SetComputeFloatParam(shader, "g_GiGuidingStrength", 0.5f);
                cmd.SetComputeFloatParam(shader, "g_GiZoom", 0.25f);
                cmd.SetComputeFloatParam(shader, "g_GiAspect", 1);
                cmd.SetComputeVectorParam(shader, "g_GiCameraPosition", CameraPosition);
                cmd.SetComputeVectorParam(shader, "g_GiJitter", Vector4.zero);
                cmd.SetComputeMatrixParam(shader, "g_GiCameraToWorld", Matrix4x4.Translate(CameraPosition));
                cmd.SetComputeMatrixParam(shader, "g_GiWorldToCamera", Matrix4x4.Translate(-CameraPosition));
                cmd.SetComputeTextureParam(shader, kernel, "g_Sky", Sky);
                cmd.SetComputeBufferParam(shader, kernel, "g_GiSurfaces", State.Surfaces);
                cmd.SetComputeBufferParam(shader, kernel, "g_GiSurfacesOut", State.Surfaces);
                cmd.SetComputeTextureParam(shader, kernel, "g_GiOutputColor", State.RawColor);
                cmd.SetComputeTextureParam(shader, kernel, "g_GiOutputDepth", State.Depth);
            }

            private void BindCache(CommandBuffer cmd, ComputeShader shader, int kernel)
            {
                cmd.SetComputeIntParam(shader, "g_GiCacheCapacity", State.Settings.cacheCapacity);
                cmd.SetComputeIntParam(shader, "g_GiCacheFrame", State.Frame);
                cmd.SetComputeIntParam(shader, "g_GiCacheMaxAge", 120);
                cmd.SetComputeIntParam(shader, "g_GiCacheHistoryLimit", 64);
                cmd.SetComputeIntParam(shader, "g_GiCacheMinSamples", 1);
                cmd.SetComputeBufferParam(shader, kernel, "g_GiCacheKeys", State.CacheKeys);
                cmd.SetComputeBufferParam(shader, kernel, "g_GiCacheHistory", State.CacheHistory);
                cmd.SetComputeBufferParam(shader, kernel, "g_GiCacheNextHistory", State.NextCacheHistory);
                cmd.SetComputeBufferParam(shader, kernel, "g_GiCacheAccum", State.CacheAccum);
            }

            private void BindEmission(CommandBuffer cmd, ComputeShader shader, int kernel)
            {
                cmd.SetComputeIntParam(shader, "g_GiEmissionGroupCount", State.EmissionGroupCount);
                cmd.SetComputeIntParam(shader, "g_GiEmissionBrickCount", State.EmissionBrickCount);
                cmd.SetComputeIntParam(shader, "g_GiEmissionLeafCount", State.EmissionLeafCount);
                cmd.SetComputeBufferParam(shader, kernel, "g_GiEmissionGroups", State.EmissionGroups);
                cmd.SetComputeBufferParam(shader, kernel, "g_GiEmissionVoxelWeights", State.EmissionWeights);
                cmd.SetComputeBufferParam(shader, kernel, "g_GiEmissionTree", State.EmissionTree);
            }

            private void BindSkin(CommandBuffer cmd, ComputeShader shader, int kernel)
            {
                var settings = State.Settings;
                cmd.SetComputeIntParam(shader, "g_GiSkinCapacity", settings.skinBrickCapacity);
                cmd.SetComputeIntParam(shader, "g_GiSkinTrainingBudget", settings.skinTrainingBudget);
                cmd.SetComputeIntParam(shader, "g_GiSkinWalks", settings.skinWalksPerPixel);
                cmd.SetComputeIntParam(shader, "g_GiSkinWalkBounces", settings.skinWalkBounces);
                cmd.SetComputeIntParam(shader, "g_GiSkinTrainingRays", settings.skinTrainingRays);
                cmd.SetComputeIntParam(shader, "g_GiSkinEmitterSampling", settings.skinEmitterSampling ? 1 : 0);
                cmd.SetComputeIntParam(shader, "g_GiSkinFrame", State.Frame);
                cmd.SetComputeIntParam(shader, "g_GiSkinMaxAge", 120);
                cmd.SetComputeIntParam(shader, "g_GiSkinHistoryLimit", 64);
                cmd.SetComputeIntParam(shader, "g_GiSkinMinSamples", 1);
                cmd.SetComputeBufferParam(shader, kernel, "g_GiSkinBricks", State.SkinBricks);
                cmd.SetComputeBufferParam(shader, kernel, "g_GiSkinTexels", State.SkinTexels);
                cmd.SetComputeBufferParam(shader, kernel, "g_GiSkinMarks", State.SkinMarks);
                cmd.SetComputeBufferParam(shader, kernel, "g_GiSkinTouched", State.SkinTouched);
                cmd.SetComputeBufferParam(shader, kernel, "g_GiSkinControl", State.SkinControl);
            }

            private void BindResampling(CommandBuffer cmd, int kernel)
            {
                cmd.SetComputeIntParam(Resampling, "g_GiResetHistory", State.Frame == 0 ? 1 : 0);
                cmd.SetComputeIntParam(Resampling, "g_GiNaadfHistoryValid", State.Frame == 0 ? 0 : 1);
                cmd.SetComputeIntParam(Resampling, "g_GiSpatialSamples", 2);
                cmd.SetComputeIntParam(Resampling, "g_GiSpatialRadius", 2);
                cmd.SetComputeIntParam(Resampling, "g_GiMaxHistoryFrames", 8);
                cmd.SetComputeIntParam(Resampling, "g_GiMaxReservoirCount", 32);
                cmd.SetComputeMatrixParam(Resampling, "g_GiPreviousWorldToCamera", Matrix4x4.Translate(-CameraPosition));
                cmd.SetComputeVectorParam(Resampling, "g_GiPreviousJitter", Vector4.zero);
                cmd.SetComputeBufferParam(Resampling, kernel, "g_GiPreviousSurfaces", State.PreviousSurfaces);
                cmd.SetComputeBufferParam(Resampling, kernel, "g_GiCandidates", State.Candidates);
                cmd.SetComputeTextureParam(Resampling, kernel, "g_GiFallback", State.Fallback);
                if (State.TemporalReservoirs != null)
                {
                    cmd.SetComputeBufferParam(Resampling, kernel, "g_GiTemporalReservoirs", State.TemporalReservoirs);
                    cmd.SetComputeBufferParam(Resampling, kernel, "g_GiReservoirHistory", State.ReservoirHistory);
                    cmd.SetComputeBufferParam(Resampling, kernel, "g_GiReservoirOutput", State.Candidates);
                }
                if (State.Buckets != null)
                {
                    cmd.SetComputeBufferParam(Resampling, kernel, "g_GiBuckets", State.Buckets);
                    cmd.SetComputeBufferParam(Resampling, kernel, "g_GiPreviousBuckets", State.PreviousBuckets);
                    cmd.SetComputeBufferParam(Resampling, kernel, "g_GiBucketSamples", State.BucketSamples);
                    cmd.SetComputeBufferParam(Resampling, kernel, "g_GiPreviousBucketSamples", State.PreviousBucketSamples);
                }
            }

            private void Dispatch(CommandBuffer cmd, ComputeShader shader, string name)
            {
                int kernel = shader.FindKernel(name);
                BindCommon(cmd, shader, kernel);
                if (shader == Resampling) BindResampling(cmd, kernel);
                if (shader == CachePath)
                {
                    BindCache(cmd, shader, kernel);
                    if (State.EmissionGroups != null) BindEmission(cmd, shader, kernel);
                }
                if (shader == Skin)
                {
                    BindSkin(cmd, shader, kernel);
                    BindEmission(cmd, shader, kernel);
                }
                cmd.DispatchCompute(shader, kernel, 1, 1, 1);
            }

            private void TrainSkin(CommandBuffer cmd)
            {
                int prepare = Skin.FindKernel("GiSkinPrepareTraining");
                BindSkin(cmd, Skin, prepare);
                cmd.DispatchCompute(Skin, prepare, 1, 1, 1);
                int train = Skin.FindKernel("GiSkinTrain");
                BindCommon(cmd, Skin, train);
                BindSkin(cmd, Skin, train);
                BindEmission(cmd, Skin, train);
                cmd.DispatchCompute(Skin, train, State.SkinControl, 16);
                int expire = Skin.FindKernel("GiSkinExpire");
                BindSkin(cmd, Skin, expire);
                cmd.DispatchCompute(Skin, expire, (State.Settings.skinBrickCapacity + 63) / 64, 1, 1);
            }

            public void Render()
            {
                using var cmd = new CommandBuffer();
                cmd.BuildRayTracingAccelerationStructure(Acceleration);
                cmd.SetRenderTarget(State.RawColor);
                cmd.ClearRenderTarget(false, true, new Color(-1, -1, -1, -1));
                if (State.Settings.UsesCache && State.Frame == 0)
                {
                    int clear = Cache.FindKernel("ClearGiCache");
                    BindCache(cmd, Cache, clear);
                    cmd.DispatchCompute(Cache, clear, State.Settings.cacheCapacity / 64, 1, 1);
                    cmd.SetComputeBufferParam(Cache, clear, "g_GiCacheNextHistory", State.CacheHistory);
                    cmd.DispatchCompute(Cache, clear, State.Settings.cacheCapacity / 64, 1, 1);
                }
                if (State.Settings.UsesSkin && State.Frame == 0)
                {
                    int clear = Skin.FindKernel("GiSkinClear");
                    BindSkin(cmd, Skin, clear);
                    cmd.DispatchCompute(Skin, clear, (State.Settings.skinBrickCapacity + 63) / 64, 1, 1);
                }
                if (State.Settings.UsesEmitterSampling && State.Frame == 0)
                {
                    int clear = Emission.FindKernel("ClearGiEmission");
                    BindEmission(cmd, Emission, clear);
                    cmd.DispatchCompute(Emission, clear, 1, 1, 1);
                    if (State.EmissionBrickCount > 0)
                    {
                        int build = Emission.FindKernel("BuildGiEmission");
                        BindCommon(cmd, Emission, build);
                        BindEmission(cmd, Emission, build);
                        cmd.SetComputeIntParam(Emission, "g_GiEmissionBuildOffset", 0);
                        cmd.SetComputeIntParam(Emission, "g_GiEmissionBuildCount", 1);
                        cmd.DispatchCompute(Emission, build, 1, 1, 1);
                    }
                }
                Dispatch(cmd, Reference, "GiGenerateSurfaces");
                switch (State.Settings.approach)
                {
                    case CaelixGiApproach.ReferencePathTracing: Dispatch(cmd, Reference, "GiReference"); break;
                    case CaelixGiApproach.RestirGi:
                        Dispatch(cmd, Resampling, "GiGenerateCandidates");
                        Dispatch(cmd, Resampling, "GiRestirTemporal");
                        Dispatch(cmd, Resampling, "GiRestirSpatial");
                        break;
                    case CaelixGiApproach.NaadfInspired:
                        Dispatch(cmd, Resampling, "GiGenerateCandidates");
                        Dispatch(cmd, Resampling, "GiNaadfBuildBuckets");
                        Dispatch(cmd, Resampling, "GiNaadfSpatial");
                        break;
                    case CaelixGiApproach.FaceRadianceCache: Dispatch(cmd, CachePath, "TraceRadianceCache"); break;
                    case CaelixGiApproach.FacePathGuiding: Dispatch(cmd, CachePath, "TraceGuidedPath"); break;
                    case CaelixGiApproach.BrickEmissionPathGuiding: Dispatch(cmd, CachePath, "TraceEmissionGuidedPath"); break;
                    case CaelixGiApproach.BrickSkinIrradiance:
                        Dispatch(cmd, Skin, "GiSkinShade");
                        TrainSkin(cmd);
                        break;
                }
                if (State.Settings.UsesCache)
                {
                    int resolve = Cache.FindKernel("ResolveGiCache");
                    BindCache(cmd, Cache, resolve);
                    cmd.DispatchCompute(Cache, resolve, State.Settings.cacheCapacity / 64, 1, 1);
                }
                Graphics.ExecuteCommandBuffer(cmd);
            }

            public Color[] ReadColor()
            {
                var request = AsyncGPUReadback.Request(State.RawColor.rt, 0, TextureFormat.RGBAFloat);
                request.WaitForCompletion();
                Assert.That(request.hasError, Is.False);
                return request.GetData<Color>().ToArray();
            }

            public void Dispose()
            {
                Acceleration.Dispose();
                State.Dispose();
                Instances.Dispose();
                Brick.Dispose();
                Materials.Dispose();
                foreach (var buffer in auxiliary) buffer.Dispose();
                foreach (var shader in shaders) Object.DestroyImmediate(shader);
                Object.DestroyImmediate(Sky);
            }
        }

        [SetUp]
        public void RequireInlineRayTracing()
        {
            if (!SystemInfo.supportsRayTracing || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
                Assert.Ignore("The prototype kernels require D3D12 ray tracing.");
            if (!SystemInfo.supportsAsyncGPUReadback) Assert.Ignore("GPU readback unavailable.");
        }

        [TestCase(CaelixGiApproach.ReferencePathTracing)]
        [TestCase(CaelixGiApproach.RestirGi)]
        [TestCase(CaelixGiApproach.NaadfInspired)]
        [TestCase(CaelixGiApproach.FaceRadianceCache)]
        [TestCase(CaelixGiApproach.FacePathGuiding)]
        [TestCase(CaelixGiApproach.BrickEmissionPathGuiding)]
        [TestCase(CaelixGiApproach.BrickSkinIrradiance)]
        public void EmptySceneWritesExpectedSkyInEveryPixel(CaelixGiApproach approach)
        {
            using var fixture = new Fixture(approach, false, 2);
            fixture.Render();
            AssertColors(fixture.ReadColor(), fixture.SkyColor);
            var surfaces = new SurfaceRecord[Size * Size];
            fixture.State.Surfaces.GetData(surfaces);
            foreach (var surface in surfaces) Assert.That(surface.flags & 1u, Is.Zero);
            var depthReadback = AsyncGPUReadback.Request(fixture.State.Depth.rt, 0);
            depthReadback.WaitForCompletion();
            Assert.That(depthReadback.hasError, Is.False);
            foreach (float depth in depthReadback.GetData<float>())
                Assert.That(depth, Is.Zero, "Primary misses use the far-plane sentinel, independent of trace distance");
        }

        [TestCase(CaelixGiApproach.ReferencePathTracing)]
        [TestCase(CaelixGiApproach.RestirGi)]
        [TestCase(CaelixGiApproach.NaadfInspired)]
        [TestCase(CaelixGiApproach.FaceRadianceCache)]
        [TestCase(CaelixGiApproach.FacePathGuiding)]
        [TestCase(CaelixGiApproach.BrickEmissionPathGuiding)]
        [TestCase(CaelixGiApproach.BrickSkinIrradiance)]
        public void ZeroBounceEmissiveCubeWritesExactEmissionAndFaceIdentity(CaelixGiApproach approach)
        {
            using var fixture = new Fixture(approach, true, 0);
            fixture.Render();
            Assert.That(fixture.ExpectedEmission.r + fixture.ExpectedEmission.g + fixture.ExpectedEmission.b, Is.GreaterThan(0));
            AssertColors(fixture.ReadColor(), fixture.ExpectedEmission);
            var surfaces = new SurfaceRecord[Size * Size];
            fixture.State.Surfaces.GetData(surfaces);
            foreach (var surface in surfaces)
            {
                Assert.That(surface.flags & 1u, Is.EqualTo(1));
                Assert.That(surface.token, Is.EqualTo(fixture.Token));
                Assert.That(surface.voxelFace >> 28, Is.EqualTo(4), "Front face points along +Z");
                Assert.That((surface.voxelFace >> 19) & 511, Is.EqualTo(7));
                Assert.That(surface.depth, Is.EqualTo(4).Within(0.001f));
                Assert.That(surface.position.z, Is.EqualTo(8).Within(0.001f));
            }
            fixture.State.FinishFrame(Matrix4x4.Translate(-fixture.CameraPosition), 0.25f, Vector2.zero);
            fixture.Render();
            AssertColors(fixture.ReadColor(), fixture.ExpectedEmission);
            if (approach == CaelixGiApproach.BrickEmissionPathGuiding)
            {
                var tree = new float[2];
                fixture.State.EmissionTree.GetData(tree);
                Color emission = fixture.ExpectedEmission;
                float luminance = emission.r * 0.2126f + emission.g * 0.7152f + emission.b * 0.0722f;
                Assert.That(tree[1], Is.EqualTo(512 * 6 * luminance).Within(0.01f), "Emitter hierarchy includes every voxel's six proposal faces");
            }
        }

        [TestCase(CaelixGiApproach.ReferencePathTracing)]
        [TestCase(CaelixGiApproach.RestirGi)]
        [TestCase(CaelixGiApproach.NaadfInspired)]
        [TestCase(CaelixGiApproach.FaceRadianceCache)]
        [TestCase(CaelixGiApproach.FacePathGuiding)]
        [TestCase(CaelixGiApproach.BrickEmissionPathGuiding)]
        [TestCase(CaelixGiApproach.BrickSkinIrradiance)]
        public void ScatteringAndHistoryProduceFiniteNonblankLighting(CaelixGiApproach approach)
        {
            using var fixture = new Fixture(approach, true, 2);
            for (int frame = 0; frame < 4; ++frame)
            {
                fixture.Render();
                Color[] colors = fixture.ReadColor();
                float average = 0;
                foreach (Color color in colors)
                {
                    for (int channel = 0; channel < 3; ++channel)
                        Assert.That(color[channel], Is.InRange(0f, 100f), $"frame {frame}, channel {channel}");
                    average += color.r + color.g + color.b;
                }
                Color emission = fixture.ExpectedEmission;
                Assert.That(average / colors.Length, Is.GreaterThan(emission.r + emission.g + emission.b + 0.01f), "Scattered sky light must be present");
                fixture.State.FinishFrame(Matrix4x4.Translate(-fixture.CameraPosition), 0.25f, Vector2.zero);
            }
        }

        [TestCase(CaelixGiApproach.FaceRadianceCache)]
        [TestCase(CaelixGiApproach.FacePathGuiding)]
        [TestCase(CaelixGiApproach.BrickEmissionPathGuiding)]
        public void CacheKernelsOnlyTrainTheirSelectedEstimator(CaelixGiApproach approach)
        {
            using var fixture = new Fixture(approach, true, 2);
            fixture.Render();
            var request = AsyncGPUReadback.Request(fixture.State.NextCacheHistory);
            request.WaitForCompletion();
            Assert.That(request.hasError, Is.False);
            var words = request.GetData<uint>();
            uint guideSamples = 0;
            for (int slot = 0; slot < fixture.State.Settings.cacheCapacity; ++slot)
            {
                uint radianceCountBits = words[slot * 16 + 3];
                uint guideCount = words[slot * 16 + 14];
                guideSamples += guideCount;
                if (approach == CaelixGiApproach.FaceRadianceCache)
                    Assert.That(guideCount, Is.Zero, "Radiance caching must not also trace and train the guiding estimator.");
                else
                    Assert.That(radianceCountBits, Is.Zero, "Guiding must not also train the radiance estimator.");
            }
            if (approach != CaelixGiApproach.FaceRadianceCache)
                Assert.That(guideSamples, Is.GreaterThan(0), "The selected guiding estimator must run.");
        }

        [Test]
        public void ResizeKeepsBuiltEmissionData()
        {
            using var fixture = new Fixture(CaelixGiApproach.BrickEmissionPathGuiding, true, 0);
            fixture.State.Settings.samplesPerPixel = 1;
            fixture.Render();
            fixture.State.FinishFrame(Matrix4x4.Translate(-fixture.CameraPosition), 0.25f, Vector2.zero);
            var groups = fixture.State.EmissionGroups;
            var weights = fixture.State.EmissionWeights;
            var tree = fixture.State.EmissionTree;
            var before = new float[2];
            tree.GetData(before);
            Assert.That(before[1], Is.GreaterThan(0));
            fixture.State.Resize(Size * 2, Size);
            Assert.That(fixture.State.EmissionGroups, Is.SameAs(groups));
            Assert.That(fixture.State.EmissionWeights, Is.SameAs(weights));
            Assert.That(fixture.State.EmissionTree, Is.SameAs(tree));
            Assert.That(fixture.State.EmissionBrickCount, Is.EqualTo(1));
            Assert.That(fixture.State.ValidLightingHistory, Is.True);
            Assert.That(fixture.State.ValidHistory, Is.False);
            var after = new float[2];
            tree.GetData(after);
            Assert.That(after, Is.EqualTo(before));
        }

        [Test]
        public void RadianceCacheTrainsVisibleFacesWithOneBounceAndOneSample()
        {
            using var fixture = new Fixture(CaelixGiApproach.FaceRadianceCache, true, 1);
            fixture.State.Settings.samplesPerPixel = 1;
            int previousSamples = 0;
            for (int frame = 0; frame < 2; frame++)
            {
                fixture.Render();
                var request = AsyncGPUReadback.Request(fixture.State.NextCacheHistory);
                request.WaitForCompletion();
                Assert.That(request.hasError, Is.False);
                int samples = 0;
                foreach (var record in request.GetData<CacheHistoryRecord>())
                {
                    if (record.irradiance.w <= 0) continue;
                    samples += (int)record.irradiance.w;
                    Assert.That(record.irradiance.x, Is.EqualTo(fixture.SkyColor.r).Within(0.004f));
                    Assert.That(record.irradiance.y, Is.EqualTo(fixture.SkyColor.g).Within(0.004f));
                    Assert.That(record.irradiance.z, Is.EqualTo(fixture.SkyColor.b).Within(0.004f));
                }
                // Threads may skip a face while another thread first claims its entry.
                if (frame == 0) Assert.That(samples, Is.GreaterThan(0));
                else Assert.That(samples - previousSamples, Is.EqualTo(Size * Size),
                    "Every primary diffuse sample trains an existing entry without a secondary hit.");
                previousSamples = samples;
                fixture.State.FinishFrame(Matrix4x4.Translate(-fixture.CameraPosition), 0.25f, Vector2.zero);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RadianceCacheQueriesVisibleFacesWithoutASecondaryHit(bool moveCamera)
        {
            using var fixture = new Fixture(CaelixGiApproach.FaceRadianceCache, true, 1);
            fixture.State.Settings.samplesPerPixel = 1;
            fixture.Render();
            fixture.State.FinishFrame(Matrix4x4.Translate(-fixture.CameraPosition), 0.25f, Vector2.zero);
            var records = new CacheHistoryRecord[fixture.State.Settings.cacheCapacity];
            fixture.State.CacheHistory.GetData(records);
            for (int i = 0; i < records.Length; i++)
                records[i].irradiance = new Vector4(4, 8, 16, 64);
            fixture.State.CacheHistory.SetData(records);
            if (moveCamera) fixture.CameraPosition += new Vector3(0.02f, 0, 0);
            fixture.Render();
            foreach (var color in fixture.ReadColor())
                Assert.That(color.r, Is.GreaterThan(fixture.ExpectedEmission.r + 1),
                    "The warm primary face must reuse diffuse lighting, not just resample the sky.");
        }

        [Test]
        public void BrickSkinTrainsTouchedTexelsFromSkyAndShadesFromThemNextFrame()
        {
            using var fixture = new Fixture(CaelixGiApproach.BrickSkinIrradiance, true, 1);
            fixture.State.Settings.samplesPerPixel = 1;
            fixture.Render();
            var control = new uint[CaelixGiSettings.SkinControlWords];
            fixture.State.SkinControl.GetData(control);
            Assert.That(control[0], Is.Zero, "The request list reopens after training is prepared.");
            Assert.That(control[1], Is.InRange(1, 64), "Front-face texels crossed by immediate exits are requested once each.");
            Assert.That(control[4], Is.EqualTo((control[1] + 63) / 64));

            var bricks = new SkinBrickRecord[fixture.State.Settings.skinBrickCapacity];
            fixture.State.SkinBricks.GetData(bricks);
            int live = 0;
            foreach (var brick in bricks)
            {
                if (brick.state == 0) continue;
                live++;
                Assert.That(brick.state, Is.EqualTo(2));
                Assert.That(brick.token, Is.EqualTo(fixture.Token));
                Assert.That(brick.brickIndex, Is.Zero);
            }
            Assert.That(live, Is.EqualTo(1), "Every visible face belongs to the single brick.");

            var texels = new uint[fixture.State.Settings.skinBrickCapacity * CaelixGiSettings.SkinTexelsPerBrick * 2];
            fixture.State.SkinTexels.GetData(texels);
            int warm = 0;
            for (int texel = 0; texel < texels.Length / 2; texel++)
            {
                uint low = texels[texel * 2], high = texels[texel * 2 + 1];
                if (low == 0 && high == 0) continue;
                warm++;
                Assert.That(texel % CaelixGiSettings.SkinTexelsPerBrick / 64, Is.EqualTo(4), "Only the +Z skin is seen");
                Assert.That(high >> 16, Is.EqualTo(fixture.State.Settings.skinTrainingRays), "Count equals the rays of one training");
                Vector3 irradiance = new(Mathf.HalfToFloat((ushort)(low & 0xffff)), Mathf.HalfToFloat((ushort)(low >> 16)),
                    Mathf.HalfToFloat((ushort)(high & 0xffff)));
                for (int channel = 0; channel < 3; channel++)
                    Assert.That(irradiance[channel], Is.EqualTo(Mathf.PI * fixture.SkyColor[channel]).Within(0.01f),
                        "Cosine rays into a uniform sky give irradiance pi times the sky radiance");
            }
            Assert.That(warm, Is.EqualTo((int)control[1]));

            fixture.State.FinishFrame(Matrix4x4.Translate(-fixture.CameraPosition), 0.25f, Vector2.zero);
            fixture.Render();
            Color expected = fixture.ExpectedEmission;
            for (int channel = 0; channel < 3; channel++) expected[channel] += fixture.ExpectedDiffuse[channel] * fixture.SkyColor[channel];
            foreach (var color in fixture.ReadColor())
                for (int channel = 0; channel < 3; channel++)
                    Assert.That(color[channel], Is.EqualTo(expected[channel]).Within(0.01f),
                        "A warm skin reproduces the diffuse sky bounce without noise");
        }

        private static void AssertColors(Color[] pixels, Color expected)
        {
            Assert.That(pixels.Length, Is.EqualTo(Size * Size));
            for (int i = 0; i < pixels.Length; ++i)
            {
                for (int channel = 0; channel < 4; ++channel)
                {
                    Assert.That(float.IsNaN(pixels[i][channel]) || float.IsInfinity(pixels[i][channel]), Is.False, $"pixel {i}, channel {channel}");
                    Assert.That(pixels[i][channel], Is.EqualTo(expected[channel]).Within(0.003f), $"pixel {i}, channel {channel}");
                }
            }
        }
    }
}
