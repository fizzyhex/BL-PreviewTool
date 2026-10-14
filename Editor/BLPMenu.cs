#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using SLZ.Marrow.Warehouse;
using UnityEditor;
using UnityEngine;

namespace BLPTool
{
    /// <summary>Searchable list of every warehouse crate with Preview / Dump buttons wired to <see cref="BLPApi"/>.</summary>
    public class BLPContentWindow : EditorWindow
    {
        string _search = "";
        string _dumpRoot = "Assets/BLPDump";
        Vector2 _scroll;
        List<Crate> _results = new();
        string _lastSearch;

        [MenuItem("Stress Level Zero/Void Tools/(BLPTool) Content Browser", priority = 2)]
        static void Open() => GetWindow<BLPContentWindow>("BLP Content");

        [MenuItem("Stress Level Zero/Void Tools/(BLPTool) Clear Preview Temp Assets", priority = 21)]
        static void ClearTemp()
        {
            if (EditorUtility.DisplayDialog("BLPTool", $"Delete {BLPApi.TempRoot} and everything previewed into it?", "Delete", "Cancel"))
                BLPApi.ClearTemp();
        }

        [MenuItem("CONTEXT/CrateSpawner/(BLPTool) Realise Preview")]
        static void RealiseSpawner(MenuCommand cmd)
        {
            var spawner = (CrateSpawner)cmd.context;
            Run(() => BLPApi.PreviewSpawnable(spawner.spawnableCrateReference.Barcode, spawner.transform));
        }

        void OnGUI()
        {
            if (!AssetWarehouse.ready)
            {
                EditorGUILayout.HelpBox("Waiting for the Asset Warehouse... (set the game install path in Marrow SDK preferences)", MessageType.Info);
                return;
            }

            _search = EditorGUILayout.TextField("Search (title / barcode)", _search);
            _dumpRoot = EditorGUILayout.TextField("Dump folder", _dumpRoot);
            if (_search != _lastSearch)
            {
                _lastSearch = _search;
                string q = _search.Trim().ToLowerInvariant();
                _results = q.Length < 2 ? new List<Crate>() : AssetWarehouse.Instance.GetPallets().SelectMany(p => p.Crates)
                    .Where(c => c.Title.ToLowerInvariant().Contains(q) || c.Barcode.ID.ToLowerInvariant().Contains(q))
                    .Take(200).ToList();
            }

            EditorGUILayout.HelpBox("Dumped game assets are for local reference: don't ship them in your pallets.", MessageType.None);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var crate in _results)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(new GUIContent($"{crate.Title}  [{crate.GetType().Name.Replace("Crate", "")}]", crate.Barcode.ID));
                    if (crate is GameObjectCrate && GUILayout.Button("Preview", GUILayout.Width(70)))
                        Run(() => BLPApi.PreviewSpawnable(crate.Barcode));
                    if (crate is LevelCrate)
                    {
                        if (GUILayout.Button("Play", GUILayout.Width(45))) BLPApi.PreviewLevel(crate.Barcode);
                        if (GUILayout.Button("+Add", GUILayout.Width(45))) BLPApi.PreviewLevel(crate.Barcode, additive: true);
                    }
                    if (GUILayout.Button("Dump", GUILayout.Width(55)))
                        Run(() => BLPApi.Dump(crate.Barcode, $"{_dumpRoot}/{Realise.BLPPaths.Sanitise(crate.Title)}"));
                    if (GUILayout.Button("Copy", GUILayout.Width(45)))
                        EditorGUIUtility.systemCopyBuffer = crate.Barcode.ID;
                }
            }
            EditorGUILayout.EndScrollView();
        }

        static void Run<T>(Func<UniTask<T>> op)
        {
            async UniTaskVoid Go()
            {
                try { await op(); }
                catch (Exception e) { EditorUtility.ClearProgressBar(); Debug.LogException(e); }
            }
            Go().Forget();
        }
    }
}
#endif
