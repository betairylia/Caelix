using Caelix.Rendering.RayQuery;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Caelix.Rendering.GiPrototypes
{
    [DisallowMultipleRendererFeature("Caelix GI Prototypes")]
    public sealed class CaelixGiPrototypeFeature : ScriptableRendererFeature
    {
        public CaelixGiSettings settings = new();
        public ComputeShader referenceShader;
        public ComputeShader resamplingShader;
        public ComputeShader cacheShader;
        public ComputeShader cachePathShader;
        public ComputeShader emissionShader;
        public ComputeShader resolveShader;
        public Shader presentShader;
        public Shader depthCopyShader;

        private static int playSession;
        private int capturedSession = -1;
        private CaelixGiSettings capturedSettings;
        private CaelixGiPrototypePass pass;
        private Material presentMaterial;
        private CaelixRayQueryRenderer scene;
        private bool warned;

        public CaelixGiApproach ActiveApproach => capturedSettings?.approach ?? settings.approach;
        public long AllocatedBytes => pass?.AllocatedBytes ?? 0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void BeginSession() => playSession++;

        public override void Create()
        {
            ReleaseResources();
            if (presentShader != null) presentMaterial = CoreUtils.CreateEngineMaterial(presentShader);
            pass = new CaelixGiPrototypePass(this, presentMaterial)
            {
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!Application.isPlaying || renderingData.cameraData.cameraType != CameraType.Game
                || renderingData.cameraData.renderType != CameraRenderType.Base) return;
            CaptureSessionSettings(playSession);
            var camera = renderingData.cameraData.camera;
            if (!SystemInfo.supportsInlineRayTracing || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12
                || camera.orthographic || camera.stereoEnabled || !ShadersReady())
            {
                if (!warned)
                {
                    Debug.LogWarning("Caelix GI prototypes require DX12 ray tracing, a perspective camera without XR, and all shader references.", this);
                    warned = true;
                }
                return;
            }
            if (scene == null || !scene.isActiveAndEnabled)
                scene = Object.FindAnyObjectByType<CaelixRayQueryRenderer>();
            if (scene == null || !scene.HasResources) return;
            if (pass == null) Create();
            pass.Configure(scene, capturedSettings);
            renderer.EnqueuePass(pass);
        }

        private bool ShadersReady() => referenceShader != null && resamplingShader != null && cacheShader != null
            && cachePathShader != null && emissionShader != null && resolveShader != null
            && presentShader != null && depthCopyShader != null;

        internal CaelixGiSettings CaptureSessionSettings(int session)
        {
            if (capturedSession != session || capturedSettings == null)
            {
                capturedSession = session;
                capturedSettings = settings.ValidatedCopy();
            }
            return capturedSettings;
        }

        public void ReleaseResources()
        {
            pass?.Dispose();
            pass = null;
            CoreUtils.Destroy(presentMaterial);
            presentMaterial = null;
        }

        protected override void Dispose(bool disposing) => ReleaseResources();
    }
}
