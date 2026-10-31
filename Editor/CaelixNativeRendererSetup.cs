using System;
using Caelix.Rendering.Native;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Caelix.EditorTools
{
    /// <summary>
    /// Editor menu that sets up the open scene for the native Vulkan renderer.
    /// </summary>
    /// <remarks>
    /// <c>Caelix/Native Renderer/Setup Scene</c>:
    /// <list type="number">
    /// <item>finds the <see cref="CaelixHost"/> of the open scene and adds a
    /// <see cref="CaelixNativeRenderer"/> to its GameObject when it has none (a new component gets
    /// <see cref="CaelixNativeRenderer.EncodingOverride.Packed"/>);</item>
    /// <item>assigns <see cref="DefaultMaterialsJsonPath"/> as the material JSON when the component
    /// has none and the asset exists;</item>
    /// <item>links the component in the host's <c>nativeRenderer</c> field;</item>
    /// <item>marks the scene dirty (save it yourself) and raises <see cref="SceneSetUp"/>, where the
    /// game selects the native renderer in its own options (the package does not know them).</item>
    /// </list>
    /// The renderer asset still needs the <see cref="CaelixNativeRendererFeature"/>, active, and the
    /// Editor must run on Vulkan.
    /// </remarks>
    public static class CaelixNativeRendererSetup
    {
        /// <summary>Material JSON assigned when the component has none (the Titania block-ID table).</summary>
        public const string DefaultMaterialsJsonPath = "Assets/Caelix/titania-materials.json";

        /// <summary>Raised after the scene was set up, with the scene's native renderer.</summary>
        public static event Action<CaelixNativeRenderer> SceneSetUp;

        [MenuItem("Caelix/Native Renderer/Setup Scene")]
        public static void SetupSceneMenu()
        {
            SetupScene();
        }

        /// <summary>Sets up the open scene (see the class remarks). Returns the component, or null without a host.</summary>
        public static CaelixNativeRenderer SetupScene()
        {
            CaelixHost host = Object.FindFirstObjectByType<CaelixHost>(FindObjectsInactive.Include);
            if (host == null)
            {
                Debug.LogError("Caelix native setup: the open scene has no CaelixHost; nothing was changed.");
                return null;
            }

            GameObject go = host.gameObject;
            bool added = false;
            if (!go.TryGetComponent(out CaelixNativeRenderer native))
            {
                native = Undo.AddComponent<CaelixNativeRenderer>(go);
                added = true;
            }

            Undo.RecordObject(native, "Setup Caelix native renderer");
            if (added)
            {
                native.Encoding = CaelixNativeRenderer.EncodingOverride.Packed;
            }
            if (native.MaterialsJson == null)
            {
                TextAsset materials = AssetDatabase.LoadAssetAtPath<TextAsset>(DefaultMaterialsJsonPath);
                if (materials != null) native.MaterialsJson = materials;
            }
            EditorUtility.SetDirty(native);

            var serializedHost = new SerializedObject(host);
            SerializedProperty link = serializedHost.FindProperty("nativeRenderer");
            if (link != null && link.objectReferenceValue != native)
            {
                link.objectReferenceValue = native;
                serializedHost.ApplyModifiedProperties();
            }

            EditorSceneManager.MarkSceneDirty(go.scene);
            Debug.Log($"Caelix native setup: {(added ? "added" : "kept")} CaelixNativeRenderer on '{go.name}' " +
                      $"(encoding {native.Encoding}, materials {(native.MaterialsJson != null ? native.MaterialsJson.name : "none")}). " +
                      "Save the scene to keep it.", native);
            SceneSetUp?.Invoke(native);
            return native;
        }
    }
}
