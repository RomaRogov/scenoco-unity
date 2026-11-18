using System;
using System.IO;
using System.Xml.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Sync layer: a prefab as a twin's target. Open means it's the prefab being edited in Prefab
    // Mode: its objects there are worked on and saved as that mode saves them. Otherwise its
    // contents are loaded in isolation (PrefabUtility.LoadPrefabContents), saved and unloaded.
    // Objects keep their file ids either way, so every placement of the prefab keeps its overrides.
    // A variant is a prefab like any other here: its root is a placement of its base (PrefabInstances).
    // ------------------------------------------------------------------------------------------

    public sealed class PrefabTarget : TwinTarget
    {
        public PrefabTarget(string assetPath) : base(assetPath) { }

        PrefabStage Stage => PrefabStageUtility.GetCurrentPrefabStage() is { } stage && stage.assetPath == AssetPath ? stage : null;

        public override bool IsOpen => Stage != null;

        public override void CreateEmpty(XElement text)
        {
            // A variant starts as a placement of its base: saved, Unity makes it a variant.
            var go = text.Attr(PrefabInstances.Base) != null
                ? PrefabInstances.Place(PrefabInstances.Asset(text, PrefabInstances.Base), BuildScene.Get())
                : BuildScene.NewObject(Path.GetFileNameWithoutExtension(AssetPath));
            try { WhileSaving(() => PrefabUtility.SaveAsPrefabAsset(go, AssetPath)); }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        public override void With(Action<LoadedAsset> action)
        {
            if (Stage is { } stage)
            {
                var edited = stage.prefabContentsRoot;
                action(new LoadedAsset
                {
                    Name = edited.name, Scene = stage.scene, Root = edited, Open = true,
                    Save = () => WhileSaving(() =>
                    {
                        PrefabUtility.SaveAsPrefabAsset(edited, AssetPath);
                        stage.ClearDirtiness();
                    }),
                });
                return;
            }
            var root = PrefabUtility.LoadPrefabContents(AssetPath);
            try
            {
                action(new LoadedAsset
                {
                    Name = root.name, Scene = root.scene, Root = root, Open = false,
                    Save = () => WhileSaving(() => PrefabUtility.SaveAsPrefabAsset(root, AssetPath)),
                });
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
    }
}
