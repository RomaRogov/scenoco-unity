# Scenoco: architecture

Unity scenes, prefabs and assets as text an agent and a person can both edit. This page says which
class does what, and why the responsibilities are split the way they are. Each source file starts
with a comment that repeats its part of this page.

## The one rule everything follows

**A value goes in the way a script would set it, and nothing translates it.** On Unity's
components an attribute is a C# property, set through its own setter — so whatever else the
setter does (turn `useMotor` on, clamp a field of view, move `sizeDelta` for an `offsetMin`)
happens as it does in a game's code — recorded with `Undo.RecordObject`, as an editor script
records it. On a game's scripts an attribute is a serialized field, set through
`SerializedObject` as the Inspector sets it; a script's properties are its code, never called.

One property goes through the Editor rather than its setter: a Transform's `localEulerAngles` is
read and set as the Inspector's rotation (`TransformUtils.Get/SetInspectorRotation`), which keeps
the angles as written next to the quaternion, so a rotation reads back as the degrees that made
it. Through the getter and setter, only 5 of 206 rotations came back as the same quaternion; the
rest came back as the same turn in noisy or other angles (`50 -30 0` as `50.00001 330 -0.0000013`).

What a property stores is never looked up: it's observed. A snapshot of the object's serialized
data before the setter and one after say what it changed (`Members.Changed`). That's how a value
that stores nothing is refused, and how the reader tells which property to write for a stored
field.

## Layers

```
  ┌────────────────────────────────────────────────────────────────────────────────────┐
  │ Sync/       a scene or prefab and its text twin, kept in step both ways (uses all)   │
  └────────────────────────────────────────────────────────────────────────────────────┘
            text                                                    Unity objects
  ┌──────────────────────┐                                  ┌─────────────────────────┐
  │ Document/   XLinq     │  ← parse / write →               │  GameObjects, components │
  └──────────┬───────────┘                                  │  ScriptableObjects, …    │
             │ elements                                      └────────────▲────────────┘
  ┌──────────▼───────────────────────────────────────────────────────────┴────────────┐
  │ Assets/     one adapter per file kind: GameObjectTreeAdapter, ScriptableObjectAdapter │
  └──────────┬──────────────────────────────────────────────────────────────────────────┘
             │ one element per object
  ┌──────────▼──────────────────────────────────────────┐
  │ Objects/  Members: the names an object takes, and     │
  │             what setting one changes (snapshots)      │
  │           ObjectWriter, ObjectReader, Defaults        │
  │           BuildScene                                  │
  └──────────┬──────────────────────────────────────────┘
             │ one value per property or field
  ┌──────────▼──────────────────────┐
  │ Values/   ValueText (single)     │
  │           StructuredValues       │
  │           UnityEventValue        │
  │           References, FieldTypes │
  └─────────────────────────────────┘
```

`Files` (the facade) picks the adapter for a file's root element and creates the context
(`ImportContext` / `ExportContext`) that carries the references down
through the layers, so no layer reaches for global state. `Sync/` sits on top of all of it: it keeps
a scene or prefab and its text twin in step, using the layers above to read and write objects.

### Document/ — the text

There is no parser or writer of our own: a file is a `System.Xml.Linq` tree (`XElement`,
`XAttribute`, `XComment`), and every layer works on those types directly.

| Class | Responsibility |
|---|---|
| `Markup` | `Parse` loads text keeping comments, whitespace and line numbers as nodes of the tree. `Print` writes a parsed (and edited) tree back, so untouched nodes come out as written; `Write` writes a new tree in the house style. `Insert` / `Remove` edit a parsed tree in place, giving a new element its siblings' indentation. Small extensions say what the layers ask of an element: `Attr`, `Tag`, `FieldIn(parent)` (`<Wave.enemies>` inside `<Wave>`), `Line`. Knows nothing about Unity. |
| `ScenocoException` | Every error: `file:line: what's wrong`, and when known, what to write instead. |

What the library normalizes when a file is rewritten: attribute values are double-quoted with one
space between attributes, and the lines around the root (a declaration, leading comments) are one
per line. Everything else a person wrote stays.

### Values/ — how a value is written

| Class | Responsibility |
|---|---|
| `ValueText` | Single values as attribute text, for a C# value of a property's type and for a serialized field: numbers, vectors, colours, a Quaternion's four numbers, enums by name, LayerMask by layer names, references, lists of references or numbers. The text is the value, nothing derived. |
| `StructuredValues` | Property elements. A property's struct, list, curve or gradient through the C# type's public members (`<JointMotor2D motorSpeed="30" />`), and lists a type gives through `Get{Names}`/`Set{Names}` and a `{name}Count` (a module's `bursts`). A struct that converts implicitly from a single value (a `MinMaxCurve` from a float, a `MinMaxGradient` from a Color) is written as that value when that's what it is (`startSize="2"`); `Shorten` finds where the short form stores the same; a field's list, nested class or `[SerializeReference]` item (named by its concrete class) through its serialized children, by their stored names. |
| `UnityEventValue` | Persistent listeners, `<Call target method [argument]>`, written into `m_PersistentCalls` like the Inspector's event drawer. |
| `ReferenceTable`, `ReferenceNames` | `#Name`, `#Parent/Child`, `#id` and asset paths, in and out. Ambiguity is an error, never a first match; export adds `id` only where it's needed. |
| `FieldTypes` | The C# type behind a serialized property, by reflection only: which enum, which list element type, which `[SerializeReference]` class, whether it's an event. |

### Objects/ — one Unity object

| Class | Responsibility |
|---|---|
| `Members` | The names an element of an object can write: a Unity type's public properties with a setter (its own before its base types', not obsolete, not `X` where `sharedX` exists — `X` makes a copy when read in the Editor), and serialized fields — all of a script's; of Unity's, the ones the Inspector shows, plus the GameObject header's `m_IsActive` (the rest is Unity's own state: bake output, auto-tiling). `Units` splits an object's stored data into values (a struct field into its members); `Changed` compares two snapshots; `GetValue`/`SetValue` are the getter and setter, except a Transform's `localEulerAngles` (the Inspector's rotation); `Probe` sets a property on a scratch copy and says what it stored. `Handles` are read-only properties whose value is a struct to set members on, which sets the object (a ParticleSystem's modules) — none is listed: the writer refuses a struct that turns out to be a copy. Bookkeeping fields (hide flags, prefab links, the Inspector's Euler hint) are never written; hierarchy fields (`m_Father`) mean a property is structure. |
| `ObjectWriter` | One element into one object, in the order written, attributes then property elements. A property through its setter, between two snapshots: nothing stored while the getter changed is runtime state, refused; a hierarchy field changed is refused (nesting is the hierarchy); an error Unity logs during the setter is the value's error; a value that reads back differently is reported at its line. A handle's members on what its getter returns, in the order written. A field through `SerializedObject`. `Reset` takes a name back to its baseline: a fresh object's value, or a placement's prefab's (its overrides reverted). |
| `ObjectReader` | One object into one element: each stored value that differs from its baseline (a fresh object, or a placed prefab's own), written once. Unity's types under a property: properties are tried on a scratch copy of the baseline in the order the writer will set them, each with this object's value, and written when the stored data comes out as this object's — the property named like the field first (`localPosition` for `m_LocalPosition`), then declaration order. Then handles' members, the same way, one member at a time (`<ParticleSystem.main> <MainModule duration="2" />`). What neither gives is written as the field, a struct's differing members only. |
| `Defaults` | Fresh objects of each type, made as the writer makes them (ObjectFactory, so presets apply), and a scratch copy per type for `Members.Probe`, cached for one operation: every `Files` call and twin pass opens one (`Defaults.Operation()`), and the outermost one's end destroys them. Not left for a script reload, which can't destroy some of them (see below). |
| `BuildScene` | The preview scene detached objects are built in, and the one way any GameObject is created (`CreateIn`), so nothing ever marks a scene a person has open as modified. Before a script reload it's closed only when empty: Unity refuses to destroy some objects during a reload, and closing a preview scene that still holds them never returns — it logs "Dereferencing NULL ImmediatePtr!" until the disk fills. A hidden scene left behind costs nothing. |

### Assets/ — one kind of file

| Class | Responsibility |
|---|---|
| `IAssetAdapter` | The contract: which root elements it reads, which objects it writes, build and describe. |
| `GameObjectTreeAdapter` | `<Prefab>` and `<Scene>`, and a loaded scene as text. Builds in three passes (structure, GameObject values, component values) so a reference never depends on file order. A GameObject's element names it (`name`, `id`); its values are GameObject's properties and fields; its Transform is a component element, written when it holds something. |
| `PrefabInstances` | A prefab placed in a scene or in another prefab (`<PrefabInstance prefab="…">`), and a variant (`<Prefab base="…">`), each holding only its overrides, including `removedComponents` / `removedGameObjects`. The one place that asks Unity what belongs to a prefab: which object a value is compared with or goes back to, which child an element means, how an override is reverted, what was removed and how it comes back (`PrefabUtility.GetRemovedComponents`, `RemovedComponent.Revert`, …). |
| `ScriptableObjectAdapter` | `<Asset type="…">`: one data asset, through the same ObjectWriter and ObjectReader as a component. `Fields` reads any object's serialized values this way, for `Files.Describe`. |
| `AssetText` | Read-only text for assets whose serialized data says little to a reader, through Unity's API: a Material's shader properties, an AnimationClip's curves and events. |

### Sync/ — an asset and its text twin

`Level1.unity` stays what Unity opens and plays; `Level1.uscene` is the same scene as text.
`Enemy.prefab` and `Enemy.uprefab` are a twin the same way. A base is kept per twin in
`Library/Scenoco`: the text as of the last time the two agreed, and the asset's export at that
moment, each with the key of every object in it (its local file id). Both directions are a diff
against that base, which is why a save rewrites only what changed and why "1.0" in a file isn't
turned into "1".

| Class | Responsibility |
|---|---|
| `Twins` | Every `*.uscene` and `*.uprefab` under `Assets/`: checked twice a second (prefabs before scenes), on scene save, when Unity reimports a prefab, and before entering Play mode. A twin that failed is tried again once anything else lands. The calls agents and menus use (`Track`, `Sync`, `Status`, `Resolve`, `Open`). |
| `Twin` | One pair and its base. The three flows: the file was edited → applied to the asset; the asset was saved → written into the file; both changed → a conflict that waits for `keep`, `file` or `merge`. Written against `TwinTarget`, so scenes and prefabs sync the same way. |
| `TwinTarget`, `LoadedAsset` | The Unity side of a twin: create an empty asset, say whether a person has it open, hand over its objects loaded. `LoadedAsset` is those objects while loaded: each by key, the export as text, unsaved work, how to save. |
| `SceneTarget` | A scene: the loaded one when it's open, else opened additively, saved and closed. |
| `PrefabTarget` | A prefab: the objects being edited when it's open in Prefab Mode, else its contents loaded in isolation, saved and unloaded. File ids are kept either way. |
| `KeyedTree`, `TreeMatcher` | A text tree with each object element paired to its object's key. The matcher gives an edited tree its keys: same name under the same parent, then same components (a rename), then a unique name elsewhere (a move); the rest is new. An ordinary object never matches a placed prefab, nor a placement of another prefab. |
| `SceneDiff`, `Change` | What changed between two keyed trees, as changes both sides speak: an attribute set or reset, a property element, a component or object added, moved or removed. `Conflicts` pairs changes that can't both be kept. |
| `TextPatch` | Changes → an edit of the file's tree: only the attribute, element or object a change names. A value the file writes under another name that stores the same data (Unity says so: setting it on a fresh object changes what the other reads, and both store the same fields) is replaced in its place. |
| `SceneApply` | Changes → an edit of the objects, as the Editor makes edits (ObjectFactory, Undo, setters and SerializedObject through ObjectWriter), as one Undo step. A name the text no longer writes goes back to a fresh object's value — or, in a placed prefab, to the prefab's (a revert, never a removal). |
| `ConflictMarkers` | A clash written into the file by `git merge-file`, in the markers every editor and agent knows. Resolving it is an edit of the file, which is then applied like any other. |
| `TwinWindow` | The person's side of a conflict: a non-modal window that opens without focus, with the same three answers. |

Nothing in Sync opens a modal dialog. A conflict is a status: shown in the window and the console
for a person, and returned by `Files.Status()` for an agent.

## Other assets: read as text, changed in C#

Only scenes and prefabs have text twins. They're deep, cross-referenced and layered with overrides,
and they change in two places at once: an open Editor and a file an agent edits. Other assets are
small and flat, and Unity's own API already edits them safely. So they're **read** as text with
`Files.Describe`, and **changed** in C# (`unity command script eval`) through `SerializedObject`
or the type's API, then saved:

| Asset | Read as (`Files.Describe`) | Changed with |
|---|---|---|
| Scene, prefab | its twin's text | the twin, or C# |
| ScriptableObject, PhysicsMaterial2D, … | `<Asset type="…">`: serialized values that differ from a fresh one (`ScriptableObjectAdapter.Fields`) | `SerializedObject`, `EditorUtility.SetDirty`, `AssetDatabase.SaveAssets` |
| Material | the shader's properties that differ from a fresh material of that shader (`AssetText`) | `Material.SetColor/SetFloat/SetTexture` |
| AnimationClip | curves by binding, as the Animation window lists them, and events (`AssetText`) | `AnimationUtility.SetEditorCurve`, `SetAnimationEvents` |
| Import settings | `Files.Describe(AssetImporter.GetAtPath(path))`, in full (an importer has no fresh state to compare with) | the importer's properties, then `SaveAndReimport()` |

A kind of asset gets a twin only when agents edit it as often as scenes. Game data authored in bulk
(levels, waves) is the likely first, and its import and export already exist (`ScriptableObjectAdapter`).

## Verification

- `Tests/Editor/ScenocoTests.cs` (21 EditMode tests): a golden round-trip of built-in components (`Samples/BuiltIns.uprefab`) and their meaning checked through Unity's getters — a camera, a light's shadows, physics with a centre of mass whose setter turns automatic off, a built-in mesh, an inactive object, UI anchors and offsets set in order, a UIDocument, a hinge whose motor setter turns the motor on; exact names and the error listing what a type takes, a read-only property, the hierarchy refused, a Quaternion's four numbers, runtime state refused, Unity's own setter error at its line, a value the setter changes reported; duplicate names and ids; a ScriptableObject; references Unity stores under other names (`AudioSource.resource`, `Animator.runtimeAnimatorController`); `Files.Describe` of a Material, an AnimationClip, an asset on disk, a scene and import settings; a ParticleSystem's modules, short values and bursts, and a copy refused as a handle.
- `Tests/Editor/UGUI/` (1 EditMode test), its own assembly compiled only when the project has `com.unity.ugui`: an Image with a built-in sprite and a Button event round-trip. Nothing else needs a UI package; the core tests use UI Toolkit's `UIDocument`.
- `Tests/Editor/SyncTests.cs` (16 EditMode tests), on throwaway scenes opened next to whatever is open (`TwinFixture`):
  - the first text of a scene holds only non-default values
  - a save changes only what changed in the file (comments, blank lines and order kept); a value the file writes under another name is replaced in its place; a rotation turned in the Inspector is written in degrees
  - an edited file is applied to an open scene as one Undo step, and to a closed scene
  - a file with a text error is reported with its line and not applied
  - each answer to a conflict: merge of different things, merge of the same thing (markers, then the resolution applied), keep, take the file
  - one edited value is exactly one change, and a component has one key however its type is written
  - nothing marks the active scene modified (checked a frame later, when Unity sets the flag)
- `Tests/Editor/PrefabTests.cs` (23 EditMode tests):
  - a prefab is created from its text; edited text changes it and its objects keep their file ids; a prefab changed in Unity is written into its text in place; the first text of an existing prefab
  - a prefab open in Prefab Mode takes an edited file, and unsaved Prefab Mode edits plus an edited file are a conflict that merges
  - placed prefabs are real placements whose text is only overrides, and read back say exactly what the file says; a changed prefab reaches placements that don't override it; an override removed from the text reverts; an override made in Unity is written into the placement
  - a prefab placed inside a prefab; a scene written before the prefab it places is built once the prefab exists, with nothing half-made left behind
  - removed components and children listed in the text are removed (as removed-component/-GameObject overrides) and read back the same; removed in Unity, they're listed in the placement; taken out of the list, they come back, and can be overridden in the same edit; a removal of something the prefab doesn't have is an error at its line
  - only the second of two same-type components overridden stays on the second
  - a variant is created from its text and read back the same; a changed base reaches it where it doesn't override; a variant made in Unity is tracked as overrides of its base; removing `base` unpacks it; changing `base` is an error that applies nothing; `Files.Import`/`Export` build and write a variant with its removals

## Not built yet

- **A variant's base can't be changed or given in place** (Unity refuses: its placements would lose their overrides). Removing `base` unpacks it.
- **Two same-type components on a prefab's object, only the second removed**: `removedComponents` names a component by its order among its type, so listing one `<BoxCollider2D />` means the first.
- **A placement's prefab can't be swapped** by editing `prefab="…"`: that's a new placement (the old one's overrides go with it).
- **Scene settings** (lighting, render settings): not in the text.
- **Edits inside a property element** (a list item changed in Unity) rewrite that whole property element, so comments inside it are lost.
- **Multi-line attributes** are joined onto one line when a file is rewritten (the XML library doesn't keep attribute spacing).
- **YAML blocks (format B).** Still converted by the eval's Python side.
- **Stored fields no property sets are written by their serialized names** (`m_IsActive`, `m_ScaleInLightmap`, and UnityEvents, which aren't single values: `<Button.m_OnClick>`).
- **A structured value's setter can turn on a switch an attribute set off** (`useMotor="false"` and `<HingeJoint2D.motor>`): attributes apply first. Read back, such an object is written by its stored fields instead.
- **Derived data Unity keeps is written when it differs**: a RectTransform's `localPosition` from its layout, a Screen Space canvas's own rect. Harmless — set again as Unity derives it.
- **A rotation set from a script** reads back in the angles Unity computes from its quaternion, which can carry float noise (`89.99999`); one made in the Inspector or the text reads back as written. Rotations are compared by angle (under 0.001°), not bit for bit: the same angles give quaternions a last digit apart by different paths.
- **The test runner's own prompt**: Unity's Test Framework asks to save modified scenes before a run. Scenoco never leaves a scene modified, but a person's unsaved work still triggers that prompt.

Worth knowing: any `.uscene` or `.uprefab` under `Assets/` is a twin (but in a folder Unity skips, ending
in `~`), so a text file with no asset creates one. Keep drafts and samples under another extension or outside `Assets/`.
