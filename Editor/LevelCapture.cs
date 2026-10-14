#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using BLPTool.Realise;
using SLZ.Marrow.Warehouse;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BLPTool
{
    /// <summary>
    /// Dumps a level to a .unity scene. AssetBundle scenes can only be opened in play mode, so this:
    /// (1) streams the level in play mode, (2) realises every loaded root into one container prefab (assets are
    /// written to disk, which survives leaving play mode), (3) back in edit mode, unpacks that prefab into a
    /// fresh scene and saves it. The job survives domain reloads via SessionState.
    /// </summary>
    [InitializeOnLoad]
    static class LevelCapture
    {
        const string JobKey = "BLPTool.LevelCapture.Job";
        const float SettleSeconds = 5f;

        [System.Serializable]
        class Job
        {
            public string Barcode, Title, Path, Phase, CapturePrefab;
        }

        static Job Current
        {
            get
            {
                string json = SessionState.GetString(JobKey, "");
                return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<Job>(json);
            }
            set => SessionState.SetString(JobKey, value == null ? "" : JsonUtility.ToJson(value));
        }

        static double s_finishedAt = -1;

        static LevelCapture()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.update += Poll;
        }

        public static RealiseReport Begin(LevelCrate level, string path)
        {
            if (Current != null)
                throw new System.InvalidOperationException($"A level capture ({Current.Title}) is already running");
            // read before PreviewLevel: its scene switch unloads the warehouse's crate objects
            string title = level.Title, source = $"{level.Title} ({level.Barcode})";
            var barcode = level.Barcode;
            Current = new Job { Barcode = barcode.ID, Title = title, Path = path, Phase = "load" };
            try { BLPApi.PreviewLevel(barcode, additive: false); }
            finally
            {
                // user declined the save-scenes prompt, or loading threw
                if (!EditorApplication.isPlayingOrWillChangePlaymode) Current = null;
            }
            if (Current == null)
                return new RealiseReport { Source = source, OutputPath = path, Failures = { "Cancelled before play mode started" } };
            return new RealiseReport { Source = source, OutputPath = path, Pending = true };
        }

        [MenuItem("Stress Level Zero/Void Tools/(BLPTool) Cancel Level Capture", true)]
        static bool CanCancel() => Current != null;

        [MenuItem("Stress Level Zero/Void Tools/(BLPTool) Cancel Level Capture", priority = 20)]
        static void Cancel()
        {
            Current = null;
            EditorUtility.ClearProgressBar();
            Debug.Log("[BLPTool] Level capture cancelled");
        }

        static void Poll()
        {
            var job = Current;
            if (job == null || job.Phase != "load" || !EditorApplication.isPlaying) return;

            var loader = Object.FindObjectsOfType<LevelLoader>().FirstOrDefault();
            if (loader == null || !loader.Finished) { s_finishedAt = -1; return; }

            // let chunk/zone scripts finish their first frames before snapshotting
            if (s_finishedAt < 0) s_finishedAt = EditorApplication.timeSinceStartup;
            if (EditorApplication.timeSinceStartup - s_finishedAt < SettleSeconds) return;

            Capture(job, loader);
        }

        static void Capture(Job job, LevelLoader loader)
        {
            var ctx = new RealiseContext(job.Path)
            {
                Progress = (info, t) => EditorUtility.DisplayCancelableProgressBar($"BLPTool: capturing {job.Title}", info, t),
            };
            ctx.Report.Source = $"{job.Title} ({job.Barcode})";

            var roots = new List<GameObject>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded)
                    roots.AddRange(scene.GetRootGameObjects().Where(r => r != loader.gameObject && r.GetComponent<LevelLoader>() == null));
            }

            var staging = Realiser.BeginStaging();
            try
            {
                // One container keeps cross-root references intact (separate prefabs can't reference each other's objects).
                var container = new GameObject(job.Title);
                container.transform.SetParent(staging.transform, false);
                Realiser.RealiseHierarchies(roots, ctx, container.transform);

                job.CapturePrefab = AssetDatabase.GenerateUniqueAssetPath($"{job.Path}/_capture.prefab");
                PrefabUtility.SaveAsPrefabAsset(container, job.CapturePrefab);
                job.Phase = ctx.Cancelled ? "cancelled" : "rebuild";
                Current = job;
            }
            catch
            {
                Current = null;
                throw;
            }
            finally
            {
                Object.DestroyImmediate(staging);
                EditorUtility.ClearProgressBar();
                ctx.Complete();
            }
            EditorApplication.ExitPlaymode();
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredEditMode) return;

            LevelLoader.RestoreTimeScale(); // in case the loader never got its OnDestroy

            // Loaders created by PreviewLevel are only meant to exist for one play session.
            foreach (var loader in Object.FindObjectsOfType<LevelLoader>(true).Where(l => l.Transient))
                Object.DestroyImmediate(loader.gameObject);

            var job = Current;
            if (job == null) return;
            if (job.Phase == "load")
            {
                Debug.LogWarning($"[BLPTool] Play mode ended before '{job.Title}' finished loading: capture abandoned");
                Current = null;
                return;
            }
            if (job.Phase == "rebuild")
                Rebuild(job);
            Current = null;
        }

        static void Rebuild(Job job)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(job.CapturePrefab);
            if (prefab == null)
            {
                Debug.LogError($"[BLPTool] Capture prefab missing: {job.CapturePrefab}");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var container = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            PrefabUtility.UnpackPrefabInstance(container, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            while (container.transform.childCount > 0)
                container.transform.GetChild(0).SetParent(null, true);
            Object.DestroyImmediate(container);

            string scenePath = AssetDatabase.GenerateUniqueAssetPath($"{job.Path}/{BLPPaths.Sanitise(job.Title)}.unity");
            EditorSceneManager.SaveScene(scene, scenePath);
            AssetDatabase.DeleteAsset(job.CapturePrefab);
            Debug.Log($"[BLPTool] Level '{job.Title}' dumped to {scenePath}");
        }
    }
}
#endif
