using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Sync layer: the person's side of a conflict — a small window, not a modal dialog.
    //
    // It opens without taking focus when a twin needs a choice, and everything else keeps running
    // while it's open: the Editor, an agent's commands, a test run. It shows each twin that needs
    // attention with the same three answers an agent has (Files.Resolve):
    //   Keep my changes · Merge · Take the file
    // ------------------------------------------------------------------------------------------

    public sealed class TwinWindow : EditorWindow
    {
        [MenuItem("Window/Scenoco")]
        static void Open() => GetWindow<TwinWindow>(false, "Scenoco", true);

        /// <summary>Show the window, without focus, when a twin needs a choice and someone can see it.</summary>
        internal static void Notify()
        {
            if (Application.isBatchMode || InternalEditorUtility.inBatchMode) return;
            EditorApplication.delayCall += () =>
            {
                var w = GetWindow<TwinWindow>(true, "Scenoco", false);
                w.minSize = new Vector2(420, 140);
                w.Repaint();
            };
        }

        internal static void Refresh()
        {
            foreach (var w in Resources.FindObjectsOfTypeAll<TwinWindow>()) w.Repaint();
        }

        Vector2 scroll;

        void OnGUI()
        {
            var attention = Twins.All.Where(t => t.Status != TwinStatus.Synced).ToList();
            if (attention.Count == 0)
            {
                EditorGUILayout.HelpBox("Every scene and prefab is in step with its text.", MessageType.Info);
                return;
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var twin in attention)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(twin.Name, EditorStyles.boldLabel);
                EditorGUILayout.LabelField(twin.Message, EditorStyles.wordWrappedLabel);
                EditorGUILayout.BeginHorizontal();
                if (twin.NeedsChoice)
                {
                    if (GUILayout.Button("Keep my changes")) Answer(twin, Resolution.KeepAsset);
                    if (twin.Status == TwinStatus.Conflict && GUILayout.Button("Merge")) Answer(twin, Resolution.Merge);
                    if (GUILayout.Button("Take the file")) Answer(twin, Resolution.TakeFile);
                }
                if (twin.Status != TwinStatus.Conflict && GUILayout.Button("Open the file"))
                    InternalEditorUtility.OpenFileAtLineExternal(Path.GetFullPath(twin.TextPath), ConflictMarkers.FirstLine(File.ReadAllText(twin.TextPath)));
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
        }

        static void Answer(Twin twin, Resolution choice) =>
            Debug.Log("[Scenoco] " + Twins.Resolve(twin.TextPath, choice, openEditor: true));
    }
}
