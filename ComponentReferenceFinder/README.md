# Component Reference Finder

Finds **who is holding a reference** to a Component / GameObject / asset — across open scenes, the
prefab stage, prefab assets, ScriptableObjects and even scenes that are not open.

Namespace: `RefFinder` — Editor only.

## Usage

| Entry point | Result |
|---|---|
| Right click a Component header ▸ **Find Refs** | who points at that component / its GameObject / its Transform |
| Right click a GameObject in the Hierarchy ▸ **Find Refs** | who points at that GameObject / Transform |
| Right click an asset in Project ▸ **Find Refs** | who points at that asset |
| **Tools ▸ Find Refs ▸ Find Refs (Selection)** — `Ctrl+Alt+F` | runs on the current selection |
| **Tools ▸ Find Refs ▸ Find Missing References** | lists missing scripts and missing object references |

Every result is a card with **Ping/Select**, **Open Code** and **Copy**. Right click a card for more:
open the prefab and select the exact child GameObject, open the scene, copy the asset path. The list
is virtualized, so a few thousand results still scroll smoothly.

The toolbar keeps what you reach for constantly (Scope, PrefabAssets, Only MonoBehaviours, search,
Only with Code, Log Hits, Rescan, Copy Paths). Everything advanced lives in **Options ▾**: scanning
`.asset` files, unopened scenes, **Runtime C#**, internal links, Turbo, bundle name, grouping,
Select All, Export CSV/JSON.

If you are in Play Mode with **Runtime C#** turned off, the window says so and offers an **Enable**
button — so a scan can never silently come back with zero results.

**Lock** keeps the current results and forces the next Find Refs into a new window, for side by side
comparison.

### Where Open Code jumps to

**Open Code** goes to the **field that actually holds the reference**, not the outermost field. The
tool follows the propertyPath by reflection down to the leaf `FieldInfo`, then looks for the
declaration line **inside the body of the class that declares that field**:

- `_slots.Array.data[3].button` jumps to `button` inside `class ItemSlot`, not to `_slots` on the
  MonoBehaviour.
- Because the search is scoped to that class body, two fields with the same name in the outer class
  and in a wrapper class are not confused.
- A field inherited from a base class opens the base class file.
- A UnityEngine field (for example `Button.m_OnClick`) has no `.cs`, so it reports
  `code: (not found)` instead of jumping somewhere random.

## Filter

Plain text matches every column. You can also target a single column:

```
scope:  type:  owner:  prop:  kind:  path:  code:  bundle:  detail:
re:<regex>          -term (exclude)

example:  scope:Prefab kind:UnityEvent -path:Legacy
```

## What it catches

- Plain object fields, **and** references inside `List<T>` / arrays / nested classes /
  `[SerializeReference]` / `ExposedReference`.
- **UnityEvent** (`Button.onClick`, …): the card reports `ref UnityEvent` and shows the **method
  name** being called.
- Prefab instance overrides (`m_Modifications.objectReference`) inside scene files.
- Nested prefabs and prefab variants, matched through the `GetCorrespondingObjectFromSource` chain.

Internal links (`m_GameObject`, `m_Father`, `m_Children`, `m_CorrespondingSourceObject`, …) are
**skipped** by default because they are noise — turn them back on in
**Options ▾ → Scan → Internal links**.

## "Runtime C#" — for prefabs instantiated at runtime (on by default)

Every phase above reads through `SerializedObject`, which means it **only sees fields Unity
serializes**. For an object created with `Instantiate()` at runtime, whatever holds it usually is
**not** serialized:

- `private List<GameObject> _pool;` (no `[SerializeField]`)
- `Dictionary<int, GameObject>` — Unity never serializes a Dictionary
- `static` fields on a manager / pool / registry
- closures from `onClick.AddListener(() => ...)` wired up at runtime

**Runtime C#** (`Options ▾ → Scan`) is **on** by default and only runs in **Play Mode**. It walks the
C# heap by reflection instead of going through serialization:

- **Roots**: every component in the loaded scenes plus DontDestroyOnLoad, every loaded
  ScriptableObject, and — if **include statics** is on — static fields of the project assemblies.
- From each root it only descends into **plain C# objects** (class/struct/List/Dictionary/array/
  delegate). When it meets a `UnityEngine.Object` it stops and compares, which is why results name
  the **direct holder** instead of exploding into the whole graph.
- The `propertyPath` line shows the **real field path**, for example `PoolService._pool[3].instance`
  or `UIManager.onClose -> HandleClose()`.

Every runtime result also resolves the `.cs` line where the holding field is declared, so **Open
Code** works and the **Only with Code** toggle does not filter them away.

The tool excludes itself from the heap scan (every type in namespace `RefFinder`): `FindRefsWindow`
is an `EditorWindow` and therefore also a `ScriptableObject`, so without the exclusion its own
`_target.primary` / `_target._match[]` / `_results[].ping` would show up as real references on
**every** scan.

Limits: depth 8, 400,000 objects, 20,000 elements per collection. Results are tagged with the
`Runtime` scope.

⚠️ **include statics**: reading a static field **triggers the static constructor** of that type if it
has not run yet. In Play Mode that is almost always harmless, but turn the toggle off if the project
has static constructors with side effects.

⚠️ Entering Play Mode clears the target (scene objects are destroyed and recreated). For objects that
only exist at runtime the correct order is: **enter Play Mode → wait for the spawn → right click the
component → Find Refs**.

## How the scan works (why it is fast)

1. **Hierarchy spreading outwards from the target** (`HierarchyWalk.cs`): instead of walking the scene
   from the first root, the tool starts at the selected GameObject and runs **three interleaved
   passes**, alternating one per step:
   - **inwards**: the target's subtree (children, grandchildren…);
   - **outwards**: climbing parents, each level also pulling in the siblings and their subtrees;
   - **wide**: everything else — the other roots **in the target's own scene**, then the prefab stage,
     then the other scenes if **All Loaded Scenes** is selected.

   The first two cover exactly the root tree containing the target and the third covers exactly the
   rest, so **they never overlap and no de-duplication table is needed**. References in the same
   panel or prefab appear on the first frame, while references in another scene do not have to queue
   behind an entire UI tree.

   These are two interleaved walks on the main thread, **not two real threads**: `SerializedObject`
   and every Unity scene API can only be called from the main thread.

   In Play Mode the **DontDestroyOnLoad** scene is always scanned at both Scope levels —
   `SceneManager` does not list it, so the tool finds it with a probe; most runtime UI and managers
   live there and missing it means zero results. The target's own scene is always scanned too, even
   when Scope is Current Scene.

2. **Text index**: every `.prefab` / `.asset` / `.unity` file is scanned off the main thread, in
   parallel, for the source prefab's `guid:` — producing a candidate list. Results are cached per
   guid and dropped when an asset is imported (`AssetTextIndexInvalidator`).

3. **Candidate assets**: only *those* files are loaded, with
   `AssetDatabase.LoadAllAssetsAtPath` (far cheaper than `LoadPrefabContents`), then walked through
   SerializedObject for the details.

4. **Unopened scenes**: the YAML is parsed off the main thread, which yields the property, the
   GameObject name and the line number without opening the scene.

The scan does **not** use a fixed time slice, it takes a **share of real time**: 25% per tick, clamped
to 4–16 ms (Turbo: 50%, clamped to 20–60 ms). The old fixed 4 ms slice was terrible in Play Mode — at
22 FPS the scan only got 4 ms × 22 = 89 ms per second (9% of the time) and took 11 times longer; a
share keeps ~25% regardless of frame rate.

`Transform` / `RectTransform` are skipped while **Internal links** is off: all of their object
references (`m_GameObject`, `m_Father`, `m_Children`, `m_CorrespondingSourceObject`,
`m_PrefabInstance`, `m_PrefabAsset`) are on the internal list and can never match — they are 35% of
all components but 14% of the scan time.

### Requirements and limits

- Scanning assets requires **Project Settings ▸ Editor ▸ Asset Serialization = Force Text** (this
  project is set that way). Otherwise the tool warns and skips that phase.
- If the target only exists **in a scene** (no source prefab), no asset can reference it, so the asset
  phase is skipped and the reason is stated.
- In an unopened scene, a reference pointing at *a prefab instance inside that scene* is written with
  a local fileID and **cannot be confirmed from text**. Such a line shows `(?)` = **Candidate** and
  needs the scene opened to be certain. References straight to a prefab asset are **Confirmed** as
  usual.
- The result cap is 20,000; hitting it is reported in the Status line.

## Compatibility

The code stays on **C# 9** and avoids every API that shifts between Unity versions:

- **No** `EntityId` / `GetEntityId()` (Unity 6.2 and newer only) — the cache uses
  `Dictionary<Object, …>`.
- **No** IMGUI `TreeView` (obsolete-as-error from Unity 6000.5, while the generic `TreeView<int>`
  does not exist on 2022) — the virtualized list in `FindRefsListView.cs` is hand written.
- `objectReferenceInstanceIDValue` (obsolete-as-error on 6000.5) is read through **reflection**, with
  a fallback to `objectReferenceEntityIdValue`; it only runs in Missing mode.

Compiles clean against the reference assemblies of **6000.5.3f1** and **6000.2.10f1**. Unity 2022 has
not been verified by compiling (that version is not installed here) — every API in use exists from
2021.3 on.

## Files

| File | Role |
|---|---|
| `ComponentReferenceFinder.cs` | menu entries |
| `ScanTarget.cs` | target description and matching, including the prefab source chain |
| `SerializedRefWalker.cs` | recursive SerializedObject walk, UnityEvent detection |
| `ReferenceScan.cs` | the scan job, split into phases, time sliced |
| `HierarchyWalk.cs` | three interleaved hierarchy passes spreading out from the target |
| `RuntimeHeapScanner.cs` | C# heap walk by reflection (Play Mode) |
| `AssetTextIndex.cs` | parallel off-thread guid scan plus cache |
| `YamlAssetScanner.cs` | YAML parsing for unopened scenes |
| `ScriptCodeLocator.cs` | resolves the leaf field from a propertyPath, then finds its declaration line in the `.cs` |
| `RefFilter.cs` | filter syntax |
| `FindRefsListView.cs` | virtualized card list |
| `FindRefsWindow.cs` | window, toolbar, CSV/JSON export |
| `RefTypes.cs` | result model and settings |
