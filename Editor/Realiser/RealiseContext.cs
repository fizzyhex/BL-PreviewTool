#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BLPTool.Realise
{
    /// <summary>
    /// Outcome of a realise run: what was created, and everything that couldn't be faithfully reproduced.
    /// </summary>
    public class RealiseReport
    {
        public string Source;
        public string OutputPath;
        public int GameObjects, Components, Assets;
        public readonly List<string> MissingScripts = new();
        public readonly HashSet<string> UnmappedShaders = new();
        public readonly List<string> Failures = new();
        public readonly List<string> Warnings = new();
        public bool Pending; // level dumps finish asynchronously (across play mode)

        public bool HasProblems => MissingScripts.Count + UnmappedShaders.Count + Failures.Count > 0;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[BLPTool] Realised '{Source}' -> {OutputPath}");
            sb.AppendLine($"  {GameObjects} GameObjects, {Components} components, {Assets} assets");
            if (Pending) sb.AppendLine("  (pending: finishes after play mode capture)");
            void List(string title, IEnumerable<string> items)
            {
                var arr = items.ToArray();
                if (arr.Length == 0) return;
                sb.AppendLine($"  {title} ({arr.Length}):");
                foreach (var i in arr) sb.AppendLine("    - " + i);
            }
            List("Missing scripts (no ExtSDK match, replaced by BLPMissingScript)", MissingScripts);
            List("Shaders with no project match (fell back)", UnmappedShaders);
            List("Failures", Failures);
            List("Warnings", Warnings);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Shared state for one realise run. Memoises bundle object -> realised object, so shared assets
    /// are extracted once and reference cycles terminate.
    /// </summary>
    public class RealiseContext
    {
        /// <summary>Project-relative folder (under Assets/) that extracted assets are written to.</summary>
        public readonly string OutputRoot;
        public readonly Dictionary<Object, Object> Map = new();
        public readonly RealiseReport Report = new();
        /// <summary>Every asset written this run (re-saved at the end, in case a cycle referenced one before it existed).</summary>
        public readonly List<Object> Created = new();

        /// <summary>Called with (info, progress01); return true to cancel.</summary>
        public System.Func<string, float, bool> Progress;
        public bool Cancelled { get; private set; }

        public RealiseContext(string outputRoot)
        {
            OutputRoot = BLPPaths.NormaliseAssetPath(outputRoot);
            Report.OutputPath = OutputRoot;
            BLPPaths.EnsureFolder(OutputRoot);
        }

        internal void Tick(string info, float t)
        {
            if (Progress != null && Progress(info, t)) Cancelled = true;
        }

        /// <summary>Unique asset path for a new asset of <paramref name="kind"/> (sub-folder) named after <paramref name="name"/>.</summary>
        public string PathFor(string kind, string name, string ext)
        {
            string folder = OutputRoot + "/" + kind;
            BLPPaths.EnsureFolder(folder);
            return AssetDatabase.GenerateUniqueAssetPath($"{folder}/{BLPPaths.Sanitise(name)}.{ext}");
        }

        /// <summary>Flushes created assets to disk and writes/logs the report.</summary>
        public void Complete()
        {
            foreach (var o in Created)
                if (o != null) EditorUtility.SetDirty(o);
            AssetDatabase.SaveAssets();
            WriteReport();
        }

        public void WriteReport()
        {
            string text = Report.ToString();
            File.WriteAllText(Path.Combine(OutputRoot, "_realise_report.txt"), text);
            if (Report.HasProblems) Debug.LogWarning(text); else Debug.Log(text);
        }
    }

    public static class BLPPaths
    {
        public static string Sanitise(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "unnamed";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim().TrimEnd('.');
        }

        /// <summary>Accepts "Assets/..." or an absolute path inside the project's Assets folder.</summary>
        public static string NormaliseAssetPath(string path)
        {
            path = path.Replace('\\', '/').TrimEnd('/');
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/') + "/";
            if (Path.IsPathRooted(path))
            {
                string full = Path.GetFullPath(path).Replace('\\', '/');
                if (!full.StartsWith(projectRoot, System.StringComparison.OrdinalIgnoreCase))
                    throw new System.ArgumentException($"Path must be inside this project's Assets folder: {path}");
                path = full.Substring(projectRoot.Length);
            }
            if (path != "Assets" && !path.StartsWith("Assets/"))
                throw new System.ArgumentException($"Path must be under Assets/: {path}");
            return path;
        }

        public static void EnsureFolder(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder)) return;
            string parent = Path.GetDirectoryName(assetFolder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(assetFolder));
        }
    }
}
#endif
