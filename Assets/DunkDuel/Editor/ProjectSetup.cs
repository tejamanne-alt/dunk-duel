using System;
using System.Reflection;
using DunkDuel.Presentation;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
#if DD_URP
using UnityEngine.Rendering.Universal;
#endif

namespace DunkDuel.Editor
{
    /// <summary>
    /// One-time project configuration through Editor APIs (never hand-edited YAML): player settings for a
    /// landscape-only iOS/Android game, High/Mid/Low URP assets on the quality levels, TextMeshPro essentials,
    /// presentation material templates in Resources, and the single Boot scene in the build list.
    /// Batch mode: Unity -batchmode -quit -projectPath . -executeMethod DunkDuel.Editor.ProjectSetup.Run
    /// </summary>
    public static class ProjectSetup
    {
        public const string BootScenePath = "Assets/DunkDuel/Scenes/Boot.unity";
        public const string RenderingFolder = "Assets/DunkDuel/Rendering";
        public const string MaterialsFolder = "Assets/DunkDuel/Data/Resources/DunkDuelPresentation/Materials";

        [MenuItem("DunkDuel/Project Setup", false, 0)]
        public static void RunFromMenu()
        {
            Run();
            EditorUtility.DisplayDialog("Dunk Duel", "Project setup complete. If the Input System backend was just enabled, restart the Editor.", "OK");
        }

        public static void Run()
        {
            ConfigurePlayer();
            ConfigureInputHandling();
            ConfigureRendering();
            CreateMaterialTemplates();
            ImportTmpEssentials();
            CreateBootScene();
            SimCodeHash.Refresh(true);
            GoldenReplayStaging.Stage();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("Dunk Duel: project setup complete.");
        }

        // ------------------------------------------------------------------ player

        private static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "Dunk Duel";
            PlayerSettings.productName = "Dunk Duel";
            AppIdentity.Apply(true); // com.astradite.dunkduel, automatic signing with the Teja Manne team
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.iOS, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)35;
            PlayerSettings.iOS.targetOSVersionString = "15.0";
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Android, ManagedStrippingLevel.Medium);
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.iOS, ManagedStrippingLevel.Medium);
        }

        /// <summary>Both input backends: the Input System for gameplay and UI, legacy kept for fallbacks.</summary>
        private static void ConfigureInputHandling()
        {
            UnityEngine.Object[] settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (settings == null || settings.Length == 0) return;
            var so = new SerializedObject(settings[0]);
            SerializedProperty p = so.FindProperty("activeInputHandler");
            if (p == null || p.intValue == 2) return;
            p.intValue = 2;
            so.ApplyModifiedProperties();
            Debug.Log("Dunk Duel: active input handling set to Both. Restart the Editor for it to take effect.");
        }

        // ------------------------------------------------------------------ rendering

        private static void ConfigureRendering()
        {
            EnsureFolder(RenderingFolder);
            EnsureQualityLevels();
            KeepRuntimeFogVariants();
#if DD_URP
            string[] tiers = { "Low", "Mid", "High" };
            var assets = new UniversalRenderPipelineAsset[3];
            var template = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            for (int i = 0; i < tiers.Length; i++)
            {
                string path = RenderingFolder + "/DD_" + tiers[i] + "_URP.asset";
                var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
                if (asset == null)
                {
                    if (template != null)
                    {
                        // Copy the project's existing URP asset so renderer data and resources stay valid.
                        AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(template), path);
                        asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
                    }
                    else
                    {
                        var renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                        AssetDatabase.CreateAsset(renderer, RenderingFolder + "/DD_" + tiers[i] + "_Renderer.asset");
                        asset = UniversalRenderPipelineAsset.Create(renderer);
                        AssetDatabase.CreateAsset(asset, path);
                    }
                }
                QualityProfile q = QualityProfile.For((QualityTier)i);
                asset.renderScale = q.RenderScale;
                asset.msaaSampleCount = q.Msaa;
                asset.shadowDistance = i == 2 ? 30f : (i == 1 ? 20f : 0f);
                asset.supportsHDR = i > 0;
                EditorUtility.SetDirty(asset);
                assets[i] = asset;
            }
            GraphicsSettings.defaultRenderPipeline = assets[2];
#else
            Debug.LogWarning("Dunk Duel: URP package not found; using the built-in pipeline.");
#endif
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < 3 && i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
#if DD_URP
                QualitySettings.renderPipeline = assets[i];
#endif
                // Low drops imported art to LOD1 and half-resolution mips (2K sets become 1K); High holds LOD0 longer.
                QualitySettings.maximumLODLevel = i == 0 ? 1 : 0;
                QualitySettings.lodBias = i == 0 ? 0.7f : (i == 1 ? 1f : 1.5f);
                QualitySettings.globalTextureMipmapLimit = i == 0 ? 1 : 0;
                QualitySettings.vSyncCount = 0;
            }
            QualitySettings.SetQualityLevel(Mathf.Min(current, QualitySettings.names.Length - 1), false);
        }

        /// <summary>
        /// Courts switch exponential-squared fog on at runtime, but the Boot scene has none, so automatic fog
        /// stripping would drop those shader variants from player builds. Keep exactly that mode.
        /// </summary>
        private static void KeepRuntimeFogVariants()
        {
            UnityEngine.Object[] objs = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (objs == null || objs.Length == 0) return;
            var so = new SerializedObject(objs[0]);
            SetSerialized(so, "m_FogStripping", 1); // Custom
            SetSerialized(so, "m_FogKeepLinear", 0);
            SetSerialized(so, "m_FogKeepExp", 0);
            SetSerialized(so, "m_FogKeepExp2", 1);
            so.ApplyModifiedProperties();
        }

        private static void SetSerialized(SerializedObject so, string property, int value)
        {
            SerializedProperty p = so.FindProperty(property);
            if (p == null) return;
            if (p.propertyType == SerializedPropertyType.Boolean) p.boolValue = value != 0;
            else p.intValue = value;
        }

        /// <summary>Makes the first three quality levels Low, Mid, High (adds levels if needed).</summary>
        private static void EnsureQualityLevels()
        {
            UnityEngine.Object[] objs = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset");
            if (objs == null || objs.Length == 0) return;
            var so = new SerializedObject(objs[0]);
            SerializedProperty levels = so.FindProperty("m_QualitySettings");
            if (levels == null || !levels.isArray) return;
            string[] names = { "Low", "Mid", "High" };
            while (levels.arraySize < 3) levels.InsertArrayElementAtIndex(levels.arraySize > 0 ? levels.arraySize - 1 : 0);
            for (int i = 0; i < 3; i++)
            {
                SerializedProperty name = levels.GetArrayElementAtIndex(i).FindPropertyRelative("name");
                if (name != null) name.stringValue = names[i];
            }
            SerializedProperty current = so.FindProperty("m_CurrentQuality");
            if (current != null) current.intValue = 2;
            so.ApplyModifiedProperties();
        }

        // ------------------------------------------------------------------ materials

        /// <summary>Template materials in Resources guarantee the shaders ship in player builds.</summary>
        private static void CreateMaterialTemplates()
        {
            EnsureFolder(MaterialsFolder);
            bool urp = GraphicsSettings.defaultRenderPipeline != null;
            CreateMaterial("Lit", urp ? "Universal Render Pipeline/Lit" : "Standard", false, false);
            CreateMaterial("LitEmissive", urp ? "Universal Render Pipeline/Lit" : "Standard", false, false, true);
            CreateMaterial("Unlit", urp ? "Universal Render Pipeline/Unlit" : "Unlit/Color", false, false);
            CreateMaterial("Transparent", urp ? "Universal Render Pipeline/Particles/Unlit" : "Sprites/Default", true, false);
            CreateMaterial("Additive", urp ? "Universal Render Pipeline/Particles/Unlit" : "Sprites/Default", true, true);
            CreateMaterial("VertexColor", urp ? "Universal Render Pipeline/Particles/Unlit" : "Sprites/Default", false, false);
        }

        private static void CreateMaterial(string name, string shaderName, bool transparent, bool additive, bool emissive = false)
        {
            string path = MaterialsFolder + "/" + name + ".mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) return;
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogWarning("Dunk Duel: shader not found: " + shaderName);
                return;
            }
            var m = new Material(shader) { name = name, enableInstancing = true };
            if (transparent) MaterialLibrary.MakeTransparent(m, additive);
            if (emissive)
            {
                // Keeps the _EMISSION variant in player builds for materials that glow at runtime.
                MaterialLibrary.SetEmission(m, Color.black);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            AssetDatabase.CreateAsset(m, path);
        }

        // ------------------------------------------------------------------ text

        private static void ImportTmpEssentials()
        {
            if (AssetDatabase.FindAssets("t:TMP_Settings").Length > 0) return;
            Type importer = Type.GetType("TMPro.TMP_PackageResourceImporter, Unity.TextMeshPro.Editor");
            MethodInfo import = importer?.GetMethod("ImportResources", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool), typeof(bool), typeof(bool) }, null);
            if (import == null)
            {
                Debug.LogWarning("Dunk Duel: import TMP Essential Resources from Window > TextMeshPro (the UI falls back to legacy text until then).");
                return;
            }
            try
            {
                import.Invoke(null, new object[] { true, false, false });
            }
            catch (Exception e)
            {
                Debug.LogWarning("Dunk Duel: TMP essentials import failed: " + e.Message);
            }
        }

        // ------------------------------------------------------------------ scenes

        private static void CreateBootScene()
        {
            EnsureFolder("Assets/DunkDuel/Scenes");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(BootScenePath) == null)
            {
                Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var marker = new GameObject("DunkDuelBoot");
                marker.AddComponent<DunkDuel.UI.BootMarker>();
                EditorSceneManager.SaveScene(scene, BootScenePath);
            }
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(BootScenePath, true) };
        }

        public static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
        }
    }
}
