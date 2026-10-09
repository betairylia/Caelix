using System;
using Caelix.Rendering.GiPrototypes;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Caelix.EditorTools
{
    [CustomEditor(typeof(CaelixGiPrototypeFeature))]
    public sealed class CaelixGiPrototypeEditor : UnityEditor.Editor
    {
        private SerializedProperty settings;
        private SerializedProperty[] shaders;
        private bool showShaders;

        private void OnEnable()
        {
            settings = serializedObject.FindProperty("settings");
            shaders = new[]
            {
                serializedObject.FindProperty("referenceShader"),
                serializedObject.FindProperty("resamplingShader"),
                serializedObject.FindProperty("cacheShader"),
                serializedObject.FindProperty("cachePathShader"),
                serializedObject.FindProperty("emissionShader"),
                serializedObject.FindProperty("skinShader"),
                serializedObject.FindProperty("resolveShader"),
                serializedObject.FindProperty("presentShader"),
                serializedObject.FindProperty("depthCopyShader")
            };
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var feature = (CaelixGiPrototypeFeature)target;
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                var approach = (CaelixGiApproach)EditorGUILayout.EnumPopup("Approach", feature.settings.approach);
                if (approach != feature.settings.approach)
                {
                    serializedObject.ApplyModifiedProperties();
                    Undo.RecordObject(feature, "Change GI approach");
                    feature.SelectApproach(approach);
                    EditorUtility.SetDirty(feature);
                    serializedObject.Update();
                }

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Tracing", EditorStyles.boldLabel);
                DrawSetting("resolutionScale");
                DrawSetting("samplesPerPixel");
                DrawSetting("maxBounces");
                DrawSetting("skyIntensity");
                DrawSetting("accumulationFrames");

                if (feature.settings.UsesReservoirs)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Sample Reuse", EditorStyles.boldLabel);
                    DrawSetting("spatialSamples");
                    DrawSetting("spatialRadius");
                    DrawSetting("reservoirHistoryFrames");
                    if (approach == CaelixGiApproach.RestirGi) DrawSetting("maxReservoirCount");
                }
                if (feature.settings.UsesCache)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Face Cache", EditorStyles.boldLabel);
                    DrawSetting("cacheCapacity");
                    DrawSetting("cacheHistorySamples");
                    DrawSetting("cacheMinSamples");
                    DrawSetting("cacheMaxAge");
                }
                if (feature.settings.UsesGuiding) DrawSetting("guidingStrength");
                if (feature.settings.UsesSkin)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Brick Skin", EditorStyles.boldLabel);
                    DrawSetting("skinBrickCapacity");
                    DrawSetting("skinWalksPerPixel");
                    DrawSetting("skinWalkBounces");
                    DrawSetting("skinTrainingRays");
                    DrawSetting("skinTrainingWalks");
                    DrawSetting("skinColdBounces");
                    DrawSetting("skinTrainingBudget");
                    DrawSetting("skinEmitterSampling");
                    DrawSetting("cacheHistorySamples");
                    DrawSetting("cacheMinSamples");
                    DrawSetting("cacheMaxAge");
                }

                EditorGUILayout.Space();
                if (GUILayout.Button("Apply Recommended Defaults"))
                {
                    serializedObject.ApplyModifiedProperties();
                    Undo.RecordObject(feature, "Reset GI approach settings");
                    feature.ApplyRecommendedSettings();
                    EditorUtility.SetDirty(feature);
                    serializedObject.Update();
                }

                showShaders = EditorGUILayout.Foldout(showShaders, "Shader References", true);
                if (showShaders)
                    foreach (var shader in shaders) EditorGUILayout.PropertyField(shader);
            }
            serializedObject.ApplyModifiedProperties();
            if (Application.isPlaying)
            {
                EditorGUILayout.LabelField("Active approach", feature.ActiveApproach.ToString());
                EditorGUILayout.LabelField("Allocated GPU storage", $"{feature.AllocatedBytes / (1024.0 * 1024.0):F1} MiB");
            }
        }

        private void DrawSetting(string name) => EditorGUILayout.PropertyField(settings.FindPropertyRelative(name));
    }

    [InitializeOnLoad]
    public static class CaelixGiPrototypeSetup
    {
        private const string ShaderPath = "Packages/ink.irylia.caelix/Runtime/Rendering/Shaders/GiPrototypes/";

        static CaelixGiPrototypeSetup()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.EnteredEditMode
                    && state != PlayModeStateChange.ExitingEditMode) return;
                foreach (var feature in Resources.FindObjectsOfTypeAll<CaelixGiPrototypeFeature>())
                    feature.ResetSession();
            };
        }

        [MenuItem("Tools/Caelix/GI Prototypes/Install On Selected Renderer")]
        private static void InstallSelected() => InstallInto((ScriptableRendererData)Selection.activeObject);

        [MenuItem("Tools/Caelix/GI Prototypes/Install On Selected Renderer", true)]
        private static bool CanInstall() => !EditorApplication.isPlayingOrWillChangePlaymode
            && Selection.activeObject is ScriptableRendererData;

        public static CaelixGiPrototypeFeature InstallInto(ScriptableRendererData renderer)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Configure GI prototypes before entering Play Mode.");
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            CaelixGiPrototypeFeature prototype = null;
            Undo.RecordObject(renderer, "Install GI prototypes");
            foreach (var feature in renderer.rendererFeatures)
            {
                if (feature is CaelixGiPrototypeFeature existing) prototype = existing;
                if (feature is CaelixRendererFeature || feature is CaelixBudgetRendererFeature)
                {
                    Undo.RecordObject(feature, "Disable previous Caelix renderer");
                    feature.SetActive(false);
                    EditorUtility.SetDirty(feature);
                }
            }
            if (prototype == null)
            {
                prototype = ScriptableObject.CreateInstance<CaelixGiPrototypeFeature>();
                prototype.name = "Caelix GI Prototypes";
                prototype.hideFlags |= HideFlags.HideInHierarchy;
                Undo.RegisterCreatedObjectUndo(prototype, "Install GI prototypes");
                AssetDatabase.AddObjectToAsset(prototype, renderer);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(prototype, out string _, out long localId);
                var serialized = new SerializedObject(renderer);
                var features = serialized.FindProperty("m_RendererFeatures");
                var map = serialized.FindProperty("m_RendererFeatureMap");
                int index = features.arraySize++;
                features.GetArrayElementAtIndex(index).objectReferenceValue = prototype;
                map.arraySize = features.arraySize;
                map.GetArrayElementAtIndex(index).longValue = localId;
                serialized.ApplyModifiedProperties();
            }
            AssignShaders(prototype);
            prototype.SetActive(true);
            prototype.Create();
            EditorUtility.SetDirty(prototype);
            EditorUtility.SetDirty(renderer);
            renderer.SetDirty();
            AssetDatabase.SaveAssets();
            return prototype;
        }

        public static void AssignShaders(CaelixGiPrototypeFeature feature)
        {
            feature.referenceShader = Load("GiReference.compute");
            feature.resamplingShader = Load("GiResampling.compute");
            feature.cacheShader = Load("GiCache.compute");
            feature.cachePathShader = Load("GiCachePath.compute");
            feature.emissionShader = Load("GiEmission.compute");
            feature.skinShader = Load("GiSkin.compute");
            feature.resolveShader = Load("GiResolve.compute");
            feature.presentShader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath + "GiPresent.shader");
            feature.depthCopyShader = AssetDatabase.LoadAssetAtPath<Shader>(
                "Packages/com.unity.render-pipelines.universal/Shaders/Utils/CopyDepth.shader");
        }

        private static ComputeShader Load(string name) => AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath + name);
    }
}
