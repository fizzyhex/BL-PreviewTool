# BL-PreviewTool
MarrowSDK Addon (Patch 6) that remotely loads Spawnable, Level, and Avatar data from your bonelab install / Mods.

This fork adds **realising**: turning content loaded from game/mod AssetBundles into real project assets that
can be saved into prefabs and scenes.

> Dumped game assets are for local reference and inspection. Don't ship them in your pallets.

## Setup
Set your game install path in the Marrow SDK preferences (Asset Warehouse > game install directory) and enable
loading Marrow games, so the warehouse lists the game's pallets.

## Content Browser
`Stress Level Zero > Void Tools > (BLPTool) Content Browser` searches every crate in the warehouse:

- **Preview** (spawnables/avatars): realises the crate into the open scene.
- **Play / +Add** (levels): streams the level in play mode, replacing or keeping the open scenes.
- **Dump**: realises the crate and its assets into a folder.

`CONTEXT/CrateSpawner > (BLPTool) Realise Preview` realises a spawner's crate under it.

## Asset Warehouse integration
No Extended SDK changes needed: the fork extends the SDK's tree view and swaps it into open Asset Warehouse
windows. It uses reflection on the window's private `treeViewAW`/`treeViewState` fields, and logs a warning
and stays out of the way if those change.

- **Installed Mods**: a group after External Pallets listing the game's pallet mods
  (`LocalLow/Stress Level Zero/BONELAB/Mods`, overridable via the `BLPTool.ModsPath` EditorPref).
  It's lazy, because 50-150 mods is normal:
  - Expanding the group only reads each mod's `pallet.json` header.
  - Expanding a mod loads that pallet into the warehouse. *Expand to load all* loads the rest.
  - Search, "+" and Alt+click never load mods. The search text filters the unloaded mod rows.
  - Loaded mods are reloaded after domain reloads and play mode.
- **Preview Tool** (crate inspectors, under the header):
  - **Preview** loads spawnables/avatars into the scene, or plays a level.
  - **Realize** dumps the selection to a folder you pick (levels go through a play-mode capture).

## API (`BLPTool.BLPApi`, editor only)
```csharp
// Realised, unsaved GameObject in the active scene. Assets go to Assets/BLPTool_Temp/<title>/, so it can be saved.
UniTask<GameObject> PreviewSpawnable(Barcode barcode, Transform parent = null);

// Streams a level in play mode (enters play mode if needed). additive keeps the open scenes.
void PreviewLevel(Barcode barcode, bool additive = false);

// Realise + save under `path` (inside Assets/): spawnables/avatars -> .prefab, data cards -> .asset,
// levels -> .unity, pallets -> every spawnable/avatar crate plus the first level. Level dumps finish after a play-mode capture,
// so their report comes back Pending.
UniTask<RealiseReport> Dump(Barcode barcode, string path);
```
Each run writes `_realise_report.txt` next to its output, listing anything that couldn't be reproduced.

## How realising works
- **Components**: bundle MonoScripts resolve to the Extended SDK's types (same assembly/class names), so each
  component is re-added and its serialized data copied. References are remapped to the realised objects.
  Components without an SDK match become `BLPMissingScript` placeholders.
- **Meshes**: bundle meshes are GPU-only. Vertices are streamed out with a capture shader
  (`Editor/Realiser/MeshCapture.shader`). Indices, bindposes and bone weights are copied directly, and
  blendshapes are recovered by GPU-skinning each shape at the bind pose and diffing.
- **Textures**: blitted back from the GPU and re-compressed to the original format.
- **Materials**: shaders are swapped for the project shader of the same name. Unmatched shaders fall back to
  `SLZ/LitMAS/LitMAS Standard`, then URP Lit, and the original name is kept in the `BLPOriginalShader` tag.
- **Levels**: bundle scenes only open in play mode. The level is streamed, realised into a capture prefab,
  and rebuilt as a `.unity` scene back in edit mode.

## Known limits
- Animator controllers come out empty: player builds strip their state machines.
- Streamed audio clips (usually music/ambience) have no readable sample data.
- Only `Texture2D` is extracted (not cubemaps, 3D textures or arrays).
- Level dumps don't include baked lighting, lightmaps, probes, occlusion or NavMesh data.
- Single-mode scene changes unload the warehouse's in-memory crates until it reloads. Resolve crates before
  switching scenes.

## Upgrade notes
Unity 2022.1+ adds `Mesh.GetBlendShapeBuffer` / `GetBlendShapeBufferRange` and `Mesh.GetBoneWeightBuffer`.
When the Marrow SDK moves past 2021.3, read blendshape deltas from those buffers instead of the
render-and-diff in `AssetExtractors.CaptureBlendShapes`.
