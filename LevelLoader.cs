using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEditor;
using SLZ.Marrow.Zones;
using UltEvents;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.AddressableAssets.ResourceLocators;
using System.Threading.Tasks;






#if UNITY_EDITOR
using SLZ.Marrow.Warehouse;
using SLZ.MarrowEditor;
#endif
namespace BLPTool
{
    [ExecuteAlways]
    public class LevelLoader : MonoBehaviour
    {
#if UNITY_EDITOR
        /// <summary>Keep already-open scenes/content; don't take over the camera, time or bundles.</summary>
        [SerializeField] public bool Additive;
        /// <summary>Created by BLPApi.PreviewLevel: removed again when play mode exits.</summary>
        [SerializeField] public bool Transient;
        /// <summary>True once every scene of the level has finished loading.</summary>
        public bool Finished { get; private set; }

        const string TimeScaleKey = "BLPTool.LevelLoader.TimeScale";

        void OnDestroy() => RestoreTimeScale();

        /// <summary>Undoes the freeze from Start (it would otherwise persist into project settings).</summary>
        public static void RestoreTimeScale()
        {
            float saved = SessionState.GetFloat(TimeScaleKey, -1);
            if (saved < 0) return;
            Time.timeScale = saved;
            SessionState.EraseFloat(TimeScaleKey);
        }

        /// <summary>Everything play mode needs to stream a level, as plain data.</summary>
        public class LoadPlan
        {
            public Barcode Level;
            public string[] SceneKeys;
            public List<string> Catalogs;
        }

        /// <summary>
        /// Resolves a level crate into a <see cref="LoadPlan"/>. Do this before any Single-mode scene change:
        /// that unloads unused assets, which destroys the warehouse's in-memory crates.
        /// </summary>
        public static LoadPlan PlanFor(LevelCrate crate)
        {
            var keys = new List<string> { crate.MainAsset.AssetGUID };
            keys.AddRange(crate.PersistentScenes.Select(s => s.AssetGUID));
            keys.AddRange(crate.ChunkScenes.Select(s => s.AssetGUID));
            var catalogs = typeof(AssetWarehouse).GetField("loadedCatalogs", UltEvents.UltEventUtils.AnyAccessBindings)
                ?.GetValue(AssetWarehouse.Instance) as List<string>;
            return new LoadPlan { Level = crate.Barcode, SceneKeys = keys.ToArray(), Catalogs = new List<string>(catalogs ?? new List<string>()) };
        }

        /// <summary>Points this loader at a level (edit mode, or play mode before Start).</summary>
        public void Configure(LoadPlan plan, bool additive)
        {
            level = new CrateReferenceT<LevelCrate>(plan.Level);
            will_be_loaded = plan.SceneKeys;
            targCatalogs = plan.Catalogs;
            Additive = additive;
            Bonelab_Folder = GameInstallPath() ?? Bonelab_Folder;
        }

        void Start()
        {
            if (!EditorApplication.isPlaying) return;
            if (!Additive || Camera.main == null)
                this.gameObject.AddComponent<Camera>();

            Texture.streamingTextureForceLoadAll = true;
            if (!Additive)
            {
                // Play-mode timeScale writes through to ProjectSettings/TimeManager.asset, so remember it for restore.
                SessionState.SetFloat(TimeScaleKey, Time.timeScale);
                Time.timeScale = 0;
                AssetBundle.UnloadAllAssetBundles(true);
            }
            DontDestroyOnLoad(gameObject);
            var prefunc = Addressables.InternalIdTransformFunc;
            Addressables.InternalIdTransformFunc = (location) =>
            {
                string o = location.InternalId;
                if (o.StartsWith("PALLET_BARCODE"))
                {
                    int firstSlash = o.IndexOf("\\");
                    if (firstSlash != -1)
                        o = o.Substring(firstSlash);
                }
                if (location.PrimaryKey.EndsWith(".bundle"))
                {
                    o = standPath + o;
                    if (!File.Exists(o))
                    {
                        string pre = o;
                        int last_ = o.LastIndexOf('_');
                        int lastSlash = o.LastIndexOf("/");
                        if (last_ != -1 && lastSlash != -1 && lastSlash < last_)
                        {
                            o = o.Substring(0, last_) + ".bundle";
                        }

                        if (!File.Exists(o))
                            o = pre;
                    }
                }

                if (!File.Exists(o))
                {
                    // check if the file would exist in each catalog
                    o = o.Split("StandaloneWindows64\\").Last();
                    foreach (var catalogPath in targCatalogs)
                    {
                        string cat = Directory.GetParent(catalogPath).FullName;
                        string p = Path.Combine(cat + "/", o);
                        if (File.Exists(p))
                        {
                            o = p;
                            break;
                        }
                    }
                }

                return o;
            };

            LevelLoad();
        }
        [SerializeField][HideInInspector] List<string> targCatalogs;
        [SerializeField][HideInInspector] PalletReference Pallet;
        [SerializeField] CrateReferenceT<LevelCrate> level;/*
        [SerializeField] bool LoadAllScenesThatStartWith;
        [SerializeField] string StartWith;*/


        [SerializeField][HideInInspector] string Bonelab_Folder = "";
        [SerializeField] string[] will_be_loaded = null;
        [SerializeField] float wait_between_loads = 0;
        string a;
        string b;
        string standPath => b ??= Path.Combine(Bonelab_Folder, "StreamingAssets", "aa", "StandaloneWindows64");
        async void LevelLoad()
        {
            foreach (var catalogPath in targCatalogs)
            {
                print("loading catalog: " + catalogPath);
                await Addressables.LoadContentCatalogAsync(catalogPath).Task;
                print("Loaded!");
            }
            /*if (LoadAllScenesThatStartWith)
            {
                foreach (var catalog in Addressables.ResourceLocators)
                    if (catalog is ResourceLocationMap map)
                        foreach (var loc in map.Locations.Values)
                            if (loc.First().ResourceType == typeof(UnityEngine.ResourceManagement.ResourceProviders.SceneInstance))
                            {
                                string name = loc.First().InternalId.Split('/').Last();
                                if (name.StartsWith(StartWith))
                                {
                                    AsyncOperationHandle<SceneInstance> loadee;
                                    (string, AsyncOperationHandle<SceneInstance>) tuple = (
                                        name,
                                        loadee = Addressables.LoadSceneAsync(loc.First(), UnityEngine.SceneManagement.LoadSceneMode.Additive)
                                    );
                                    loadingSubScenes.Add(tuple);
                                    loadee.Completed += (a) => loadingSubScenes.Remove(tuple);
                                }
                            }
                return;
            }
            else*/
            {
                print($"[BLPTool] Loading {will_be_loaded?.Length ?? 0} scene(s)");
                foreach (var guid in will_be_loaded ?? new string[0])
                {
                    //var loc = ((ResourceLocationMap)Addressables.ResourceLocators.First(loc => loc is ResourceLocationMap)).Locations.Values.First(v => v.First().ProviderId == guid);
                    await Task.Delay((int)(wait_between_loads*1000));
                    AsyncOperationHandle<SceneInstance> loadee;
                    (string, AsyncOperationHandle<SceneInstance>) tuple = (
                        guid,//loc.First().InternalId.Split('/').Last(),
                        loadee = Addressables.LoadSceneAsync(guid, UnityEngine.SceneManagement.LoadSceneMode.Additive)
                    );
                    loadingSubScenes.Add(tuple);
                    loadee.Completed += (a) => loadingSubScenes.Remove(tuple);
                    handles.Add(loadee);
                }
                foreach (var handle in handles)
                {
                    await handle.Task;
                    if (handle.Status != AsyncOperationStatus.Succeeded)
                        Debug.LogError($"[BLPTool] Scene load failed: {handle.DebugName}\n{handle.OperationException}");
                }
                print($"[BLPTool] Level loaded: {handles.Count} scene(s)");
                Finished = true;
            }
        }
        readonly List<AsyncOperationHandle<SceneInstance>> handles = new();
        /*
        private List<string> LoadedScenes = new List<string>();
        private void OnLoad_Completed(AsyncOperationHandle<SceneInstance> obj)
        {
            foreach (var item in FindObjectsOfType<SceneChunk>())
            {
                foreach (var item1 in (MarrowScene[])item.GetType().GetField("sceneLayers", UltEventUtils.AnyAccessBindings).GetValue(item))
                {
                    Debug.Log(item.gameObject.name + " has " + item1.AssetGUID);
                    if (!LoadedScenes.Contains(item1.AssetGUID))
                    {
                        LoadedScenes.Add(item1.AssetGUID);
                        AsyncOperationHandle<SceneInstance> loadee; 
                        (string, AsyncOperationHandle<SceneInstance>) tuple = (
                            item.gameObject.name,
                            loadee = Addressables.LoadSceneAsync(item1.AssetGUID, UnityEngine.SceneManagement.LoadSceneMode.Additive)
                        );
                        loadingSubScenes.Add(tuple);
                        loadee.Completed += (a) => loadingSubScenes.Remove(tuple);
                    }
                }
            }
        }*/

        List<(string, AsyncOperationHandle<SceneInstance>)> loadingSubScenes = new();
        private void OnGUI()
        {
            if (!EditorApplication.isPlaying) return;
            if (loadingSubScenes.Count > 0)
            {
                foreach (var loader in loadingSubScenes)
                {
                    GUILayout.Label($"Loading {loader.Item1} at {(int)(loader.Item2.PercentComplete * 100)}%");
                }
            }
            else GUILayout.Label("\ndone");
        }
        private void Update()
        {
            if (!EditorApplication.isPlaying)
                RefreshKeys();
        }

        /// <summary>First configured game install path. The SDK only loads its preferences lazily (and they are
        /// unset right after a domain reload), so load them on demand rather than hitting its null instance.</summary>
        static string GameInstallPath()
        {
            List<string> paths;
            try { paths = SDKProjectPreferences.MarrowGameInstallPaths; }
            catch (System.NullReferenceException)
            {
                SDKProjectPreferences.LoadFromFile();
                paths = SDKProjectPreferences.MarrowGameInstallPaths;
            }
            return paths != null && paths.Count > 0 ? paths[0] : null;
        }

        /// <summary>Keeps a hand-placed loader (Level Loader menu) in sync with its level field.</summary>
        void RefreshKeys()
        {
            Bonelab_Folder = GameInstallPath() ?? Bonelab_Folder;
            // Only while the crate resolves: after a scene change the warehouse's crates may be unloaded,
            // and the keys we already have must survive that.
            if (level?.Crate is LevelCrate crate)
            {
                var plan = PlanFor(crate);
                will_be_loaded = plan.SceneKeys;
                targCatalogs = plan.Catalogs;
                Pallet ??= new PalletReference();
                Pallet.Barcode = new Barcode(crate.Pallet?.Barcode);
            }
        }
#endif
    }
}