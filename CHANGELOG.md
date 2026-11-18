# Changelog

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
