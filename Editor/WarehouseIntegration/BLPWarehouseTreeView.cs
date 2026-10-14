using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SLZ.Marrow.Warehouse;
using SLZ.MarrowEditor;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace BLPTool
{
    /// <summary>A top-level group BLPTool adds to the Asset Warehouse tree (after External Pallets).</summary>
    public abstract class WarehouseTreeSection
    {
        public abstract string Title { get; }

        /// <summary>External pallets claimed here are listed in this section instead of under External Pallets.</summary>
        public abstract bool Claims(Pallet pallet);

        /// <summary>Rows listed after the section's pallets: status lines, or stand-ins for content not loaded yet.</summary>
        public virtual IEnumerable<Row> Rows => Array.Empty<Row>();

        /// <summary>Called when the section is expanded; load content lazily here.</summary>
        public virtual void OnExpanded() { }

        public class Row
        {
            public string Label;
            /// <summary>When set, the row is expandable and this runs when it's expanded (e.g. to load one pallet).</summary>
            public Action OnExpand;
            /// <summary>Hidden when the warehouse search text isn't in its label.</summary>
            public bool Searchable;
        }
    }

    /// <summary>
    /// The SDK's warehouse tree plus BLPTool's sections. Built on top of the SDK's own BuildRoot, so the SDK
    /// stays untouched: claimed pallets are moved out of External Pallets and section rows are appended.
    /// </summary>
    public class BLPWarehouseTreeView : AssetWarehouseTreeView
    {
        public static readonly List<WarehouseTreeSection> Sections = new List<WarehouseTreeSection>();

        readonly Dictionary<int, Action> _expandCallbacks = new Dictionary<int, Action>();
        readonly HashSet<int> _firedIds = new HashSet<int>();
        readonly HashSet<int> _sectionIds = new HashSet<int>();

        public BLPWarehouseTreeView(TreeViewState state) : base(state) { }

        protected override TreeViewItem BuildRoot()
        {
            var root = base.BuildRoot();
            _expandCallbacks.Clear();
            _firedIds.Clear(); // ids are reassigned on every build; callbacks are idempotent
            _pendingOpened.Clear();
            if (list || root.children == null || root.children.Count < 2)
                return root;

            // base adds "Pallets" then "External Pallets" first in tree mode
            var external = root.children.FirstOrDefault(c => c.displayName == "External Pallets") ?? root.children[1];
            _sectionIds.Clear();

            foreach (var section in Sections)
            {
                var sectionItem = NewItem(section.Title);
                root.AddChild(sectionItem);
                _expandCallbacks[sectionItem.id] = section.OnExpanded;
                _sectionIds.Add(sectionItem.id);

                if (external.children != null)
                {
                    foreach (var palletItem in external.children.OfType<ScannableTreeViewItem>().ToList())
                    {
                        if (AssetWarehouse.Instance.TryGetPallet(palletItem.barcode, out var pallet) && section.Claims(pallet))
                        {
                            external.children.Remove(palletItem);
                            sectionItem.AddChild(palletItem);
                        }
                    }
                }

                foreach (var row in section.Rows)
                {
                    if (row.Searchable && !string.IsNullOrEmpty(search) && row.Label.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    var rowItem = NewItem(row.Label);
                    sectionItem.AddChild(rowItem);
                    if (row.OnExpand != null)
                    {
                        _expandCallbacks[rowItem.id] = row.OnExpand;
                        rowItem.AddChild(NewItem("Loading..."));
                    }
                }

                if (!sectionItem.hasChildren) // keep it expandable so it can load lazily
                    sectionItem.AddChild(NewItem("(empty)"));
            }

            SettleExpandedState();
            SetupDepthsFromParentsAndChildren(root);
            return root;
        }

        /// <summary>
        /// Expanded state is stored by id and ids shift on every build, so an unrelated item's expanded id can land
        /// on one of our rows. Lazy rows therefore always start collapsed (only a real user expansion loads), and a
        /// section that is open after a rebuild just gets its (idempotent) OnExpanded once things settle.
        /// </summary>
        void SettleExpandedState()
        {
            foreach (int id in _expandCallbacks.Keys)
            {
                if (!state.expandedIDs.Contains(id)) continue;
                if (_sectionIds.Contains(id))
                {
                    _firedIds.Add(id);
                    var onExpanded = _expandCallbacks[id];
                    EditorApplication.delayCall += () => onExpanded();
                }
                else
                {
                    state.expandedIDs.Remove(id);
                }
            }
        }

        // The base tree maps item id -> orderedObjs index, so every added row takes the next slot.
        TreeViewItem NewItem(string label)
        {
            var item = new TreeViewItem { id = orderedObjs.Count, displayName = label };
            orderedObjs.Add(null);
            return item;
        }

        readonly HashSet<int> _pendingOpened = new HashSet<int>();
        bool _processScheduled;

        protected override void ExpandedStateChanged()
        {
            base.ExpandedStateChanged();
            foreach (int id in _expandCallbacks.Keys)
            {
                if (!IsExpanded(id))
                    _firedIds.Remove(id);
                else if (!_firedIds.Contains(id))
                    _pendingOpened.Add(id);
            }

            // Alt+click (SetExpandedRecursive) raises one change per item; batch everything opened this frame.
            if (_pendingOpened.Count > 0 && !_processScheduled)
            {
                _processScheduled = true;
                EditorApplication.delayCall += ProcessOpened;
            }
        }

        void ProcessOpened()
        {
            _processScheduled = false;
            var opened = _pendingOpened.Where(id => _expandCallbacks.ContainsKey(id) && IsExpanded(id) && !_firedIds.Contains(id)).ToList();
            _pendingOpened.Clear();

            // Search, "+" and Alt+click open many lazy rows at once. That isn't a user asking to load ~100 mods:
            // leave those rows collapsed. Sections still get their (cheap) callback.
            bool bulk = opened.Count(id => !_sectionIds.Contains(id)) > 1;
            foreach (int id in opened)
            {
                if (bulk && !_sectionIds.Contains(id))
                {
                    SetExpanded(id, false);
                    continue;
                }
                _firedIds.Add(id);
                _expandCallbacks[id]();
            }
        }
    }

    /// <summary>
    /// Swaps BLPWarehouseTreeView into open Asset Warehouse windows. The window keeps its tree in a private
    /// field, so this goes through reflection; if the SDK renames it, BLPTool just stops adding its sections.
    /// </summary>
    [InitializeOnLoad]
    static class WarehouseTreeInjector
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo s_treeField = typeof(AssetWarehouseWindow).GetField("treeViewAW", Private);
        static readonly FieldInfo s_stateField = typeof(AssetWarehouseWindow).GetField("treeViewState", Private);
        static double s_nextCheck;

        static WarehouseTreeInjector()
        {
            if (s_treeField == null || s_stateField == null)
            {
                Debug.LogWarning("[BLPTool] Asset Warehouse window layout changed; Installed Mods section disabled.");
                return;
            }
            EditorApplication.update += Inject;
        }

        static void Inject()
        {
            if (EditorApplication.timeSinceStartup < s_nextCheck) return;
            s_nextCheck = EditorApplication.timeSinceStartup + 0.5;

            foreach (var window in Resources.FindObjectsOfTypeAll<AssetWarehouseWindow>())
            {
                if (!(s_treeField.GetValue(window) is AssetWarehouseTreeView current) || current is BLPWarehouseTreeView)
                    continue;
                if (!(s_stateField.GetValue(window) is TreeViewState state))
                    continue;

                var replacement = new BLPWarehouseTreeView(state);
                // carry over filters/search/icons (all public state on the SDK tree)
                foreach (var field in typeof(AssetWarehouseTreeView).GetFields(BindingFlags.Instance | BindingFlags.Public))
                    if (field.Name != nameof(AssetWarehouseTreeView.orderedObjs))
                        field.SetValue(replacement, field.GetValue(current));
                s_treeField.SetValue(window, replacement);
                replacement.Reload();
                window.Repaint();
            }
        }
    }
}
