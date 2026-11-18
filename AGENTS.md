# Scenoco — the rules card

How to write a Unity scene or prefab as text. Read **Every scene** before writing one; read a
paragraph of **When the game needs it** the first time the game needs that thing.

## Every scene

- **A `.uscene` is a `.unity`, a `.uprefab` is a `.prefab`.** The text sits next to its asset and
  is the same thing. Write or edit the text and within half a second the asset follows: an open
  scene as one Undo step, a closed one loaded, changed and saved. A text with no asset creates it.
  Saves made in Unity are written back into the text in place, comments and order kept.
- **What's there is in the scene; what changes is in scripts.** Everything you could see or click
  in the Editor's Hierarchy — the camera, lights, floor, walls, player, UI, a level's layout — is an
  element in a `.uscene` or `.uprefab`, with its mesh, material and collider. A script
  (`MonoBehaviour`) moves, reacts and spawns; it doesn't build. No `GameObject.CreatePrimitive`, no
  `new GameObject` given a renderer or collider, no `new Material` or `Shader.Find` in game code.
- **It only declares.** Objects, their components, the values that differ from a fresh component —
  no logic. The scene sets each script's starting fields.
- **Only a prefab is instantiated, and anything placed more than once is a prefab** — a coin, a
  wall, an obstacle, static or not. A script spawns it from a field the scene fills:
  `public GameObject coin;` → `<Spawner coin="Assets/Prefabs/Coin.prefab" />` →
  `Instantiate(coin, position, Quaternion.identity)`.
- **A level is one element per object,** `<PrefabInstance prefab="…/Brick.prefab">` with its
  `<Transform>`, never a loop or an array in a script: Unity's Editor moves them, and the file says
  what it did. Spawning what appears while playing (waves, an endless track's segments) is a script's.
- **Shapes and colours are assets.** A mesh is Unity's built-in one, `<MeshFilter
  sharedMesh="Library/unity default resources#Cube" />` (also Sphere, Capsule, Cylinder, Plane,
  Quad), or a model's. A colour is a material asset, made once as Assets ▸ Create ▸ Material makes
  it, with the project's render pipeline's shader, then named by its path:

  ```csharp
  // unity command script eval --code '…'
  var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
  var m = new Material(rp != null ? rp.defaultMaterial : AssetDatabase.GetBuiltinExtraResource<Material>("Default-Material.mat")) { color = new Color(0.95f, 0.75f, 0.1f) };
  AssetDatabase.CreateAsset(m, "Assets/Materials/Coin.mat");
  ```
- **An attribute is an assignment, as a script would write it.** An element is a component by its
  class name (`<Rigidbody2D>`, `<Paddle>`). On Unity's components an attribute is a C# property
  with a setter (`mass`, `orthographicSize`, `sharedMaterial`), set through that setter, in the
  order written. Whatever else the setter does happens too. On your scripts it's a serialized
  field by its declared name (public, or `[SerializeField]`). A stored field no property sets is
  written by its serialized name, as in the `.unity` file (`m_IsActive`).
- **A GameObject** has `name` (required) and GameObject's own properties: `tag`, `layer` (its
  number), `isStatic`. `m_IsActive="false"` makes it inactive. Its place is its Transform:
  `<Transform localPosition="0 1 0" localEulerAngles="0 90 0" localScale="2 2 2" />`. Children are
  `<GameObject>` elements inside it.
- **Values are the C# values:** vectors `"x y z"`, colours `"r g b a"` (floats 0–1), a Quaternion
  `"x y z w"`, enums by name. Left-handed, Y up, metres. A rotation is `localEulerAngles` in
  degrees: the Inspector's Rotation field, which keeps the angles as written (450 stays 450).
- **References:** another object `#Name`, or `#Parent/Child` when names repeat, or `#id` after
  giving the target `id="…"`. A component property takes `#Name` and gets that object's component
  of its type. An asset is its path: `Assets/Sprites/a.png#a` (the `#a` names a sub-asset: a sprite
  inside a texture), `Assets/Prefabs/Enemy.prefab`; Unity's built-in ones the same way
  (`Library/unity default resources#Cube`). A list of references is space-separated, or
  comma-separated when a path has a space.
- **Leave out what you don't set.** Writing a default is harmless; the next save drops it.
- **After an edit, check the status** — `Scenoco.Files.Sync()` returns each twin's status:
  `Synced`, `Invalid` (a text error, as `file:line: what's wrong`, and the asset left as it was),
  `Conflict` (unsaved work in Unity and an edited file), `Merging` (conflict markers in the file).
  Fix an error at its line; the message says what is accepted there.
- **Nothing ever opens a dialog.** A `Conflict` waits for `Scenoco.Files.Resolve(path, "keep" |
  "file" | "merge")`. Avoid it: save the scene before editing its text, or edit only the text.

```xml
<!-- Assets/Prefabs/Coin.uprefab -->
<Prefab name="Coin">
  <Transform localScale="0.6 0.05 0.6" localEulerAngles="90 0 0" />
  <MeshFilter sharedMesh="Library/unity default resources#Cylinder" />
  <MeshRenderer sharedMaterial="Assets/Materials/Coin.mat" />
  <CapsuleCollider isTrigger="true" />
  <Coin value="10" />
</Prefab>

<!-- Assets/Scenes/Level1.uscene -->
<Scene name="Level1">
  <GameObject name="Main Camera" tag="MainCamera">
    <Transform localPosition="0 4 -8" localEulerAngles="25 0 0" />
    <Camera />
  </GameObject>
  <GameObject name="Directional Light">
    <Transform localEulerAngles="50 -30 0" />
    <Light type="Directional" />
  </GameObject>
  <GameObject name="Ground">
    <Transform localScale="3 1 30" />
    <MeshFilter sharedMesh="Library/unity default resources#Plane" />
    <MeshRenderer sharedMaterial="Assets/Materials/Ground.mat" />
    <MeshCollider sharedMesh="Library/unity default resources#Plane" />
  </GameObject>
  <GameObject name="Player">
    <Transform localPosition="0 1 0" />
    <MeshFilter sharedMesh="Library/unity default resources#Capsule" />
    <MeshRenderer sharedMaterial="Assets/Materials/Player.mat" />
    <CapsuleCollider />
    <Player speed="6" />
  </GameObject>
  <PrefabInstance prefab="Assets/Prefabs/Coin.prefab" name="Coin">
    <Transform localPosition="0 1 8" />
  </PrefabInstance>
</Scene>
```

## When the game needs it

**A new script.** Write the `.cs`, run `unity recompile`, then name it in the text. A component
named before it compiles is an `Invalid` "no component is named …" until it does.

**Prefabs in detail.** `Enemy.uprefab` is `<Prefab name="Enemy">` whose
attributes and components are the root object's. Place it with
`<PrefabInstance prefab="Assets/Prefabs/Enemy.prefab" name="E1">` and its `<Transform … />`. A
placement writes only its overrides: a component element sets only what it names, a `<GameObject
name="Muzzle">` inside it means the prefab's child of that name, a child the prefab doesn't have
is added. Taking an override out of the text reverts it. To leave something of the prefab out:

```xml
<PrefabInstance prefab="Assets/Prefabs/Enemy.prefab" name="Ghost">
  <PrefabInstance.removedComponents> <Rigidbody2D /> </PrefabInstance.removedComponents>
  <PrefabInstance.removedGameObjects> <GameObject name="Muzzle" /> </PrefabInstance.removedGameObjects>
</PrefabInstance>
```

On a child of the placement they're `<GameObject.removedComponents>` / `<GameObject.removedGameObjects>`.
Two components of one type: the n-th element of that type means the n-th component.

**A variant** of a prefab: `<Prefab base="Assets/Prefabs/Enemy.prefab" name="Boss">`, written as
overrides, like a placement. Its base can't be changed later; remove `base` to unpack it.

**Structured values** (structs, lists, curves, events) are property elements, applied after the
attributes, one child element per item, named by the item's type:

```xml
<HingeJoint2D useLimits="true">
  <HingeJoint2D.limits> <JointAngleLimits2D min="-90" max="0" /> </HingeJoint2D.limits>
</HingeJoint2D>
<Spawner interval="2">
  <Spawner.waves>
    <Wave count="5" delay="1.5" />
    <Wave count="8" delay="1" />
  </Spawner.waves>
  <Spawner.difficulty> <Key time="0" value="1" /> <Key time="60" value="3" /> </Spawner.difficulty>
</Spawner>
```

A list of single values: `<Path.points> <Vector2 value="0 1" /> </Path.points>`; of references or
numbers, an attribute with spaces: `targets="#A #B"`. `[SerializeReference]` items are named by
their concrete class.

**Events** are stored fields: a Button's is `m_OnClick`, yours by its name —
`<Button.m_OnClick> <Call target="#Game" method="Game.Restart" /> </Button.m_OnClick>`, with
`argument="…"` for a one-argument method.

**UI is UI Toolkit, or uGUI when the project has it.** Both draw in simulation captures.

- **UI Toolkit** is built into Unity 6, and it's the one to use: its layout is a UXML document and its
  look a USS stylesheet — text files, as the scene is — and the scene holds one element for it:
  `<UIDocument panelSettings="Assets/UI/Panel.asset" visualTreeAsset="Assets/UI/Hud.uxml" />`.
  A script fills it in: `GetComponent<UIDocument>().rootVisualElement.Q<Label>("score").text = …`.
  Once per project, a theme file `Assets/UI/Runtime.tss` holding `@import url("unity-theme://default");`
  and the panel settings:

  ```csharp
  var ps = ScriptableObject.CreateInstance<UnityEngine.UIElements.PanelSettings>();
  ps.themeStyleSheet = AssetDatabase.LoadAssetAtPath<UnityEngine.UIElements.ThemeStyleSheet>("Assets/UI/Runtime.tss");
  ps.scaleMode = UnityEngine.UIElements.PanelScaleMode.ScaleWithScreenSize; ps.referenceResolution = new Vector2Int(1920, 1080);
  AssetDatabase.CreateAsset(ps, "Assets/UI/Panel.asset");
  ```
- **uGUI** only when `com.unity.ugui` is in `Packages/manifest.json`: a Canvas and its objects are
  elements in the scene, each placed by its `RectTransform` — `<RectTransform anchorMin="0 1"
  anchorMax="0 1" pivot="0 1" offsetMin="20 -80" offsetMax="320 -20" />`, anchors as fractions of
  the parent, then offsets in pixels (after anchors and pivot: they're set in order).
- **Never `OnGUI`** while the game plays: it's Unity's immediate-mode debug UI, drawn outside the
  scene and missing from every capture.

**Made in code** is only what can't be declared: a mesh computed from data (a terrain from a
heightmap) is built by a script into its own `MeshFilter`, on an object the scene declares.

**Other assets** — materials, animation clips, data assets, import settings — have no text twin.
Read one with `Scenoco.Files.Describe("Assets/…")` (an importer:
`Files.Describe(AssetImporter.GetAtPath(path))`); change it in C# with Unity's API
(`Material.SetColor`, `AnimationUtility.SetEditorCurve`, `SerializedObject`, an importer's
properties then `SaveAndReimport()`), and save (`EditorUtility.SetDirty` + `AssetDatabase.SaveAssets`).

**A scene or prefab without a twin** (made in Unity): `Scenoco.Files.Track("Assets/…/X.unity")`
writes its text, then edit that. `Files.Describe` reads any scene or prefab without making a twin.

## When something is off

- **`Invalid` "has no …":** names are exact — a property, or a stored field. The error lists every
  name the component takes; a read-only or obsolete property says so.
- **"runtime state":** setting that property changes nothing Unity stores (a velocity), so it
  wouldn't last. Set it from a script when the game runs.
- **"Unity says: …":** the setter itself refused the value (a tag that isn't defined).
- **A warning says a value "became" another:** the setter changed it (a field of view above 179,
  an angle wrapped). Write a value it keeps.
- **A value changed in Unity comes back under another name:** a save writes what Unity stores,
  under the property named after it, in place of the name you wrote (`localRotation` you wrote
  comes back as `localEulerAngles`).
- **A `.uscene` or `.uprefab` anywhere under `Assets/` is a twin** and creates an asset. Keep drafts
  under another extension.
