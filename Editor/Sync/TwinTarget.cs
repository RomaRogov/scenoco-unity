using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Sync layer: the Unity side of a twin — the asset a text file is the twin of.
    //
    //   TwinTarget    one asset on disk: a scene (SceneTarget) or a prefab (PrefabTarget). It knows
    //                 how to create an empty one, whether a person has it open, and how to hand
    //                 over its objects loaded — the open ones when it's open, otherwise loaded just
    //                 for the occasion and released after.
    //   LoadedAsset   those objects while they're loaded: every object by key, the export as text,
    //                 whether there is unsaved work, and how to save.
    //
    // Twin (the flows) is written against these two, so a scene and a prefab sync the same way.
    // ------------------------------------------------------------------------------------------

    public abstract class TwinTarget
    {
        public readonly string AssetPath;

        /// <summary>True while this code saves an asset, so the save isn't taken for a person's.</summary>
        internal static bool Saving;

        protected TwinTarget(string assetPath) => AssetPath = assetPath;

        /// <summary>A person has it open for editing (a loaded scene; a prefab in Prefab Mode).</summary>
        public abstract bool IsOpen { get; }

        /// <summary>Make an empty asset to build <paramref name="text"/> into (a variant starts as its base).</summary>
        public abstract void CreateEmpty(XElement text);

        /// <summary>Run with the asset's objects loaded.</summary>
        public abstract void With(Action<LoadedAsset> action);

        public static TwinTarget ForText(string textPath) => Path.GetExtension(textPath) switch
        {
            ".uscene" => new SceneTarget(Path.ChangeExtension(textPath, ".unity")),
            ".uprefab" => new PrefabTarget(Path.ChangeExtension(textPath, ".prefab")),
            _ => throw new ArgumentException($"{textPath}: a twin is a .uscene or a .uprefab"),
        };

        /// <summary>The text file that is the twin of an asset: Level1.unity → Level1.uscene, Enemy.prefab → Enemy.uprefab.</summary>
        public static string TextPathFor(string assetPath) => Path.GetExtension(assetPath) switch
        {
            ".unity" => Path.ChangeExtension(assetPath, ".uscene"),
            ".prefab" => Path.ChangeExtension(assetPath, ".uprefab"),
            ".uscene" or ".uprefab" => assetPath,
            _ => throw new ArgumentException($"{assetPath}: only scenes (.unity) and prefabs (.prefab) have text twins"),
        };

        protected static void WhileSaving(Action save)
        {
            Saving = true;
            try { save(); }
            finally { Saving = false; }
        }
    }

    public sealed class LoadedAsset
    {
        public string Name;
        public Scene Scene;          // where its objects live: the scene itself, or a prefab's contents
        public GameObject Root;      // a prefab's root object; null for a scene
        public bool Open;            // a person has it open: edits are undoable, and their unsaved work counts
        public Action Save;

        /// <summary>Every object by key.</summary>
        public Dictionary<string, GameObject> Objects()
        {
            var d = new Dictionary<string, GameObject>();
            var roots = Root != null ? new[] { Root } : Scene.GetRootGameObjects();
            foreach (var root in roots)
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    d[KeyOf(t.gameObject)] = t.gameObject;
            return d;
        }

        /// <summary>
        /// Unsaved work: the scene's own flag, or any of its objects' — an edit marks its object at
        /// once, the scene only at the end of the frame.
        /// </summary>
        public bool Unsaved(Dictionary<string, GameObject> objects) =>
            Scene.isDirty || objects.Values.Any(go => go != null && (EditorUtility.IsDirty(go) || go.GetComponents<Component>().Any(c => c != null && EditorUtility.IsDirty(c))));

        /// <summary>The asset as text, each object element keyed. <paramref name="preferredIds"/>: ids its file already uses.</summary>
        public KeyedTree Export(IReadOnlyDictionary<GameObject, string> preferredIds)
        {
            var elements = new Dictionary<XElement, GameObject>();
            var root = Root != null
                ? GameObjectTreeAdapter.DescribePrefab(Root, preferredIds, elements)
                : GameObjectTreeAdapter.Describe(Scene, preferredIds, elements);
            var tree = new KeyedTree(root);
            foreach (var kv in elements) tree.Keys[kv.Key] = KeyOf(kv.Value);
            return tree;
        }

        /// <summary>
        /// An object's identity in its asset's file (its local file id; for an object of a placed prefab,
        /// with the placement's) — or, before it was ever saved, in this session.
        /// </summary>
        public static string KeyOf(GameObject go)
        {
            var id = GlobalObjectId.GetGlobalObjectIdSlow(go);
            return id.targetObjectId != 0 ? $"{id.targetObjectId}:{id.targetPrefabId}" : $"new:{go.GetEntityId()}";
        }
    }
}
