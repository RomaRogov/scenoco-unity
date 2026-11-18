using UnityEngine;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // What one import or export carries along: references, and what Unity changed.
    // Created by Files (the facade) and passed down through adapters, writers and readers,
    // so no layer reaches for global state.
    // ------------------------------------------------------------------------------------------

    /// <summary>Text → Unity objects.</summary>
    public sealed class ImportContext
    {
        public readonly ReferenceTable References = new();

        /// <summary>Values Unity changed as they were written (clamped, re-derived), as "file:line: …" — also logged.</summary>
        public readonly System.Collections.Generic.List<string> Changed = new();

        /// <summary>Record Undo for every change (editing open scenes); off when building detached objects.</summary>
        public bool RecordUndo;

        /// <summary>The scene new GameObjects are created in: the build scene for detached objects.</summary>
        public UnityEngine.SceneManagement.Scene Target = BuildScene.Get();
    }

    /// <summary>Unity objects → text.</summary>
    public sealed class ExportContext
    {
        public readonly ReferenceNames Names;

        public ExportContext(GameObject root) : this(new ReferenceNames(root)) { }

        public ExportContext(ReferenceNames names) => Names = names;
    }
}
