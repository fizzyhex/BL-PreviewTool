using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SLZ.Marrow.Warehouse;
using UnityEditor;
using UnityEngine;

namespace BLPTool
{
    /// <summary>
    /// "Installed Mods" group in the Asset Warehouse. Lazy at two levels, since users commonly have 50-150 mods:
    /// expanding the group only reads each mod's pallet.json header; expanding a mod loads that one pallet
    /// (catalog + data cards) into the warehouse. Loaded mods are reloaded after domain reloads.
    /// </summary>
    [InitializeOnLoad]
    public class InstalledModsSection : WarehouseTreeSection
    {
        public class ModInfo
        {
            public Barcode Barcode;
            public string Title, Author, PalletPath;
        }

        const string ModsPathPref = "BLPTool.ModsPath";
        const string LoadedModsKey = "BLPTool.LoadedMods";

        public static readonly InstalledModsSection Instance = new InstalledModsSection();

        static InstalledModsSection()
        {
            BLPWarehouseTreeView.Sections.Add(Instance);
            AssetWarehouse.OnReady(() => Instance.RestoreLoadedMods().Forget());
        }

        /// <summary>The game's mod folder (persistentDataPath/Mods of BONELAB), overridable via EditorPrefs.</summary>
        public static string ModsPath
        {
            get
            {
                string custom = EditorPrefs.GetString(ModsPathPref, "");
                if (!string.IsNullOrEmpty(custom)) return custom;
                // the editor's own persistentDataPath is LocalLow/<company>/<product>
                string localLow = Directory.GetParent(Application.persistentDataPath).Parent.FullName;
                return Path.Combine(localLow, "Stress Level Zero", "BONELAB", "Mods");
            }
            set => EditorPrefs.SetString(ModsPathPref, value ?? "");
        }

        List<ModInfo> _mods;
        readonly HashSet<Barcode> _modBarcodes = new HashSet<Barcode>();
        readonly HashSet<Barcode> _loading = new HashSet<Barcode>();
        readonly Dictionary<Barcode, string> _failed = new Dictionary<Barcode, string>();

        public override string Title => _mods == null ? "Installed Mods" : $"Installed Mods ({_mods.Count})";

        public IReadOnlyList<ModInfo> Mods => _mods ?? (IReadOnlyList<ModInfo>)Array.Empty<ModInfo>();

        public override bool Claims(Pallet pallet)
        {
            if (_modBarcodes.Contains(pallet.Barcode)) return true;
            return AssetWarehouse.Instance.TryGetPalletManifest(pallet.Barcode, out var manifest)
                   && IsUnderModsPath(manifest.PalletPath);
        }

        public override void OnExpanded()
        {
            if (_mods == null) Rescan();
        }

        public override IEnumerable<Row> Rows
        {
            get
            {
                if (_mods == null)
                {
                    yield return new Row { Label = "Expand to list installed mods" };
                    yield break;
                }
                if (!Directory.Exists(ModsPath))
                {
                    yield return new Row { Label = $"No mods folder at {ModsPath}" };
                    yield break;
                }

                var unloaded = _mods.Where(m => !IsLoaded(m)).ToList();
                if (unloaded.Count > 1 && _loading.Count == 0)
                    yield return new Row { Label = $"> Expand to load all {unloaded.Count} mods", OnExpand = () => LoadAll().Forget() };

                foreach (var mod in unloaded)
                {
                    string state = _loading.Contains(mod.Barcode) ? "loading..."
                        : _failed.TryGetValue(mod.Barcode, out var why) ? $"failed: {why}"
                        : "not loaded";
                    yield return new Row
                    {
                        Label = $"{mod.Title}  [{mod.Author}]  - {state}",
                        OnExpand = _failed.ContainsKey(mod.Barcode) ? null : () => Load(mod).Forget(),
                        Searchable = true,
                    };
                }

                if (_mods.Count == 0)
                    yield return new Row { Label = "No pallet mods installed" };
            }
        }

        /// <summary>Re-reads the mods folder (pallet.json headers only).</summary>
        public void Rescan()
        {
            var mods = new List<ModInfo>();
            if (Directory.Exists(ModsPath))
            {
                foreach (string dir in Directory.GetDirectories(ModsPath))
                {
                    string palletPath = Directory.GetFiles(dir, $"*{Pallet.PALLET_JSON_FILENAME}", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (palletPath == null) continue;
                    var info = ReadHeader(palletPath);
                    if (info != null) mods.Add(info);
                }
            }
            _mods = mods.OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase).ToList();
            _modBarcodes.Clear();
            foreach (var mod in _mods) _modBarcodes.Add(mod.Barcode);
            NotifyChanged();
        }

        static ModInfo ReadHeader(string palletPath)
        {
            try
            {
                // SLZ.Serialize format: { root: { ref }, objects: { <ref>: { barcode, title, author, ... } } }
                var json = JObject.Parse(File.ReadAllText(palletPath));
                string rootRef = (string)json["root"]?["ref"] ?? "1";
                var pallet = json["objects"]?[rootRef];
                string barcode = (string)pallet?["barcode"];
                if (string.IsNullOrEmpty(barcode)) return null;
                return new ModInfo
                {
                    Barcode = new Barcode(barcode),
                    Title = (string)pallet["title"] ?? barcode,
                    Author = (string)pallet["author"] ?? "?",
                    PalletPath = palletPath.Replace('\\', '/'),
                };
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BLPTool] Couldn't read mod pallet header {palletPath}: {e.Message}");
                return null;
            }
        }

        public bool IsLoaded(ModInfo mod) => AssetWarehouse.ready && AssetWarehouse.Instance.HasPallet(mod.Barcode);

        /// <summary>Loads one mod's pallet into the warehouse.</summary>
        public async UniTask<bool> Load(ModInfo mod)
        {
            if (IsLoaded(mod) || !_loading.Add(mod.Barcode)) return IsLoaded(mod);
            _failed.Remove(mod.Barcode);
            NotifyChanged();
            QueueEditorUpdateLoop.StartEditorUpdateLoop(); // keep async loading ticking while the editor is unfocused
            bool ok = false;
            try
            {
                ok = await AssetWarehouse.Instance.LoadPalletFromFolderAsync(mod.PalletPath);
                if (!ok) _failed[mod.Barcode] = "see console";
                else RememberLoaded(mod.Barcode, true);
            }
            catch (Exception e)
            {
                _failed[mod.Barcode] = e.GetType().Name;
                Debug.LogException(e);
            }
            finally
            {
                QueueEditorUpdateLoop.StopEditorUpdateLoop();
                _loading.Remove(mod.Barcode);
                NotifyChanged();
            }
            return ok;
        }

        public async UniTask LoadAll()
        {
            foreach (var mod in _mods.Where(m => !IsLoaded(m)).ToList())
                await Load(mod);
        }

        /// <summary>Mods the user loaded come back after domain reloads / play mode (the warehouse forgets them).</summary>
        async UniTaskVoid RestoreLoadedMods()
        {
            var barcodes = SessionState.GetString(LoadedModsKey, "").Split('|').Where(s => s.Length > 0).ToList();
            if (barcodes.Count == 0) return;
            if (_mods == null) Rescan();
            foreach (var mod in _mods.Where(m => barcodes.Contains(m.Barcode.ID)))
                await Load(mod);
        }

        static void RememberLoaded(Barcode barcode, bool loaded)
        {
            var set = new HashSet<string>(SessionState.GetString(LoadedModsKey, "").Split('|').Where(s => s.Length > 0));
            if (loaded) set.Add(barcode.ID); else set.Remove(barcode.ID);
            SessionState.SetString(LoadedModsKey, string.Join("|", set));
        }

        static bool IsUnderModsPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string full = Path.GetFullPath(path).Replace('\\', '/');
            string root = Path.GetFullPath(ModsPath).Replace('\\', '/').TrimEnd('/') + "/";
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        // The Asset Warehouse window rebuilds its tree on this event.
        static void NotifyChanged() => AssetWarehouse.Instance?.OnChanged?.Invoke();
    }
}
