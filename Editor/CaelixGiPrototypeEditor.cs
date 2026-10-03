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
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("settings"), true);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("referenceShader"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("resamplingShader"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("cacheShader"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("cachePathShader"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("emissionShader"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("resolveShader"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("presentShader"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("depthCopyShader"));
            }
            serializedObject.ApplyModifiedProperties();
            var feature = (CaelixGiPrototypeFeature)target;
            if (Application.isPlaying)
            {
                EditorGUILayout.LabelField("Active approach", feature.ActiveApproach.ToString());
                EditorGUILayout.LabelField("Allocated GPU storage", $"{feature.AllocatedBytes / (1024.0 * 1024.0):F1} MiB");
            }
        }
    }

    [InitializeOnLoad]
    public static class CaelixGiPrototypeSetup
    {
        private const string ShaderPath = "Packages/ink.irylia.caelix/Runtime/Rendering/Shaders/GiPrototypes/";

        static CaelixGiPrototypeSetup()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.EnteredEditMode) return;
                foreach (var feature in Resources.FindObjectsOfTypeAll<CaelixGiPrototypeFeature>())
                    feature.ReleaseResources();
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
            feature.resolveShader = Load("GiResolve.compute");
            feature.presentShader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath + "GiPresent.shader");
            feature.depthCopyShader = AssetDatabase.LoadAssetAtPath<Shader>(
                "Packages/com.unity.render-pipelines.universal/Shaders/Utils/CopyDepth.shader");
        }

        private static ComputeShader Load(string name) => AssetDatabase.LoadAssetAtPath<ComputeShader>(ShaderPath + name);
    }
}
