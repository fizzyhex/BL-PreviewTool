#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace BLPTool.Realise
{
    /// <summary>
    /// Turns individual bundle assets into persistent project assets. Bundle meshes/textures are usually
    /// non-readable (GPU-only), so these read back from the GPU instead of the CPU copy that doesn't exist.
    /// </summary>
    public static class AssetExtractors
    {
        #region Mesh
        // Matches CapturedVertex in MeshCapture.shader: pos3 nrm3 tan4 col4 uv[8]*4
        const int CaptureFloats = 3 + 3 + 4 + 4 + 8 * 4;
        static Material s_captureMat;

        public static Mesh ExtractMesh(Mesh src, RealiseContext ctx)
        {
            Mesh dst;
            if (src.isReadable)
            {
                dst = Object.Instantiate(src);
            }
            else
            {
                dst = new Mesh { indexFormat = src.indexFormat };
                var attrs = src.GetVertexAttributes();
                const GraphicsBuffer.Target readableTargets = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource;

                // Skinned meshes get Raw/CopySource buffers for GPU skinning: copy every stream verbatim.
                if ((src.vertexBufferTarget & readableTargets) != 0)
                {
                    dst.SetVertexBufferParams(src.vertexCount, attrs);
                    for (int s = 0; s < src.vertexBufferCount; s++)
                    {
                        using var vb = src.GetVertexBuffer(s);
                        var bytes = new byte[vb.count * vb.stride];
                        vb.GetData(bytes);
                        dst.SetVertexBufferData(bytes, 0, 0, bytes.Length, s, NoChecks);
                    }
                }
                // Static meshes' vertex buffers can't be read back (and retargeting them wipes the data),
                // so draw the mesh once with a shader that streams decoded vertices into a UAV.
                else if (!CaptureVertices(src, dst, attrs, ctx))
                {
                    Object.DestroyImmediate(dst);
                    return null;
                }

                if (!CopyIndices(src, dst, ctx))
                {
                    Object.DestroyImmediate(dst);
                    return null;
                }

                // Bindposes and skin weights keep a CPU copy even on non-readable meshes.
                try { dst.bindposes = src.bindposes; } catch { /* not exposed */ }
                try
                {
                    var perVertex = src.GetBonesPerVertex();
                    if (perVertex.Length == src.vertexCount)
                        dst.SetBoneWeights(perVertex, src.GetAllBoneWeights());
                }
                catch (Exception e) { ctx.Report.Warnings.Add($"Mesh '{src.name}': bone weights not recoverable ({e.Message})"); }

                // UPGRADE NOTE (Unity 2022.1+): Mesh.GetBlendShapeBuffer / GetBlendShapeBufferRange and
                // Mesh.GetBoneWeightBuffer expose these GPU buffers directly. When the Marrow SDK moves past 2021.3,
                // read blendshape deltas from GetBlendShapeBuffer instead of the render-and-diff in CaptureBlendShapes.
                if (src.blendShapeCount > 0)
                    CaptureBlendShapes(src, dst, ctx);
                dst.bounds = src.bounds;
            }

            dst.name = src.name;
            return SaveAsset(dst, ctx.PathFor("Meshes", src.name, "asset"), ctx);
        }

        const MeshUpdateFlags NoChecks = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices
                                       | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds;

        static bool CopyIndices(Mesh src, Mesh dst, RealiseContext ctx)
        {
            var subs = Enumerable.Range(0, src.subMeshCount).Select(src.GetSubMesh).ToArray();
            int total = subs.Length == 0 ? 0 : subs.Max(d => d.indexStart + d.indexCount);
            byte[] bytes = null;

            const GraphicsBuffer.Target readableTargets = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource;
            if ((src.indexBufferTarget & readableTargets) == 0)
            {
                // Index data keeps a CPU copy even on non-readable meshes; retargeting re-uploads it intact.
                src.indexBufferTarget |= GraphicsBuffer.Target.Raw;
            }
            try
            {
                using var ib = src.GetIndexBuffer();
                bytes = new byte[ib.count * ib.stride];
                ib.GetData(bytes);
                total = ib.count;
            }
            catch (Exception e)
            {
                ctx.Report.Failures.Add($"Mesh '{src.name}': index readback failed ({e.Message})");
                return false;
            }

            dst.SetIndexBufferParams(total, src.indexFormat);
            dst.SetIndexBufferData(bytes, 0, 0, bytes.Length, NoChecks);
            dst.subMeshCount = subs.Length;
            for (int i = 0; i < subs.Length; i++)
                dst.SetSubMesh(i, subs[i], NoChecks);
            return true;
        }

        static bool CaptureVertices(Mesh src, Mesh dst, VertexAttributeDescriptor[] attrs, RealiseContext ctx)
        {
            if (s_captureMat == null)
            {
                var shader = Shader.Find("Hidden/BLPTool/MeshCapture");
                if (shader == null || !shader.isSupported)
                {
                    ctx.Report.Failures.Add($"Mesh '{src.name}': MeshCapture shader unavailable/unsupported on this GPU");
                    return false;
                }
                s_captureMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }

            int vc = src.vertexCount;
            var data = new float[vc * CaptureFloats];
            for (int v = 0; v < vc; v++) data[v * CaptureFloats] = float.NaN; // sentinel: "not written"

            var subs = Enumerable.Range(0, src.subMeshCount).Select(src.GetSubMesh).ToArray();
            using (var buf = new ComputeBuffer(vc, CaptureFloats * sizeof(float), ComputeBufferType.Structured))
            {
                buf.SetData(data);
                var rt = RenderTexture.GetTemporary(1, 1, 0);
                var prev = RenderTexture.active;
                try
                {
                    // SV_VertexID's treatment of BaseVertexLocation is driver-dependent: try without an offset first,
                    // then redo offset sub-meshes if they left holes.
                    Draw(src, subs, buf, rt, useBaseVertex: false);
                    buf.GetData(data);
                    if (HasHoles(data, vc) && subs.Any(s => s.baseVertex != 0))
                    {
                        Draw(src, subs, buf, rt, useBaseVertex: true);
                        buf.GetData(data);
                    }
                }
                finally
                {
                    Graphics.ClearRandomWriteTargets();
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                }
            }

            int holes = 0;
            for (int v = 0; v < vc; v++)
                if (float.IsNaN(data[v * CaptureFloats])) { holes++; for (int f = 0; f < CaptureFloats; f++) data[v * CaptureFloats + f] = 0; }
            if (holes == vc)
            {
                ctx.Report.Failures.Add($"Mesh '{src.name}': GPU capture produced no vertices");
                return false;
            }
            if (holes > 0)
                ctx.Report.Warnings.Add($"Mesh '{src.name}': {holes}/{vc} vertices unreferenced by any triangle (zeroed)");

            Vector3 V3(int v, int o) => new Vector3(data[v * CaptureFloats + o], data[v * CaptureFloats + o + 1], data[v * CaptureFloats + o + 2]);
            Vector4 V4(int v, int o) => new Vector4(data[v * CaptureFloats + o], data[v * CaptureFloats + o + 1], data[v * CaptureFloats + o + 2], data[v * CaptureFloats + o + 3]);
            var range = Enumerable.Range(0, vc);

            dst.SetVertices(range.Select(v => V3(v, 0)).ToArray());
            foreach (var a in attrs)
            {
                switch (a.attribute)
                {
                    case VertexAttribute.Position: break;
                    case VertexAttribute.Normal: dst.SetNormals(range.Select(v => V3(v, 3)).ToArray()); break;
                    case VertexAttribute.Tangent: dst.SetTangents(range.Select(v => V4(v, 6)).ToArray()); break;
                    case VertexAttribute.Color: dst.SetColors(range.Select(v => (Color)V4(v, 10)).ToArray()); break;
                    case >= VertexAttribute.TexCoord0 and <= VertexAttribute.TexCoord7:
                        int ch = a.attribute - VertexAttribute.TexCoord0, off = 14 + ch * 4;
                        if (a.dimension <= 2) dst.SetUVs(ch, range.Select(v => (Vector2)V4(v, off)).ToArray());
                        else if (a.dimension == 3) dst.SetUVs(ch, range.Select(v => (Vector3)V4(v, off)).ToArray());
                        else dst.SetUVs(ch, range.Select(v => V4(v, off)).ToArray());
                        break;
                    case VertexAttribute.BlendWeight or VertexAttribute.BlendIndices:
                        break; // copied from the CPU-side skin data in ExtractMesh
                    default:
                        ctx.Report.Warnings.Add($"Mesh '{src.name}': vertex attribute {a.attribute} not captured");
                        break;
                }
            }
            return true;
        }

        static void Draw(Mesh src, SubMeshDescriptor[] subs, ComputeBuffer buf, RenderTexture rt, bool useBaseVertex)
        {
            Graphics.SetRenderTarget(rt);
            Graphics.ClearRandomWriteTargets();
            Graphics.SetRandomWriteTarget(1, buf, false);
            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i].topology != MeshTopology.Triangles && subs[i].topology != MeshTopology.Quads
                    && subs[i].topology != MeshTopology.Lines && subs[i].topology != MeshTopology.LineStrip
                    && subs[i].topology != MeshTopology.Points) continue;
                s_captureMat.SetInteger("_BaseVertex", useBaseVertex ? subs[i].baseVertex : 0);
                s_captureMat.SetPass(0);
                Graphics.DrawMeshNow(src, Matrix4x4.identity, i);
            }
        }

        static bool HasHoles(float[] data, int vc)
        {
            for (int v = 0; v < vc; v++) if (float.IsNaN(data[v * CaptureFloats])) return true;
            return false;
        }

        /// <summary>
        /// Blendshape deltas have no readable copy on a non-readable mesh (in 2021.3). Recover them by GPU-skinning the
        /// mesh with its bones placed exactly at the bind pose (so skinning is the identity), once per shape frame,
        /// and diffing the skinned output buffer against the neutral pose.
        /// </summary>
        static void CaptureBlendShapes(Mesh src, Mesh dst, RealiseContext ctx)
        {
            int vc = src.vertexCount;
            bool f32 = src.GetVertexAttributeFormat(VertexAttribute.Position) == VertexAttributeFormat.Float32
                       && src.GetVertexAttributeStream(VertexAttribute.Position) == 0;
            if (!f32)
            {
                ctx.Report.Warnings.Add($"Mesh '{src.name}': {src.blendShapeCount} blendshapes skipped (non-float32 positions)");
                return;
            }

            var root = new GameObject("BLP_BlendCapture") { hideFlags = HideFlags.HideAndDontSave };
            var camGo = new GameObject("BLP_BlendCaptureCam") { hideFlags = HideFlags.HideAndDontSave };
            var rt = RenderTexture.GetTemporary(16, 16, 16);
            var tempMat = new Material(ShaderRemap.Fallback) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var bindposes = src.bindposes;
                var bones = new Transform[bindposes.Length];
                for (int i = 0; i < bones.Length; i++)
                {
                    var bone = new GameObject("b" + i) { hideFlags = HideFlags.HideAndDontSave }.transform;
                    bone.SetParent(root.transform, false);
                    var m = bindposes[i].inverse;
                    bone.SetPositionAndRotation(m.GetColumn(3), m.rotation);
                    bone.localScale = m.lossyScale;
                    bones[i] = bone;
                }

                var smr = root.AddComponent<SkinnedMeshRenderer>();
                smr.sharedMesh = src;
                smr.bones = bones;
                smr.updateWhenOffscreen = true;
                smr.forceMatrixRecalculationPerRender = true; // re-skin on every cam.Render(), not once per frame
                smr.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
                smr.sharedMaterials = Enumerable.Repeat(tempMat, src.subMeshCount).ToArray();

                var cam = camGo.AddComponent<Camera>();
                cam.targetTexture = rt;
                cam.enabled = false;
                var b = src.bounds;
                cam.transform.position = b.center - Vector3.forward * (b.extents.magnitude * 3 + 1);
                cam.transform.LookAt(b.center);
                cam.farClipPlane = b.extents.magnitude * 10 + 10;

                float[] Skin()
                {
                    cam.Render();
                    using var buf = smr.GetVertexBuffer();
                    if (buf == null) return null;
                    var data = new float[buf.count * buf.stride / 4];
                    buf.GetData(data);
                    return data;
                }

                for (int s = 0; s < src.blendShapeCount; s++) smr.SetBlendShapeWeight(s, 0);
                var neutral = Skin();
                if (neutral == null)
                {
                    ctx.Report.Warnings.Add($"Mesh '{src.name}': {src.blendShapeCount} blendshapes skipped (GPU skinning output unavailable)");
                    return;
                }
                int stride = neutral.Length / vc; // floats per skinned vertex: pos3 [nrm3] [tan4]
                bool hasN = stride >= 6, hasT = stride >= 10;

                for (int s = 0; s < src.blendShapeCount; s++)
                {
                    for (int f = 0; f < src.GetBlendShapeFrameCount(s); f++)
                    {
                        float w = src.GetBlendShapeFrameWeight(s, f);
                        smr.SetBlendShapeWeight(s, w);
                        var posed = Skin();
                        var dp = new Vector3[vc];
                        var dn = hasN ? new Vector3[vc] : null;
                        var dt = hasT ? new Vector3[vc] : null;
                        for (int v = 0; v < vc; v++)
                        {
                            int o = v * stride;
                            dp[v] = new Vector3(posed[o] - neutral[o], posed[o + 1] - neutral[o + 1], posed[o + 2] - neutral[o + 2]);
                            if (hasN) dn[v] = new Vector3(posed[o + 3] - neutral[o + 3], posed[o + 4] - neutral[o + 4], posed[o + 5] - neutral[o + 5]);
                            if (hasT) dt[v] = new Vector3(posed[o + 6] - neutral[o + 6], posed[o + 7] - neutral[o + 7], posed[o + 8] - neutral[o + 8]);
                        }
                        dst.AddBlendShapeFrame(src.GetBlendShapeName(s), w, dp, dn, dt);
                    }
                    smr.SetBlendShapeWeight(s, 0);
                }
            }
            catch (Exception e)
            {
                ctx.Report.Warnings.Add($"Mesh '{src.name}': blendshape capture failed ({e.Message})");
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(camGo);
                Object.DestroyImmediate(tempMat);
                RenderTexture.ReleaseTemporary(rt);
            }
        }
        #endregion

        #region Texture
        public static Texture2D ExtractTexture2D(Texture2D src, RealiseContext ctx)
        {
            Texture2D dst;
            if (src.isReadable)
            {
                dst = Object.Instantiate(src);
            }
            else
            {
                var gf = src.graphicsFormat;
                bool srgb = GraphicsFormatUtility.IsSRGBFormat(gf);
                bool hdr = GraphicsFormatUtility.IsHalfFormat(gf) || GraphicsFormatUtility.IsIEEE754Format(gf)
                           || src.format == TextureFormat.BC6H;
                int w = src.width, h = src.height;
                bool mips = src.mipmapCount > 1;

                // Blit decodes the GPU texture; an sRGB target re-encodes so stored bytes match the source.
                var rt = RenderTexture.GetTemporary(w, h, 0, hdr ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32,
                                                     srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
                var prev = RenderTexture.active;
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var raw = new Texture2D(w, h, hdr ? TextureFormat.RGBAHalf : TextureFormat.RGBA32, mips, !srgb);
                raw.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                raw.Apply(true);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                dst = raw;

                // Restore the original storage format, so size and sampling match the game.
                TextureFormat target = Uncrunched(src.format);
                if (target != raw.format)
                {
                    try
                    {
                        if (GraphicsFormatUtility.IsCompressedFormat(gf))
                            EditorUtility.CompressTexture(raw, target, TextureCompressionQuality.Normal);
                        else
                        {
                            var conv = new Texture2D(w, h, target, mips, !srgb);
                            conv.SetPixels(raw.GetPixels());
                            conv.Apply(true);
                            Object.DestroyImmediate(raw);
                            dst = conv;
                        }
                    }
                    catch (Exception e)
                    {
                        ctx.Report.Warnings.Add($"Texture '{src.name}': kept as {dst.format}, couldn't convert to {target} ({e.Message})");
                    }
                }
            }

            dst.name = src.name;
            dst.wrapModeU = src.wrapModeU;
            dst.wrapModeV = src.wrapModeV;
            dst.wrapModeW = src.wrapModeW;
            dst.filterMode = src.filterMode;
            dst.anisoLevel = src.anisoLevel;
            dst.mipMapBias = src.mipMapBias;
            return SaveAsset(dst, ctx.PathFor("Textures", src.name, "asset"), ctx);
        }

        static TextureFormat Uncrunched(TextureFormat f) => f switch
        {
            TextureFormat.DXT1Crunched => TextureFormat.DXT1,
            TextureFormat.DXT5Crunched => TextureFormat.DXT5,
            TextureFormat.ETC_RGB4Crunched => TextureFormat.ETC_RGB4,
            TextureFormat.ETC2_RGBA8Crunched => TextureFormat.ETC2_RGBA8,
            _ => f,
        };

        public static Sprite ExtractSprite(Sprite src, RealiseContext ctx)
        {
            var tex = Realiser.Realise(src.texture, ctx) as Texture2D;
            if (tex == null || !Realiser.IsProjectAsset(tex))
            {
                ctx.Report.Failures.Add($"Sprite '{src.name}': texture couldn't be realised");
                return null;
            }
            Rect rect = src.packed ? src.textureRect : src.rect;
            var dst = Sprite.Create(tex, rect, new Vector2(src.pivot.x / src.rect.width, src.pivot.y / src.rect.height),
                                    src.pixelsPerUnit, 0, SpriteMeshType.FullRect, src.border);
            dst.name = src.name;
            return SaveAsset(dst, ctx.PathFor("Sprites", src.name, "asset"), ctx);
        }
        #endregion

        #region Material
        public const string OriginalShaderTag = "BLPOriginalShader";

        public static Material ExtractMaterial(Material src, RealiseContext ctx)
        {
            var shader = ShaderRemap.Remap(src.shader, ctx);
            var dst = new Material(shader) { name = src.name };
            dst.CopyPropertiesFromMaterial(src);
            dst.shader = shader; // CopyPropertiesFromMaterial doesn't touch the shader, but be explicit
            dst.shaderKeywords = src.shaderKeywords;
            dst.renderQueue = src.renderQueue;
            dst.enableInstancing = src.enableInstancing;
            dst.doubleSidedGI = src.doubleSidedGI;
            dst.globalIlluminationFlags = src.globalIlluminationFlags;
            if (shader.name != src.shader.name)
                dst.SetOverrideTag(OriginalShaderTag, src.shader.name);

            ctx.Map[src] = dst; // before recursing, in case anything cycles back to this material
            foreach (string prop in src.GetTexturePropertyNames())
            {
                var tex = src.GetTexture(prop);
                if (tex != null)
                    dst.SetTexture(prop, Realiser.Realise(tex, ctx) as Texture);
            }
            // Saved last, so the file is written with references that already have GUIDs.
            return SaveAsset(dst, ctx.PathFor("Materials", src.name, "mat"), ctx);
        }
        #endregion

        #region Audio
        public static AudioClip ExtractAudio(AudioClip src, RealiseContext ctx)
        {
            if (src.loadState != AudioDataLoadState.Loaded)
            {
                // normally preloaded by BLPApi.PreloadAudio; this is the fallback for clips found some other way
                src.LoadAudioData();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (src.loadState == AudioDataLoadState.Loading && sw.ElapsedMilliseconds < 2000)
                    System.Threading.Thread.Sleep(5);
            }
            var samples = new float[src.samples * src.channels];
            if (samples.Length == 0 || !src.GetData(samples, 0) || samples.All(s => s == 0))
            {
                ctx.Report.Failures.Add($"AudioClip '{src.name}': sample data unavailable (streamed or compressed-in-memory clip)");
                return null;
            }

            string path = ctx.PathFor("Audio", src.name, "wav");
            WriteWav(path, samples, src.channels, src.frequency);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(path) is AudioImporter imp)
            {
                imp.loadInBackground = src.loadInBackground;
                imp.preloadAudioData = src.preloadAudioData;
                var settings = imp.defaultSampleSettings;
                settings.loadType = src.loadType;
                imp.defaultSampleSettings = settings;
                imp.SaveAndReimport();
            }
            ctx.Report.Assets++;
            return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        }

        static void WriteWav(string path, float[] samples, int channels, int frequency)
        {
            using var fs = new FileStream(path, FileMode.Create);
            using var bw = new BinaryWriter(fs);
            int byteCount = samples.Length * 2;
            bw.Write(new[] { 'R', 'I', 'F', 'F' }); bw.Write(36 + byteCount);
            bw.Write(new[] { 'W', 'A', 'V', 'E' }); bw.Write(new[] { 'f', 'm', 't', ' ' });
            bw.Write(16); bw.Write((short)1); bw.Write((short)channels); bw.Write(frequency);
            bw.Write(frequency * channels * 2); bw.Write((short)(channels * 2)); bw.Write((short)16);
            bw.Write(new[] { 'd', 'a', 't', 'a' }); bw.Write(byteCount);
            foreach (float s in samples) bw.Write((short)(Mathf.Clamp(s, -1f, 1f) * short.MaxValue));
        }
        #endregion

        #region Generic
        /// <summary>Animator state machine internals etc.: these must live inside their owning asset.</summary>
        static bool IsSubAssetType(Object o) => o.GetType().Namespace == "UnityEditor.Animations" && o is not RuntimeAnimatorController;

        /// <summary>
        /// ScriptableObjects (data cards, surface data...), AnimationClips, PhysicMaterials, Avatars, etc.:
        /// readable natively, so clone, save, then remap their own references.
        /// </summary>
        public static Object ExtractGeneric(Object src, RealiseContext ctx, string ownerAssetPath)
        {
            if (src is ScriptableObject && src.GetType() == typeof(ScriptableObject))
            {
                ctx.Report.MissingScripts.Add($"ScriptableObject '{src.name}' (script has no ExtSDK match)");
                return null;
            }
            if (src is Texture or Cubemap)
            {
                ctx.Report.Failures.Add($"{src.GetType().Name} '{src.name}': unsupported texture kind (only Texture2D is extracted)");
                return src;
            }

            Object copy;
            try
            {
                if (src is ScriptableObject)
                {
                    // CreateInstance binds the project's MonoScript; Instantiate would keep the bundle's (pathless) one,
                    // and swapping m_Script afterwards recreates the native object out from under us.
                    copy = ScriptableObject.CreateInstance(src.GetType());
                    EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(src), copy);
                }
                else copy = Object.Instantiate(src);
            }
            catch (Exception e)
            {
                ctx.Report.Failures.Add($"{src.GetType().Name} '{src.name}': can't be cloned ({e.Message})");
                return src;
            }
            copy.name = src.name;
            ctx.Map[src] = copy; // before recursing, so cycles resolve to this copy

            if (IsSubAssetType(src) && ownerAssetPath != null)
            {
                copy.hideFlags = HideFlags.HideInHierarchy;
                AssetDatabase.AddObjectToAsset(copy, ownerAssetPath);
                ctx.Report.Assets++;
                Realiser.RemapReferences(copy, ctx, ownerAssetPath);
                return copy;
            }

            string ext = copy switch
            {
                AnimationClip => "anim",
                PhysicMaterial => "physicMaterial",
                AnimatorOverrideController => "overrideController",
                UnityEditor.Animations.AnimatorController => "controller",
                _ => "asset",
            };
            string kind = copy is ScriptableObject ? "ScriptableObjects" : copy.GetType().Name + "s";
            string assetPath = ctx.PathFor(kind, src.name, ext);

            if (copy is UnityEditor.Animations.AnimatorController ac)
            {
                // state machines etc. become sub-assets, which needs the owner file to exist first
                SaveAsset(copy, assetPath, ctx);
                Realiser.RemapReferences(copy, ctx, assetPath);
                if (ac.layers.Length == 0)
                    ctx.Report.Warnings.Add($"AnimatorController '{src.name}': no editable layers (player builds strip state machines)");
                return copy;
            }

            // Remap first (this also swaps the bundle MonoScript for the project one), then write the file once.
            Realiser.RemapReferences(copy, ctx, assetPath);
            return SaveAsset(copy, assetPath, ctx);
        }
        #endregion

        static T SaveAsset<T>(T asset, string path, RealiseContext ctx) where T : Object
        {
            AssetDatabase.CreateAsset(asset, path);
            ctx.Created.Add(asset);
            ctx.Report.Assets++;
            return asset;
        }
    }
}
#endif
