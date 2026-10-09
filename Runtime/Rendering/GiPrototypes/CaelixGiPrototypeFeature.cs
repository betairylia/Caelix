using System.Collections.Generic;
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
        public ComputeShader skinShader;
        public ComputeShader resolveShader;
        public Shader presentShader;
        public Shader depthCopyShader;

        [SerializeField, HideInInspector]
        private List<CaelixGiSettings> approachSettings = new();

        [System.NonSerialized]
        private CaelixGiSettings capturedSettings;
        private CaelixGiPrototypePass pass;
        private Material presentMaterial;
        private CaelixRayQueryRenderer scene;
        private bool warned;

        public CaelixGiApproach ActiveApproach => capturedSettings?.approach ?? settings.approach;
        public long AllocatedBytes => pass?.AllocatedBytes ?? 0;

        /// <summary>Stores the outgoing values and restores the selected approach's values.</summary>
        public void SelectApproach(CaelixGiApproach approach)
        {
            if (Application.isPlaying)
                throw new System.InvalidOperationException("Configure GI prototypes before entering Play Mode.");
            if (!System.Enum.IsDefined(typeof(CaelixGiApproach), approach))
                throw new System.ArgumentOutOfRangeException(nameof(approach));
            if (settings.approach == approach) return;

            approachSettings ??= new List<CaelixGiSettings>();
            int outgoing = approachSettings.FindIndex(value => value != null && value.approach == settings.approach);
            if (outgoing >= 0) approachSettings[outgoing] = settings.ValidatedCopy();
            else approachSettings.Add(settings.ValidatedCopy());
            var saved = approachSettings.Find(value => value != null && value.approach == approach);
            settings = saved?.ValidatedCopy() ?? CaelixGiSettings.Recommended(approach);
            ResetSession();
        }

        public void ApplyRecommendedSettings()
        {
            if (Application.isPlaying)
                throw new System.InvalidOperationException("Configure GI prototypes before entering Play Mode.");
            settings = CaelixGiSettings.Recommended(settings.approach);
            ResetSession();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void BeginSession()
        {
            foreach (var feature in Resources.FindObjectsOfTypeAll<CaelixGiPrototypeFeature>())
                feature.ResetSession();
        }

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
            CaptureSessionSettings();
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
            && cachePathShader != null && emissionShader != null && skinShader != null && resolveShader != null
            && presentShader != null && depthCopyShader != null;

        internal CaelixGiSettings CaptureSessionSettings()
        {
            if (capturedSettings == null)
                capturedSettings = settings.ValidatedCopy();
            return capturedSettings;
        }

        /// <summary>Clears the previous run so the next one captures the Inspector settings.</summary>
        public void ResetSession()
        {
            ReleaseResources();
            capturedSettings = null;
            scene = null;
            warned = false;
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
