#if UNITY_EDITOR
using System;
using System.Linq;
using System.Threading.Tasks;
using BLPTool.Realise;
using Cysharp.Threading.Tasks;
using SLZ.Marrow.Warehouse;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
using Object = UnityEngine.Object;

namespace BLPTool
{
    /// <summary>
    /// Editor entry points for inspecting any Marrow pallet content (game or mods) and turning it into
    /// saveable project assets. Dumped game assets are for local reference only: don't redistribute them.
    /// </summary>
    public static class BLPApi
    {
        /// <summary>Where <see cref="PreviewSpawnable"/> writes extracted assets.</summary>
        public const string TempRoot = "Assets/BLPTool_Temp";

        /// <summary>
        /// Loads a spawnable/avatar crate as a realised, unsaved GameObject hierarchy in the active scene
        /// (under <paramref name="parent"/> if given). Its assets are extracted to <see cref="TempRoot"/>, so it can be
        /// dragged into a prefab or scene and saved.
        /// </summary>
        public static async UniTask<GameObject> PreviewSpawnable(Barcode barcode, Transform parent = null)
        {
            var crate = await ResolveCrate(barcode) as GameObjectCrate
                        ?? throw new ArgumentException($"{barcode} is not a spawnable/avatar crate");
            var ctx = NewContext($"{TempRoot}/{BLPPaths.Sanitise(crate.Title)}", crate);
            var staging = Realiser.BeginStaging();
            try
            {
                var clone = await RealiseCrate(crate, ctx, staging.transform);
                clone.transform.SetParent(parent, false);
                Undo.RegisterCreatedObjectUndo(clone, $"Preview {crate.Title}");
                Selection.activeGameObject = clone;
                return clone;
            }
            finally
            {
                Object.DestroyImmediate(staging);
                Finish(ctx);
            }
        }

        /// <summary>
        /// Streams a level into play mode (entering it if needed). <paramref name="additive"/> keeps the open
        /// scene(s) instead of starting from an empty scene.
        /// </summary>
        public static void PreviewLevel(Barcode barcode, bool additive = false)
        {
            if (!AssetWarehouse.ready || !AssetWarehouse.Instance.TryGetCrate(barcode, out LevelCrate crate))
                throw new ArgumentException($"{barcode} is not a loaded level crate");

            // Resolve before NewScene: a Single-mode scene change unloads the warehouse's in-memory crates.
            var plan = LevelLoader.PlanFor(crate);
            string title = crate.Title;

            if (!EditorApplication.isPlaying && !additive)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }

            var loader = new GameObject($"LevelLoader ({title})").AddComponent<LevelLoader>();
            loader.Transient = !EditorApplication.isPlaying;
            loader.Configure(plan, additive);
            if (!EditorApplication.isPlaying)
                EditorApplication.EnterPlaymode();
        }

        /// <summary>
        /// Realises a crate with all its assets (meshes, textures, materials, audio...) into <paramref name="path"/>
        /// (under Assets/). Spawnables/avatars become a .prefab, data cards an .asset, levels a .unity scene, and a
        /// pallet barcode dumps every crate in it. Level dumps finish asynchronously via play mode:
        /// the returned report is marked Pending.
        /// </summary>
        public static async UniTask<RealiseReport> Dump(Barcode barcode, string path)
        {
            path = BLPPaths.NormaliseAssetPath(path);
            await WaitForWarehouse();

            if (AssetWarehouse.Instance.TryGetPallet(barcode, out Pallet pallet))
            {
                var summary = new RealiseReport { Source = pallet.Title, OutputPath = path };
                foreach (var c in pallet.Crates.Where(c => c is GameObjectCrate).Concat(pallet.Crates.OfType<LevelCrate>().Take(1)))
                {
                    var r = await Dump(c.Barcode, $"{path}/{BLPPaths.Sanitise(c.Title)}");
                    summary.GameObjects += r.GameObjects; summary.Components += r.Components; summary.Assets += r.Assets;
                    summary.Failures.AddRange(r.Failures.Select(f => $"[{c.Title}] {f}"));
                    summary.Pending |= r.Pending;
                }
                if (pallet.Crates.OfType<LevelCrate>().Count() > 1)
                    summary.Warnings.Add("Only the first level was dumped: each level dump needs its own play mode session");
                return summary;
            }

            if (AssetWarehouse.Instance.TryGetCrate(barcode, out Crate crate))
            {
                switch (crate)
                {
                    case GameObjectCrate goCrate:
                    {
                        var ctx = NewContext(path, goCrate);
                        var staging = Realiser.BeginStaging();
                        try
                        {
                            var clone = await RealiseCrate(goCrate, ctx, staging.transform);
                            PrefabUtility.SaveAsPrefabAsset(clone, AssetDatabase.GenerateUniqueAssetPath($"{path}/{BLPPaths.Sanitise(goCrate.Title)}.prefab"));
                        }
                        finally
                        {
                            Object.DestroyImmediate(staging);
                            Finish(ctx);
                        }
                        return ctx.Report;
                    }
                    case LevelCrate level:
                        return LevelCapture.Begin(level, path);
                    default:
                        throw new NotSupportedException($"Crate type {crate.GetType().Name} can't be dumped yet");
                }
            }

            if (AssetWarehouse.Instance.TryGetDataCard(barcode, out DataCard card))
            {
                var ctx = NewContext(path, card);
                try { Realiser.Realise(card, ctx); }
                finally { Finish(ctx); }
                return ctx.Report;
            }

            throw new ArgumentException($"Nothing in the warehouse has barcode {barcode}");
        }

        /// <summary>Removes everything <see cref="PreviewSpawnable"/> extracted.</summary>
        public static void ClearTemp()
        {
            if (AssetDatabase.IsValidFolder(TempRoot))
                AssetDatabase.DeleteAsset(TempRoot);
        }

        static async UniTask<GameObject> RealiseCrate(GameObjectCrate crate, RealiseContext ctx, Transform staging)
        {
            var source = await LoadMainGameObject(crate);
            if (source == null) throw new Exception($"Couldn't load {crate.Title} ({crate.MainAsset.AssetGUID})");
            await PreloadAudio(new Object[] { source });
            var clone = Realiser.RealiseHierarchies(new[] { source }, ctx, staging)[0];
            clone.name = source.name;
            return clone;
        }

        /// <summary>
        /// Background-loading clips have no sample data until loaded; load them all up-front (asynchronously)
        /// so extraction can read them synchronously.
        /// </summary>
        public static async UniTask PreloadAudio(Object[] roots, float timeoutSeconds = 30)
        {
            var clips = EditorUtility.CollectDependencies(roots).OfType<AudioClip>()
                .Where(c => c.loadState != AudioDataLoadState.Loaded).ToList();
            foreach (var clip in clips) clip.LoadAudioData();
            for (float t = 0; t < timeoutSeconds && clips.Any(c => c.loadState == AudioDataLoadState.Loading); t += 0.05f)
                await Task.Delay(50);
        }

        static async UniTask<GameObject> LoadMainGameObject(GameObjectCrate crate)
        {
            if (crate.MainAsset.EditorAsset is GameObject local) return local; // pallet authored in this project
            return await Addressables.LoadAssetAsync<GameObject>(crate.MainAsset.AssetGUID).Task;
        }

        /// <summary>Pallets load asynchronously after every domain reload; wait for the barcode to show up.</summary>
        public static async UniTask<Scannable> ResolveCrate(Barcode barcode, float timeoutSeconds = 60)
        {
            for (float t = 0; t < timeoutSeconds; t += 0.1f)
            {
                if (AssetWarehouse.ready && AssetWarehouse.Instance.TryGetCrate(barcode, out Crate c)) return c;
                await Task.Delay(100);
            }
            throw new TimeoutException($"Crate {barcode} not found in the warehouse (is the game install path set?)");
        }

        static async UniTask WaitForWarehouse()
        {
            for (int i = 0; i < 600 && !(AssetWarehouse.ready && AssetWarehouse.Instance != null); i++)
                await Task.Delay(100);
        }

        static RealiseContext NewContext(string path, Scannable source)
        {
            var ctx = new RealiseContext(path)
            {
                Progress = (info, t) => EditorUtility.DisplayCancelableProgressBar($"BLPTool: realising {source.Title}", info, t),
            };
            ctx.Report.Source = $"{source.Title} ({source.Barcode})";
            return ctx;
        }

        static void Finish(RealiseContext ctx)
        {
            EditorUtility.ClearProgressBar();
            ctx.Complete();
            AssetDatabase.Refresh();
        }
    }
}
#endif
