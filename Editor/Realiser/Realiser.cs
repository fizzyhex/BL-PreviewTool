#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BLPTool.Realise
{
    /// <summary>
    /// Converts objects loaded from AssetBundles (in-memory only, unsaveable) into "realised" equivalents:
    /// GameObject hierarchies with real ExtSDK components, and persistent assets in the AssetDatabase.
    /// </summary>
    public static class Realiser
    {
        /// <summary>
        /// Rebuilds <paramref name="roots"/> (bundle prefabs or loaded scene objects) as new GameObjects parented
        /// to <paramref name="parent"/>. Every component whose script resolves to an ExtSDK type is copied, and every
        /// reference is remapped: in-hierarchy refs to the clones, asset refs to extracted project assets.
        /// </summary>
        public static List<GameObject> RealiseHierarchies(IList<GameObject> roots, RealiseContext ctx, Transform parent)
        {
            var goPairs = new List<(GameObject src, GameObject dst)>();
            var compPairs = new List<(Component src, Component dst)>();

            // Pass 1: skeleton. Registering every GameObject/Transform up-front lets pass 3 resolve internal refs.
            foreach (var root in roots)
                CloneGameObject(root, parent, goPairs, ctx);

            // Pass 2: components (values still point at bundle objects).
            for (int i = 0; i < goPairs.Count; i++)
            {
                if (i % 32 == 0) ctx.Tick($"Components: {goPairs[i].src.name}", 0.1f + 0.2f * i / goPairs.Count);
                CopyComponents(goPairs[i].src, goPairs[i].dst, compPairs, ctx);
            }

            // Pass 3: remap references (this is where assets get extracted).
            for (int i = 0; i < compPairs.Count; i++)
            {
                if (ctx.Cancelled) break;
                if (i % 16 == 0) ctx.Tick($"Assets: {compPairs[i].src.name} ({compPairs[i].src.GetType().Name})", 0.3f + 0.7f * i / compPairs.Count);
                RemapReferences(compPairs[i].dst, ctx, null);
            }

            ctx.Report.GameObjects += goPairs.Count;
            ctx.Report.Components += compPairs.Count;
            return roots.Select(r => (GameObject)ctx.Map[r]).ToList();
        }

        /// <summary>Returns a saveable equivalent of <paramref name="o"/> (memoised).</summary>
        public static Object Realise(Object o, RealiseContext ctx, string ownerAssetPath = null)
        {
            if (o == null) return null;
            if (ctx.Map.TryGetValue(o, out var mapped)) return mapped;
            if (IsProjectAsset(o)) return o;

            Object result;
            try
            {
                result = o switch
                {
                    GameObject go => RealiseExternal(go, ctx),
                    Component c => RealiseExternal(c.gameObject, ctx) != null && ctx.Map.TryGetValue(c, out var mc) ? mc : null,
                    Mesh mesh => AssetExtractors.ExtractMesh(mesh, ctx),
                    Texture2D tex => AssetExtractors.ExtractTexture2D(tex, ctx),
                    Sprite sprite => AssetExtractors.ExtractSprite(sprite, ctx),
                    Material mat => AssetExtractors.ExtractMaterial(mat, ctx),
                    Shader shader => ShaderRemap.Remap(shader, ctx),
                    AudioClip clip => AssetExtractors.ExtractAudio(clip, ctx),
                    MonoScript script => ProjectScript(script, ctx),
                    _ => AssetExtractors.ExtractGeneric(o, ctx, ownerAssetPath),
                };
            }
            catch (Exception e)
            {
                ctx.Report.Failures.Add($"{o.GetType().Name} '{o.name}': {e.GetType().Name}: {e.Message}");
                result = null;
            }

            // Failed extractions keep the bundle object: still renders this session, just won't survive a save.
            result ??= o;
            ctx.Map[o] = result;
            return result;
        }

        /// <summary>
        /// Bundle objects pass EditorUtility.IsPersistent and even AssetDatabase.Contains; only a real asset path
        /// tells project assets (and Unity built-ins) apart from them.
        /// </summary>
        public static bool IsProjectAsset(Object o) => !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(o));

        static Dictionary<Type, MonoScript> s_scriptsByClass;

        /// <summary>
        /// Bundles carry their own MonoScript objects (resolved to our types, but pathless). Cloned ScriptableObjects
        /// keep pointing at those, so swap in the project's MonoScript (ExtSDK) for the same class.
        /// </summary>
        static MonoScript ProjectScript(MonoScript bundleScript, RealiseContext ctx)
        {
            if (s_scriptsByClass == null)
            {
                s_scriptsByClass = new Dictionary<Type, MonoScript>();
                foreach (var s in MonoImporter.GetAllRuntimeMonoScripts())
                {
                    var cls = s.GetClass();
                    if (cls != null && IsProjectAsset(s) && !s_scriptsByClass.ContainsKey(cls))
                        s_scriptsByClass.Add(cls, s);
                }
            }
            var type = bundleScript.GetClass();
            if (type != null && s_scriptsByClass.TryGetValue(type, out var found)) return found;
            ctx.Report.MissingScripts.Add($"MonoScript '{bundleScript.name}' ({type?.FullName ?? "unresolved"}) has no project script");
            return null;
        }

        /// <summary>A GameObject outside the hierarchy being realised, e.g. a projectile prefab a gun references.</summary>
        static GameObject RealiseExternal(GameObject go, RealiseContext ctx)
        {
            if (ctx.Map.TryGetValue(go, out var m)) return (GameObject)m;
            if (go.scene.IsValid())
            {
                ctx.Report.Warnings.Add($"Reference to scene object '{go.name}' outside the realised set was dropped");
                return null;
            }

            GameObject root = go.transform.root.gameObject;
            if (ctx.Map.ContainsKey(root)) // root is mid-realisation; its children are registered in pass 1
                return ctx.Map.TryGetValue(go, out m) ? (GameObject)m : null;

            var staging = BeginStaging();
            try
            {
                var clone = RealiseHierarchies(new[] { root }, ctx, staging.transform)[0];
                string path = ctx.PathFor("Prefabs", root.name, "prefab");
                var asset = PrefabUtility.SaveAsPrefabAsset(clone, path);
                ctx.Report.Assets++;
                RetargetMap(root, clone, asset, ctx);
            }
            finally { Object.DestroyImmediate(staging); }
            return ctx.Map.TryGetValue(go, out m) ? (GameObject)m : null;
        }

        /// <summary>After saving <paramref name="clone"/> as <paramref name="asset"/>, point src mappings at the asset's objects.</summary>
        public static void RetargetMap(GameObject src, GameObject clone, GameObject asset, RealiseContext ctx)
        {
            ctx.Map[src] = asset;
            var sc = src.GetComponents<Component>();
            var cc = clone.GetComponents<Component>();
            var ac = asset.GetComponents<Component>();
            // clone/asset component lists line up; src may differ (RequireComponent reuse), so match via the clone.
            var cloneIndex = new Dictionary<Component, int>();
            for (int i = 0; i < cc.Length; i++) cloneIndex[cc[i]] = i;
            foreach (var s in sc)
                if (s != null && ctx.Map.TryGetValue(s, out var c) && c is Component cComp && cloneIndex.TryGetValue(cComp, out int idx) && idx < ac.Length)
                    ctx.Map[s] = ac[idx];
            for (int i = 0; i < src.transform.childCount && i < asset.transform.childCount; i++)
                RetargetMap(src.transform.GetChild(i).gameObject, clone.transform.GetChild(i).gameObject, asset.transform.GetChild(i).gameObject, ctx);
        }

        /// <summary>Inactive holder: realised objects are built under it so ExtSDK scripts don't Awake mid-build.</summary>
        public static GameObject BeginStaging()
        {
            var staging = new GameObject("BLP_Staging") { hideFlags = HideFlags.DontSave };
            staging.SetActive(false);
            return staging;
        }

        static void CloneGameObject(GameObject src, Transform parent, List<(GameObject, GameObject)> pairs, RealiseContext ctx)
        {
            var dst = src.transform is RectTransform ? new GameObject(src.name, typeof(RectTransform)) : new GameObject(src.name);
            dst.transform.SetParent(parent, false);
            dst.SetActive(src.activeSelf);
            dst.layer = src.layer;
            try { dst.tag = src.tag; }
            catch { ctx.Report.Warnings.Add($"Tag '{src.tag}' on '{src.name}' isn't defined in this project"); }
            GameObjectUtility.SetStaticEditorFlags(dst, GameObjectUtility.GetStaticEditorFlags(src));

            var st = src.transform;
            var dt = dst.transform;
            dt.localPosition = st.localPosition;
            dt.localRotation = st.localRotation;
            dt.localScale = st.localScale;
            if (st is RectTransform srt && dt is RectTransform drt)
            {
                drt.anchorMin = srt.anchorMin;
                drt.anchorMax = srt.anchorMax;
                drt.pivot = srt.pivot;
                drt.sizeDelta = srt.sizeDelta;
                drt.anchoredPosition3D = srt.anchoredPosition3D;
            }

            ctx.Map[src] = dst;
            ctx.Map[st] = dt;
            pairs.Add((src, dst));
            for (int i = 0; i < st.childCount; i++)
                CloneGameObject(st.GetChild(i).gameObject, dt, pairs, ctx);
        }

        static void CopyComponents(GameObject src, GameObject dst, List<(Component, Component)> pairs, RealiseContext ctx)
        {
            var used = new HashSet<Component> { dst.transform };
            foreach (var c in src.GetComponents<Component>())
            {
                if (c == null)
                {
                    var placeholder = dst.AddComponent<BLPMissingScript>();
                    placeholder.SourcePath = HierarchyPath(src);
                    ctx.Report.MissingScripts.Add(placeholder.SourcePath);
                    continue;
                }
                if (c is Transform) continue; // done in CloneGameObject; CopySerialized would clobber parent/children

                var type = c.GetType();
                // Reuse instances that an earlier AddComponent pulled in via RequireComponent.
                Component d = dst.GetComponents(type).FirstOrDefault(x => x.GetType() == type && !used.Contains(x));
                if (d == null)
                {
                    try { d = dst.AddComponent(type); }
                    catch (Exception e) { ctx.Report.Failures.Add($"{type.Name} on '{HierarchyPath(src)}': {e.Message}"); }
                }
                if (d == null)
                {
                    ctx.Report.Failures.Add($"{type.Name} on '{HierarchyPath(src)}': AddComponent refused (DisallowMultipleComponent/abstract?)");
                    continue;
                }
                used.Add(d);
                EditorUtility.CopySerialized(c, d);
                ctx.Map[c] = d;
                pairs.Add((c, d));
            }
        }

        static readonly HashSet<string> s_skipProps = new()
        {
            "m_GameObject", "m_PrefabInstance", "m_PrefabAsset", "m_CorrespondingSourceObject", "m_Father", "m_Children",
        };

        static readonly HashSet<string> s_primitiveElements = new()
        {
            "int", "unsigned int", "float", "double", "char", "bool", "UInt8", "SInt8", "UInt16", "SInt16", "SInt64", "UInt64",
            "Vector2f", "Vector3f", "Vector4f", "Quaternionf", "ColorRGBA", "Matrix4x4f", "Keyframe", "string",
        };

        /// <summary>Walks every serialized object reference of <paramref name="obj"/> and swaps it for its realised version.</summary>
        public static void RemapReferences(Object obj, RealiseContext ctx, string ownerAssetPath)
        {
            var so = new SerializedObject(obj);
            var it = so.GetIterator();
            bool enter = true;
            while (it.Next(enter))
            {
                enter = true;
                switch (it.propertyType)
                {
                    case SerializedPropertyType.ObjectReference:
                        if (s_skipProps.Contains(it.name)) continue;
                        var v = it.objectReferenceValue;
                        if (v == null) continue;
                        var r = Realise(v, ctx, ownerAssetPath);
                        if (r != v) it.objectReferenceValue = r;
                        break;
                    case SerializedPropertyType.String:
                        enter = false;
                        break;
                    case SerializedPropertyType.Generic when it.isArray && s_primitiveElements.Contains(it.arrayElementType):
                        enter = false; // big data blobs (vertex arrays, curves...): no references inside
                        break;
                }
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static string HierarchyPath(GameObject go)
        {
            string path = go.name;
            for (var t = go.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
            return path;
        }
    }
}
#endif
