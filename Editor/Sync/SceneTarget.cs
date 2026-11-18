using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Sync layer: a scene as a twin's target. Open means loaded in the Editor; otherwise it is
    // opened next to whatever is open (additively), worked on, saved and closed again.
    // ------------------------------------------------------------------------------------------

    public sealed class SceneTarget : TwinTarget
    {
        public SceneTarget(string assetPath) : base(assetPath) { }

        Scene Loaded => SceneManager.GetSceneByPath(AssetPath);

        public override bool IsOpen => Loaded.IsValid() && Loaded.isLoaded;

        public override void CreateEmpty(System.Xml.Linq.XElement text)
        {
            // An empty scene file, which Unity opens with default scene settings. (NewScene can't add a
            // scene next to an untitled one, which is what a test run or a fresh Editor has open.)
            File.WriteAllText(AssetPath, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n");
            AssetDatabase.ImportAsset(AssetPath);
        }

        public override void With(Action<LoadedAsset> action)
        {
            var scene = Loaded;
            if (IsOpen)
            {
                action(Asset(scene, true));
                return;
            }
            var listed = scene.IsValid();                 // in the Hierarchy, unloaded: leave it listed
            var opened = EditorSceneManager.OpenScene(AssetPath, OpenSceneMode.Additive);
            try { action(Asset(opened, false)); }
            finally { EditorSceneManager.CloseScene(opened, !listed); }
        }

        static LoadedAsset Asset(Scene scene, bool open) => new()
        {
            Name = scene.name, Scene = scene, Open = open,
            Save = () => WhileSaving(() => EditorSceneManager.SaveScene(scene)),
        };
    }
}
