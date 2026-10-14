#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BLPTool.Realise
{
    /// <summary>
    /// Bundle shaders are compiled player binaries: they render in-editor but can't be saved. Swap them for the
    /// project's shader of the same name (SLZShaders / URP / built-in), falling back to LitMAS Standard, then URP Lit.
    /// </summary>
    public static class ShaderRemap
    {
        // LitMAS shares SLZ's texture conventions (_BaseMap + MAS), so most SLZ materials degrade gracefully into it.
        // SLZ's URP fork renames Lit to "Lit (PBR Workflow)".
        static readonly string[] FallbackNames =
        {
            "SLZ/LitMAS/LitMAS Standard",
            "Universal Render Pipeline/Lit (PBR Workflow)",
            "Universal Render Pipeline/Lit",
        };
        static Dictionary<string, Shader> s_projectShaders;

        static Dictionary<string, Shader> ProjectShaders
        {
            get
            {
                if (s_projectShaders != null) return s_projectShaders;
                s_projectShaders = new Dictionary<string, Shader>();
                // without explicit folders FindAssets skips Packages/ (URP, ExtSDK shaders)
                foreach (string guid in AssetDatabase.FindAssets("t:Shader", new[] { "Assets", "Packages" }))
                {
                    var s = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid));
                    if (s != null && !s_projectShaders.ContainsKey(s.name))
                        s_projectShaders.Add(s.name, s);
                }
                return s_projectShaders;
            }
        }

        public static bool IsProjectShader(Shader s) => s != null && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(s));

        /// <summary>Returns a saveable shader for <paramref name="bundleShader"/>, or null when only the fallback fits.</summary>
        public static Shader Find(string shaderName)
        {
            if (ProjectShaders.TryGetValue(shaderName, out var s)) return s;
            // built-ins (Standard, Sprites/Default, Hidden/InternalErrorShader...) live in unity default resources
            var builtin = Shader.Find(shaderName);
            return IsProjectShader(builtin) ? builtin : null;
        }

        public static Shader Remap(Shader bundleShader, RealiseContext ctx)
        {
            if (bundleShader == null) return Fallback;
            if (IsProjectShader(bundleShader)) return bundleShader;
            var found = Find(bundleShader.name);
            if (found != null) return found;
            ctx.Report.UnmappedShaders.Add(bundleShader.name);
            return Fallback;
        }

        public static Shader Fallback
        {
            get
            {
                foreach (string name in FallbackNames)
                    if (Find(name) is Shader s) return s;
                return Shader.Find("Standard");
            }
        }

        public static void ClearCache() => s_projectShaders = null;
    }
}
#endif
