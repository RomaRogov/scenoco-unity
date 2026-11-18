using System.Linq;
using UnityEngine;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // The entry point: text ↔ Unity objects. Picks the adapter for a file's root element, builds
    // the context, and hands over. Everything else lives in the layers:
    //   Document/  the text        Values/   value text            Objects/  one object's names and values
    //   Assets/    one file kind   Sync/     a scene and its .uscene twin, kept in step both ways
    // ------------------------------------------------------------------------------------------

    public static class Files
    {
        static readonly IAssetAdapter[] Adapters =
        {
            new GameObjectTreeAdapter(),
            new ScriptableObjectAdapter(),
        };

        /// <summary>Build new, detached Unity objects from a file's text.</summary>
        public static Object Import(string text, string source = null, bool recordUndo = false)
        {
            using var _ = Defaults.Operation();
            var root = Markup.Parse(text, source);
            var adapter = Adapters.FirstOrDefault(a => a.Roots.Contains(root.Tag()))
                          ?? throw new ScenocoException(root, $"<{root.Name}> is not a kind of file this reads — it reads: {string.Join(", ", Adapters.SelectMany(a => a.Roots))}");
            return adapter.Build(root, new ImportContext { RecordUndo = recordUndo });
        }

        /// <summary>A Unity object as text. A GameObject is written as a prefab.</summary>
        public static string Export(Object o)
        {
            using var _ = Defaults.Operation();
            var adapter = Adapters.FirstOrDefault(a => a.CanDescribe(o))
                          ?? throw new System.ArgumentException($"no adapter writes a {o.GetType().Name} yet");
            return Markup.Write(adapter.Describe(o));
        }

        // --- scenes and their text twins (Sync/) -------------------------------------------------

        /// <summary>
        /// Give a scene or a prefab a text twin (Level1.unity → Level1.uscene, Enemy.prefab → Enemy.uprefab)
        /// and return its path. Only non-default values are written.
        /// </summary>
        public static string Track(string assetPath) => Twins.Track(assetPath).TextPath;

        /// <summary>Apply every edited .uscene and .uprefab now, and report each twin's status.</summary>
        public static string Sync() => Twins.Sync();

        /// <summary>Each twin's status: Synced, Conflict (waiting for a choice), Merging (markers in the file), Invalid (a text error).</summary>
        public static string Status() => Twins.Status();

        /// <summary>Answer a conflict: "keep" (the scene's or prefab's changes), "file" (the file's), or "merge".</summary>
        public static string Resolve(string path, string choice) => Twins.Resolve(path, choice switch
        {
            "keep" => Resolution.KeepAsset,
            "file" => Resolution.TakeFile,
            "merge" => Resolution.Merge,
            _ => throw new System.ArgumentException("choice is \"keep\", \"file\" or \"merge\""),
        });

        /// <summary>Open a scene without a prompt and without discarding unsaved changes (it opens next to a modified scene).</summary>
        public static string Open(string scenePath) => Twins.Open(scenePath).path;

        // --- reading any asset -------------------------------------------------------------------

        /// <summary>
        /// Any asset as text, to read: what differs from a fresh one. A scene or prefab as its twin
        /// would say it; anything else (a Material, an AnimationClip, a data asset) as &lt;Asset type="…"&gt;.
        /// Read-only: assets other than scenes and prefabs are changed in C#, through SerializedObject or
        /// the type's own API (Material.SetColor, AnimationUtility.SetEditorCurve, …), then saved.
        /// </summary>
        public static string Describe(string assetPath)
        {
            using var _ = Defaults.Operation();
            if (assetPath.EndsWith(".unity") || assetPath.EndsWith(".prefab"))
            {
                string text = null;
                TwinTarget.ForText(TwinTarget.TextPathFor(assetPath)).With(a => text = Markup.Write(a.Export(null).Root));
                return text;
            }
            var asset = UnityEditor.AssetDatabase.LoadMainAssetAtPath(assetPath) ?? throw new System.ArgumentException($"no asset at {assetPath}");
            return Describe(asset);
        }

        /// <summary>
        /// One object as text, to read. For an asset's import settings:
        /// <c>Describe(AssetImporter.GetAtPath(path))</c>; change them through the importer, then SaveAndReimport().
        /// </summary>
        public static string Describe(Object o)
        {
            using var _ = Defaults.Operation();
            return o is GameObject ? Export(o) : Markup.Write(AssetText.Describe(o) ?? ScriptableObjectAdapter.Fields(o));
        }

        /// <summary>A GameObject's children as a scene's root objects.</summary>
        public static string ExportScene(GameObject container)
        {
            using var _ = Defaults.Operation();
            return Markup.Write(GameObjectTreeAdapter.Describe(container, "Scene"));
        }
    }
}
