# Scenoco (`com.roro.scenoco`)

Unity scenes, prefabs and data assets as readable text, in Unity's own C# vocabulary — for people and agents to edit, kept in step with the real scene both ways.

```xml
<Scene name="Level1">
  <!-- the player -->
  <GameObject name="Ball">
    <Transform localPosition="0 -4 0" />
    <CircleCollider2D radius="0.32" />
    <Rigidbody2D gravityScale="0" />
  </GameObject>
</Scene>
```

An attribute is an assignment a script would make: on Unity's components a C# property, set through its own setter (`gravityScale`, `localPosition`); on a game's scripts a serialized field by name. Nothing translates names or values in between.

## Install

Unity 6 or later. In the Package Manager, **+ ▸ Install package from git URL…**:

```
https://github.com/RomaRogov/scenoco-unity.git
```

or in `Packages/manifest.json`, `"com.roro.scenoco": "https://github.com/RomaRogov/scenoco-unity.git#v0.1.0"`.
To run its own tests, also add `"testables": ["com.roro.scenoco"]`. For an agent, `AGENTS.md` is the
card to read before writing a scene; installed from git it's in `Library/PackageCache/com.roro.scenoco@…/`.

## An asset and its text twin

`Level1.uscene` sits next to `Level1.unity`, and `Enemy.uprefab` next to `Enemy.prefab`. Unity keeps opening, playing and testing the asset; the text file is the same thing as text.

| What happens | What Scenoco does |
|---|---|
| A scene or prefab gets a twin (Assets ▸ Scenoco ▸ Create Text Twin, or `Files.Track`) | Writes every object, and of each only the values that differ from a fresh one. |
| The text is edited (by an agent, an IDE, a merge) | Within half a second the change is applied to the asset: as one Undo step if it's open (a loaded scene, a prefab in Prefab Mode), then saved; loaded in the background and saved if it isn't. A text file with no asset creates it. |
| The scene is saved, or the prefab changes, in Unity | Only what changed is written into the text. Comments, blank lines and the order things were written in stay. |
| Both changed (unsaved changes in Unity and an edited file) | Nothing is applied. The Scenoco window opens, without taking focus, asking to **keep my changes**, **merge**, or **take the file**. Merge combines changes to different things; changes to the same thing are written into the file as standard conflict markers. |

No modal dialog is ever shown, so an agent is never stopped. It works on the file, and when a person's unsaved work is in the way it sees that as a status and can answer it:

```csharp
Scenoco.Files.Track("Assets/Scenes/Level1.unity");               // make the twin (a .prefab works the same)
Scenoco.Files.Status();                                           // Synced / Conflict / Merging / Invalid, per twin
Scenoco.Files.Sync();                                             // apply edited files now instead of within half a second
Scenoco.Files.Resolve("Assets/Scenes/Level1.uscene", "merge");    // "keep" | "file" | "merge"
Scenoco.Files.Open("Assets/Scenes/Level2.unity");                 // open without a prompt and without discarding unsaved work
```

Text errors are reported as `file:line: what's wrong`, with what to write instead, and the scene is left as it was.

## Prefabs placed in a scene or in another prefab

A placement is written with only its differences from the prefab, which is Unity's own override rule:

```xml
<PrefabInstance prefab="Assets/Prefabs/Enemy.prefab" name="Boss">
  <Transform localPosition="4 0 0" />
  <Rigidbody2D mass="5" />                        <!-- an override on the prefab's component -->
  <GameObject name="Muzzle">                      <!-- an override on a child the prefab has -->
    <Transform localPosition="0 2 0" />
  </GameObject>
  <GameObject name="Flag" />                      <!-- a child the prefab doesn't have: added -->
</PrefabInstance>
```

- What's written is an override; what's left out keeps the prefab's value and follows the prefab when it changes.
- Removing an override from the text reverts it. A prefab's own component or child is never destroyed that way.
- Editing `Enemy.uprefab` changes the prefab without changing its objects' file ids, so every placement keeps its overrides.

What a placement doesn't have of its prefab is listed under the names Unity stores it with
(`m_RemovedComponents`, `m_RemovedGameObjects`), on the object it belongs to:

```xml
<PrefabInstance prefab="Assets/Prefabs/Enemy.prefab" name="Ghost">
  <PrefabInstance.removedComponents>
    <Rigidbody2D />                               <!-- the n-th of its type on the prefab's object -->
  </PrefabInstance.removedComponents>
  <PrefabInstance.removedGameObjects>
    <GameObject name="Muzzle" />                  <!-- the prefab's child of that name -->
  </PrefabInstance.removedGameObjects>
</PrefabInstance>
```

Taking an item out of the list brings the prefab's component or child back. On a child of the
placement the lists are `<GameObject.removedComponents>` and `<GameObject.removedGameObjects>`.

When a placement's object has two components of one type and only the second is overridden, the
first is written too, empty, so the override stays on the second.

## Prefab variants

A variant is a prefab whose root is a placement of its base, so its text is overrides of the base:

```xml
<Prefab base="Assets/Prefabs/Enemy.prefab" name="Boss">
  <Rigidbody2D mass="9" />
  <GameObject name="Crown" />
</Prefab>
```

A `.uprefab` with `base` creates a variant. Removing `base` unpacks it into a prefab of its own, as
Unity's Unpack does. Changing `base` is an error, because Unity can't swap a variant's base without
dropping its placements' overrides.

## Reading any other asset

Only scenes and prefabs have text twins. Anything else is read as text and changed in C#:

```csharp
Scenoco.Files.Describe("Assets/Materials/Glow.mat");
// <Asset type="Material" name="Glow" shader="Universal Render Pipeline/Unlit" _BaseColor="1 0 0 1" />
Scenoco.Files.Describe("Assets/Animations/Bob.anim");        // curves by binding, and events
Scenoco.Files.Describe("Assets/Data/Level1.asset");          // a data asset's non-default fields
Scenoco.Files.Describe(AssetImporter.GetAtPath("Assets/Art/hero.png"));   // import settings
```

To change one, use Unity's API (`Material.SetColor`, `AnimationUtility.SetEditorCurve`,
`SerializedObject`, an importer's properties plus `SaveAndReimport()`), then save.

## Detached objects

```csharp
var crate = (GameObject)Scenoco.Files.Import(text, "Crate.uprefab");   // built in a preview scene
var again = Scenoco.Files.Export(crate);                               // non-default values only
```

## Rules it keeps

- Unity's components are written through their C# properties' own setters, recorded with `Undo.RecordObject` as an editor script does; a script's fields, and Unity's stored fields no property sets, through `SerializedObject`.
- What a property stores is learned by doing it: a snapshot of the object's serialized data before and after the setter. There is no table of names.
- The text is XML read and written by `System.Xml.Linq`; conflict markers are written by `git merge-file`.

How the code is organised: [ARCHITECTURE.md](ARCHITECTURE.md).
