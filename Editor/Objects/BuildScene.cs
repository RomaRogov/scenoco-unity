using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Objects layer: where detached objects are built — a preview scene of their own.
    //
    // A GameObject is created in a scene, and creating one in a scene a person has open marks that
    // scene modified (and the next test run or scene switch then stops on a "save changes?" prompt).
    // Building, defaults and the type-map probe therefore all happen in this preview scene: the
    // isolation Unity's own prefab editing uses. Nothing here ever appears in, or dirties, an open scene.
    //
    // CreateIn is how every GameObject in the package is made, for the same reason: Unity makes a
    // new object in the active scene first and then moves it, which marks the active scene modified
    // a frame later even when the object was meant for another scene. An object born hidden and
    // unsaved doesn't; it's moved to its scene and only then made an ordinary object.
    // ------------------------------------------------------------------------------------------

    [InitializeOnLoad]
    public static class BuildScene
    {
        static Scene scene;

        static BuildScene() => AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;

        /// <summary>
        /// Before scripts reload, close the scene only when nothing is left in it. Unity refuses to
        /// destroy some objects during a reload, and closing a preview scene that still holds them loops
        /// forever ("Dereferencing NULL ImmediatePtr!"). A hidden scene left behind costs nothing.
        /// </summary>
        static void BeforeReload()
        {
            if (!scene.IsValid() || Defaults.Holding || scene.rootCount > 0)
            {
                scene = default;
                return;
            }
            EditorSceneManager.ClosePreviewScene(scene);
            scene = default;
        }

        public static Scene Get()
        {
            if (!scene.IsValid()) scene = EditorSceneManager.NewPreviewScene();
            return scene;
        }

        /// <summary>A new GameObject in the build scene.</summary>
        public static GameObject NewObject(string name, HideFlags flags = HideFlags.None) => CreateIn(Get(), name, flags);

        /// <summary>A new GameObject in a scene, without touching the active scene.</summary>
        public static GameObject CreateIn(Scene scene, string name, HideFlags flags = HideFlags.None)
        {
            var go = EditorUtility.CreateGameObjectWithHideFlags(name, HideFlags.HideAndDontSave);
            SceneManager.MoveGameObjectToScene(go, scene);
            go.hideFlags = flags;
            return go;
        }

        /// <summary>Destroy the cached fresh objects and close the scene — at a time Unity allows it, not during a reload.</summary>
        public static void Close()
        {
            Defaults.Clear();
            if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
            scene = default;
        }
    }
}
