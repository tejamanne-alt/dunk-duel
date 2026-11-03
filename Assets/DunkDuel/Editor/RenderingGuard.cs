using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace DunkDuel.Editor
{
    /// <summary>
    /// Makes sure a build can draw the game: URP is assigned (when the package is installed) and the material templates
    /// in Resources exist with shaders that match the active pipeline. Without them every runtime material falls back to
    /// Unity's error shader and the court, fighters and ball render solid magenta. Runs on Editor load and before every
    /// player build, so it no longer matters whether or when Project Setup was run.
    /// </summary>
    [InitializeOnLoad]
    public static class RenderingGuard
    {
        static RenderingGuard()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                Ensure("Editor load");
            };
        }

        /// <summary>What is missing, or an empty list when the project can render.</summary>
        public static List<string> Problems()
        {
            var problems = new List<string>();
#if DD_URP
            if (!ProjectSetup.UsesUrp) problems.Add("no URP asset is assigned in Graphics/Quality settings");
#endif
            bool urp = ProjectSetup.UsesUrp;
            foreach (string name in ProjectSetup.TemplateNames)
            {
                string path = ProjectSetup.MaterialsFolder + "/" + name + ".mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                string want = ProjectSetup.TemplateShader(name, urp);
                if (m == null) problems.Add("material template " + name + " is missing");
                else if (m.shader == null || m.shader.name != want) problems.Add("material template " + name + " uses " + (m.shader != null ? m.shader.name : "no shader") + ", needs " + want);
            }
            return problems;
        }

        /// <summary>Repairs the pipeline and templates if needed. Returns true when the project can render afterwards.</summary>
        public static bool Ensure(string when)
        {
            List<string> problems = Problems();
            if (problems.Count == 0) return true;
            Debug.LogWarning("Dunk Duel (" + when + "): repairing rendering setup: " + string.Join("; ", problems) + ".");
#if DD_URP
            if (!ProjectSetup.UsesUrp) ProjectSetup.ConfigureRendering();
#endif
            ProjectSetup.CreateMaterialTemplates();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            List<string> left = Problems();
            if (left.Count == 0)
            {
                Debug.Log("Dunk Duel: rendering setup repaired (" + (ProjectSetup.UsesUrp ? "URP" : "built-in pipeline") + ").");
                return true;
            }
            Debug.LogError("Dunk Duel: rendering setup still incomplete: " + string.Join("; ", left)
                + ". Run DunkDuel > Project Setup; the build would render magenta.");
            return false;
        }
    }

    /// <summary>Before every player build: refuse to ship a build whose materials would all render magenta.</summary>
    public sealed class RenderingGuardBuildStep : IPreprocessBuildWithReport
    {
        public int callbackOrder => -150;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (!RenderingGuard.Ensure("build"))
            {
                throw new BuildFailedException("Dunk Duel: material templates or URP asset missing (see Console). Run DunkDuel > Project Setup.");
            }
        }
    }
}
