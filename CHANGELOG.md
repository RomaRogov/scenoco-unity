# Changelog

## [0.2.0] - 2026-11-19

### Added
- A ParticleSystem's modules are written as a script sets them: `<ParticleSystem.main> <MainModule
  duration="1" startLifetime="0.5" />`, read and written through the module structs' own properties
  (`Members.Handles`). A struct that is only a copy is refused.
- A struct that converts implicitly from one value is written as that value when it is one: a
  `MinMaxCurve` constant as `startSize="2"`, a `MinMaxGradient` colour as `startColor="1 0 0"`.
- Lists a type gives through `Get{Names}`/`Set{Names}` and a `{name}Count` are a member: a module's
  `bursts`.

### Fixed
- A text applied to its scene or prefab resolves `#Name` without the objects that same change
  removes: an object replaced by a new one of the same name (a group turned into a placed prefab)
  was reported as an ambiguous second candidate with no line.
- A list whose items are of a generic type — a Unity spline's `SplineDataKeyValuePair<T>` — is written:
  its items' elements are named without the arity, where they used to stop the scene's text with an XML
  error (``The '`' character … cannot be included in a name``).
- `Sync()` reports what that call did: a synced twin it didn't change says `in step`, where it used to
  repeat its last message (`created …`) on every call. `Status()` still gives the last thing that
  happened to each twin.
- A Transform's `localEulerAngles` that is already the stored rotation, read back as other angles (`-4.24`
  as `355.76`), is no longer refused as runtime state: rotations are compared as rotations.
- A struct at the end of a list item, followed by a field hidden in the Inspector, no longer takes that field
  in as its own: a Unity spline's last knot wrote `Spline.m_MetaData` inside its rotation's `float4`,
  recursively — 14,000 lines for 10 knots. The end of a property counts hidden properties now.
- A `.uscene` or `.uprefab` in a folder Unity doesn't import (ending in `~`, or starting with a dot) is
  no twin: a tool can stage text there without Scenoco building it.

## [0.1.1] - 2026-11-18

### Fixed
- A text twin with Windows line endings (Git on Windows checks text out with them) is read as the
  same text, and written back with them, so a checkout isn't taken for an edit.

## [0.1.0] - 2026-11-18

First release.

- `.uscene` and `.uprefab` text twins of scenes and prefabs, kept in step with the Editor both ways:
  a text edit is applied as one Undo step and saved; a save in Unity is written back into the text,
  comments and order kept. Conflicts open a merge window, never a modal dialog.
- Attributes are Unity's C# property names, set through their setters with Undo; scripts' serialized
  fields by name. No converters or aliases. `Transform.localEulerAngles` reads and writes the
  Inspector's rotation.
- What Unity computes isn't written: a RectTransform's values a Canvas or layout drives, and its
  localPosition (the anchors and anchored position say it).
- Prefab placements write only their overrides, with removed components and children; variants via `base`.
- `Scenoco.Files`: `Sync`, `Status`, `Track`, `Describe`, `Resolve`.
- Value formats in one place (`ValueText`): vectors space-separated, colours `r g b [a]`, enums by
  name, layer masks by names, references as `#Name` or asset paths.
