using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Sync layer: every twin in the project, kept in step without anyone asking.
    //
    //   any *.uscene / *.uprefab under Assets/   is a twin (its .unity / .prefab is created from it
    //                                            if missing)
    //   twice a second               each file is checked; an edited file is applied (Twin)
    //   a scene saved, a prefab      its twin's file is updated in place (a prefab is noticed when
    //   saved or changed             Unity reimports it: Prefab Mode, an Inspector edit, Apply)
    //   before entering Play mode    every twin is checked at once, so a test plays the latest text
    //
    // Nothing runs while compiling, importing or playing. Nothing waits for a person: a conflict
    // is a status, shown in the Scenoco window and the console, until someone chooses.
    // ------------------------------------------------------------------------------------------

    [InitializeOnLoad]
    public static class Twins
    {
        static readonly Dictionary<string, Twin> twins = new();
        static double nextPoll, nextScan;

        static Twins()
        {
            EditorApplication.update += Update;
            EditorSceneManager.sceneSaved += scene => Saved(scene.path);
            EditorApplication.playModeStateChanged += change =>
            {
                if (change == PlayModeStateChange.ExitingEditMode) Sync();
            };
        }

        public static IReadOnlyCollection<Twin> All
        {
            get
            {
                if (twins.Count == 0) Scan();
                return twins.Values;
            }
        }

        static void Update()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            var now = EditorApplication.timeSinceStartup;
            if (now < nextPoll) return;
            nextPoll = now + 0.5;
            if (now >= nextScan)
            {
                Scan();
                nextScan = now + 3;
            }
            Drain();
            foreach (var twin in InOrder()) Guard(twin, () => twin.Poll());
        }

        /// <summary>Prefabs before scenes, so a scene written together with a prefab it places finds it.</summary>
        static List<Twin> InOrder() => twins.Values.OrderBy(t => t.Target is SceneTarget).ThenBy(t => t.TextPath).ToList();

        // Prefabs Unity just reimported, waiting to be looked at outside the import.
        static readonly HashSet<string> changedAssets = new();

        /// <summary>Unity reimported assets: a changed prefab with a twin is written into its file at the next check.</summary>
        sealed class Imports : AssetPostprocessor
        {
            static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
            {
                if (TwinTarget.Saving) return;
                foreach (var path in imported)
                    if (path.EndsWith(".prefab")) changedAssets.Add(path);
            }
        }

        static void Drain()
        {
            if (changedAssets.Count == 0) return;
            var paths = changedAssets.ToList();
            changedAssets.Clear();
            foreach (var path in paths) Saved(path);
        }

        static void Saved(string assetPath)
        {
            foreach (var twin in twins.Values.Where(t => t.AssetPath == assetPath).ToList()) Guard(twin, twin.AssetSaved);
        }

        static void Scan()
        {
            var found = Directory.Exists("Assets")
                ? Directory.GetFiles("Assets", "*.u*", SearchOption.AllDirectories)
                    .Where(p => p.EndsWith(".uscene") || p.EndsWith(".uprefab")).Select(p => p.Replace('\\', '/'))
                    .Where(Imported).ToList()
                : new List<string>();
            foreach (var path in found)
                if (!twins.ContainsKey(path)) twins[path] = new Twin(path);
            foreach (var gone in twins.Keys.Except(found).ToList()) twins.Remove(gone);
        }

        /// <summary>
        /// Where Unity imports files, so a twin's asset can be: not in a folder Unity skips — one ending in ~
        /// (Samples~, a tool's staging) or starting with a dot.
        /// </summary>
        static bool Imported(string path) =>
            path.Split('/').Take(path.Count(c => c == '/')).All(folder => !folder.EndsWith("~") && !folder.StartsWith("."));

        static void Guard(Twin twin, Action action)
        {
            try { action(); }
            catch (ScenocoException e) { twin.Problem(TwinStatus.Invalid, e.Message); }
            catch (Exception e)
            {
                twin.Problem(TwinStatus.Error, $"{twin.Name}: {e.Message}");
                Debug.LogException(e);
            }
        }

        // --- what agents and menus call ----------------------------------------------------------

        /// <summary>
        /// Check every twin now (instead of within half a second) and report what this call did: a twin it
        /// changed says how, one already in step says so, and one waiting on a choice still says what for.
        /// </summary>
        public static string Sync()
        {
            Scan();
            Drain();
            var before = twins.Values.ToDictionary(t => t, t => t.Changes);
            foreach (var twin in InOrder()) Guard(twin, () => twin.Poll(true));
            foreach (var twin in InOrder()) Guard(twin, () => twin.Poll());      // what failed for want of something that has landed now
            // A message left from an earlier sync ("created …") is not news: an untouched synced twin is in step.
            return Report(t => t.Status == TwinStatus.Synced && before.TryGetValue(t, out var n) && n == t.Changes ? "in step" : t.Message);
        }

        /// <summary>Each twin's status, with the message of the last thing that happened to it.</summary>
        public static string Status() => Report(t => t.Message);

        static string Report(Func<Twin, string> message)
        {
            if (twins.Count == 0) Scan();
            if (twins.Count == 0) return "nothing has a text twin (Files.Track(\"Assets/….unity\" or \"….prefab\") makes one)";
            var sb = new StringBuilder();
            foreach (var t in twins.Values.OrderBy(t => t.TextPath))
            {
                var m = message(t);
                sb.Append(t.TextPath).Append(": ").Append(t.Status).Append(m.Length > 0 ? " — " + m : "").Append('\n');
            }
            return sb.ToString().TrimEnd();
        }

        public static Twin Track(string assetPath)
        {
            var twin = Twin.Create(assetPath);
            twins[twin.TextPath] = twin;
            return twin;
        }

        public static string Resolve(string path, Resolution choice, bool openEditor = false)
        {
            var twin = Find(path) ?? throw new ArgumentException($"{path} has no text twin");
            string result = null;
            Guard(twin, () => result = twin.Resolve(choice, openEditor));
            return result ?? $"{twin.Name}: {twin.Status} — {twin.Message}";
        }

        public static Twin Find(string path)
        {
            if (twins.Count == 0) Scan();
            path = path.Replace('\\', '/');
            if (!new[] { ".unity", ".prefab", ".uscene", ".uprefab" }.Contains(Path.GetExtension(path))) return null;
            return twins.TryGetValue(TwinTarget.TextPathFor(path), out var t) ? t : null;
        }

        /// <summary>
        /// Open a scene for editing without a prompt and without discarding anything: when a loaded
        /// scene has unsaved changes, the scene is opened next to it instead of in its place.
        /// </summary>
        public static Scene Open(string scenePath)
        {
            if (Find(scenePath) is { } twin) Guard(twin, () => twin.Poll(true));
            var loaded = SceneManager.GetSceneByPath(scenePath);
            if (loaded.IsValid() && loaded.isLoaded)
            {
                SceneManager.SetActiveScene(loaded);
                return loaded;
            }
            var unsaved = Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Any(s => s.isDirty);
            var scene = EditorSceneManager.OpenScene(scenePath, unsaved ? OpenSceneMode.Additive : OpenSceneMode.Single);
            if (unsaved) SceneManager.SetActiveScene(scene);
            return scene;
        }

        // --- menus -----------------------------------------------------------------------------------

        [MenuItem("Assets/Scenoco/Create Text Twin", true)]
        static bool CanTrack()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            return (path.EndsWith(".unity") || path.EndsWith(".prefab")) && !File.Exists(TwinTarget.TextPathFor(path));
        }

        [MenuItem("Assets/Scenoco/Create Text Twin")]
        static void TrackSelected()
        {
            var twin = Track(AssetDatabase.GetAssetPath(Selection.activeObject));
            Debug.Log($"[Scenoco] wrote {twin.TextPath}");
        }
    }
}
