using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Scenoco.Tests
{
    /// <summary>
    /// What the twin tests share: a throwaway folder, scenes and prefabs made from text, scenes
    /// opened next to whatever is open, and values changed the way the Inspector changes them.
    /// </summary>
    public abstract class TwinFixture
    {
        protected const string Folder = "Assets/ScenocoSyncTests";

        protected string scenePath, textPath;

        [SetUp]
        public void MakeFolder()
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "ScenocoSyncTests");
        }

        [TearDown]
        public void Clean()
        {
            for (var i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.path.StartsWith(Folder)) EditorSceneManager.CloseScene(s, true);
            }
            foreach (var twin in Twins.All.Where(t => t.TextPath.StartsWith(Folder)).ToList()) twin.Forget();
            AssetDatabase.DeleteAsset(Folder);
            Twins.Sync();
        }

        /// <summary>A scene made from text: the .uscene is written and its .unity created from it.</summary>
        protected Twin FromText(string name, string text)
        {
            textPath = $"{Folder}/{name}.uscene";
            scenePath = $"{Folder}/{name}.unity";
            File.WriteAllText(textPath, text);
            Twins.Sync();
            Assert.That(File.Exists(scenePath), Twins.Status());
            return Twins.Find(textPath);
        }

        /// <summary>A prefab made from text: the .uprefab is written and its .prefab created from it.</summary>
        protected Twin PrefabFromText(string name, string text)
        {
            var path = $"{Folder}/{name}.uprefab";
            File.WriteAllText(path, text);
            Twins.Sync();
            Assert.That(File.Exists($"{Folder}/{name}.prefab"), Twins.Status());
            return Twins.Find(path);
        }

        protected Scene OpenScene() => EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);

        protected static GameObject Get(Scene scene, string name) =>
            scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Transform>(true)).Select(t => t.gameObject).SingleOrDefault(g => g.name == name);

        protected static void Edit(Object o, string path, System.Action<SerializedProperty> set)
        {
            var so = new SerializedObject(o);
            set(so.FindProperty(path));
            so.ApplyModifiedProperties();
        }

        protected void Save(Scene scene) => EditorSceneManager.SaveScene(scene);

        protected string Text => File.ReadAllText(textPath);
    }
}
