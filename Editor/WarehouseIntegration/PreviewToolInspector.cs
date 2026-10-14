using System;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using SLZ.Marrow.Warehouse;
using SLZ.MarrowEditor;
using UnityEditor;
using UnityEngine;

namespace BLPTool
{
    /// <summary>
    /// "Preview Tool" foldout on every crate inspector. Drawn through Unity's public header hook, so the SDK's
    /// crate editors stay untouched (it sits just under the inspector header).
    /// </summary>
    [InitializeOnLoad]
    static class PreviewToolInspector
    {
        const string FoldoutPref = "BLPTool.Inspector.PreviewToolExpanded";
        const string RealizeFolderPref = "BLPTool.Inspector.RealizeFolder";
        static bool s_busy;
        static GUIStyle s_foldoutStyle;

        static PreviewToolInspector() => Editor.finishedDefaultHeaderGUI += OnHeaderGUI;

        static void OnHeaderGUI(Editor editor)
        {
            if (editor is CrateEditor)
                Draw(editor);
        }

        static void Draw(Editor editor)
        {
            var crates = editor.targets.OfType<Crate>().Where(c => c != null).ToArray();
            if (crates.Length == 0) return;

            bool expanded = EditorPrefs.GetBool(FoldoutPref, true);
            s_foldoutStyle ??= new GUIStyle(EditorStyles.foldout) { fontStyle = FontStyle.Bold };
            bool newExpanded;
            using (new EditorGUILayout.HorizontalScope())
            {
                // the header area draws in hierarchy mode, which puts foldout arrows 15px left of the rect
                GUILayout.Space(15);
                newExpanded = EditorGUILayout.Foldout(expanded, "Preview Tool", true, s_foldoutStyle);
            }
            if (newExpanded != expanded) EditorPrefs.SetBool(FoldoutPref, newExpanded);
            if (newExpanded)
            {
                bool levels = crates.All(c => c is LevelCrate);
                bool spawnables = crates.All(c => c is GameObjectCrate);
                using (new EditorGUI.IndentLevelScope())
                using (new EditorGUI.DisabledScope(s_busy || EditorApplication.isCompiling || !AssetWarehouse.ready))
                {
                    using (new EditorGUI.DisabledScope(!(spawnables || (levels && crates.Length == 1))))
                    {
                        string previewHint = levels ? "Loads the level (enters play mode)"
                            : spawnables ? "Loads the spawnable(s) into the open scene"
                            : "Select spawnables/avatars, or a single level";
                        if (ButtonRow("Preview", previewHint))
                            Preview(crates, spawnables);
                    }

                    using (new EditorGUI.DisabledScope(!(spawnables || levels)))
                    {
                        string realizeHint = crates.Length > 1 ? $"Realizes {crates.Length} items to a picked folder" : "Realizes the item to a picked folder";
                        if (ButtonRow("Realize", realizeHint))
                            Realize(crates);
                    }
                }
            }
        }

        static bool ButtonRow(string label, string hint)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(EditorGUI.indentLevel * 15);
                bool clicked = GUILayout.Button(label, GUILayout.Width(90));
                EditorGUILayout.LabelField(hint, EditorStyles.miniLabel);
                return clicked;
            }
        }

        static void Preview(Crate[] crates, bool spawnables)
        {
            if (!spawnables)
            {
                BLPApi.PreviewLevel(crates[0].Barcode);
                return;
            }
            var barcodes = crates.Select(c => c.Barcode).ToArray();
            Run(async () =>
            {
                foreach (var barcode in barcodes)
                    await BLPApi.PreviewSpawnable(barcode);
            });
        }

        static void Realize(Crate[] crates)
        {
            string start = EditorPrefs.GetString(RealizeFolderPref, Application.dataPath);
            string picked = EditorUtility.OpenFolderPanel("Realize to folder (inside Assets)", start, "");
            if (string.IsNullOrEmpty(picked)) return;

            string assetsFolder;
            try { assetsFolder = Realise.BLPPaths.NormaliseAssetPath(picked); }
            catch (ArgumentException)
            {
                EditorUtility.DisplayDialog("Preview Tool", "Pick a folder inside this project's Assets folder.", "OK");
                return;
            }
            EditorPrefs.SetString(RealizeFolderPref, picked);

            var levels = crates.OfType<LevelCrate>().ToArray();
            if (levels.Length > 1)
            {
                EditorUtility.DisplayDialog("Preview Tool", "Levels are realized through a play mode capture, one at a time. Select a single level.", "OK");
                return;
            }
            if (levels.Length == 1 && !EditorUtility.DisplayDialog("Preview Tool",
                    $"Realizing '{levels[0].Title}' enters play mode to stream and capture it, then rebuilds it as a scene. Continue?", "Realize", "Cancel"))
                return;

            var jobs = crates.Select(c => (c.Barcode, Folder: $"{assetsFolder}/{Realise.BLPPaths.Sanitise(c.Title)}")).ToArray();
            Run(async () =>
            {
                foreach (var job in jobs)
                {
                    var report = await BLPApi.Dump(job.Barcode, job.Folder);
                    if (!report.Pending)
                        EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(job.Folder));
                }
            });
        }

        static void Run(Func<UniTask> op)
        {
            async UniTaskVoid Go()
            {
                s_busy = true;
                try { await op(); }
                catch (Exception e)
                {
                    EditorUtility.ClearProgressBar();
                    Debug.LogException(e);
                }
                finally { s_busy = false; }
            }
            Go().Forget();
        }
    }
}
