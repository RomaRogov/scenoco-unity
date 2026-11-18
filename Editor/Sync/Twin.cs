using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Sync layer: one asset and its text twin, side by side — Level1.unity and Level1.uscene,
    // Enemy.prefab and Enemy.uprefab.
    //
    // The asset stays what Unity opens, plays and tests; the text is the same thing for people and
    // agents to read and edit. A base is kept per twin (Library/Scenoco): the text as of the last
    // time the two agreed, and the asset's export at that moment, each with the key of every
    // object in it. Both directions are then a diff against the base:
    //
    //   file edited    diff(base text, file)    → applied to the asset (open: one Undo step, then
    //                                              saved; closed: loaded, saved, released)
    //   asset saved    diff(base export, asset) → applied to the file, in place (TextPatch)
    //   both changed   a conflict: nothing is applied until someone chooses
    //                    keep   the asset's changes; the file is rewritten from them
    //                    file   the file's changes; the asset's unsaved changes are replaced
    //                    merge  both: changes to different things are combined; changes to the same
    //                           thing are written into the file as conflict markers to resolve
    //
    // Nothing here opens a modal dialog. A conflict is shown in the Scenoco window (non-modal) and
    // logged; an agent sees it in Files.Status() and answers with Files.Resolve().
    // ------------------------------------------------------------------------------------------

    public enum TwinStatus { Synced, Conflict, Merging, Invalid, Error }

    public enum Resolution { KeepAsset, TakeFile, Merge }

    public sealed class Twin
    {
        [Serializable]
        sealed class Base
        {
            public string text;
            public List<string> textKeys = new();
            public string export;
            public List<string> exportKeys = new();
        }

        public readonly string TextPath;
        public readonly TwinTarget Target;
        public TwinStatus Status { get; private set; }
        public string Message { get; private set; } = "";

        /// <summary>Counts every change any twin made to an asset: a twin that failed looks again when it moves.</summary>
        internal static int Progress;

        Base state;
        int failedAt = -1;
        DateTime seenTime;
        long seenLength = -1;
        string loggedProblem;

        public Twin(string textPath)
        {
            TextPath = textPath.Replace('\\', '/');
            Target = TwinTarget.ForText(TextPath);
            state = Load();
        }

        public string Name => Path.GetFileName(TextPath);
        public string AssetPath => Target.AssetPath;
        public bool NeedsChoice => Status is TwinStatus.Conflict or TwinStatus.Merging;

        // --- start tracking: the first text of an existing asset ----------------------------------

        /// <summary>Write an asset's text twin: every object, and of each only what differs from a fresh one (or from its prefab).</summary>
        public static Twin Create(string assetPath)
        {
            using var _ = Defaults.Operation();
            var twin = new Twin(TwinTarget.TextPathFor(assetPath));
            if (File.Exists(twin.TextPath)) throw new InvalidOperationException($"{twin.TextPath} already exists");
            twin.Target.With(asset =>
            {
                var objects = asset.Objects();
                var export = twin.Export(asset, objects);
                var text = Markup.Write(export.Root);
                twin.WriteFile(text);
                twin.Record(asset, text, export, objects);
            });
            AssetDatabase.ImportAsset(twin.TextPath);
            return twin;
        }

        // --- file → asset ------------------------------------------------------------------------

        /// <summary>Check the file; when it changed since it was last seen (or <paramref name="force"/>), act on it.</summary>
        public void Poll(bool force = false)
        {
            using var _ = Defaults.Operation();
            var info = new FileInfo(TextPath);
            if (!info.Exists) return;
            // Unchanged since last seen — unless it failed and something else has landed since (the prefab it places, say).
            var retry = Status is TwinStatus.Invalid or TwinStatus.Error && failedAt != Progress;
            if (!force && !retry && info.LastWriteTimeUtc == seenTime && info.Length == seenLength) return;
            failedAt = Progress;
            seenTime = info.LastWriteTimeUtc;
            seenLength = info.Length;
            var text = File.ReadAllText(TextPath);
            if (state != null && text == state.text)
            {
                if (Status != TwinStatus.Synced) Set(TwinStatus.Synced, "in step");
                return;
            }
            FileChanged(text);
        }

        void FileChanged(string text)
        {
            if (ConflictMarkers.In(text))
            {
                Set(TwinStatus.Merging, $"{Name} has conflict markers: keep one side of each, save, and the asset follows");
                return;
            }
            KeyedTree edited;
            try
            {
                edited = new KeyedTree(Markup.Parse(text, TextPath));
            }
            catch (ScenocoException e)
            {
                Problem(TwinStatus.Invalid, e.Message);
                return;
            }
            var created = !File.Exists(AssetPath);
            if (created)
            {
                Target.CreateEmpty(edited.Root);   // then filled like any other edit, from an empty base
                state = null;
            }
            try
            {
                Apply(text, edited, created);
            }
            catch when (created)
            {
                // Nothing half-made is left behind: the asset appears when its text is right.
                AssetDatabase.DeleteAsset(AssetPath);
                Forget();
                throw;
            }
        }

        void Apply(string text, KeyedTree edited, bool created)
        {
            Target.With(asset =>
            {
                var objects = asset.Objects();
                if (state == null) Bootstrap(asset, objects);
                if (asset.Open && Changed(asset, objects))
                {
                    Problem(TwinStatus.Conflict, $"{Name} was changed while {asset.Name} has unsaved changes. Keep your changes, take the file, or merge?");
                    TwinWindow.Notify();
                    return;
                }
                var before = BaseText();
                var after = TreeMatcher.Match(before, edited.Root);
                var changes = SceneDiff.Diff(before, after);
                if (changes.Count == 0)
                {
                    // Only comments or layout changed: the asset has nothing to take.
                    Record(asset, text, after, objects);
                    Set(TwinStatus.Synced, "in step");
                    return;
                }
                var unityChanged = SceneApply.Apply(asset.Scene, after, objects, changes, asset.Open, $"Apply {Name}");
                asset.Save();
                Progress++;
                Record(asset, text, after, objects);
                var what = created ? $"created {AssetPath} from {Name}" : $"applied {Count(changes)} from {Name}: {Summary(changes)}";
                if (unityChanged.Count > 0) what += $". Unity changed {unityChanged.Count} written value{(unityChanged.Count == 1 ? "" : "s")} as {(unityChanged.Count == 1 ? "it was" : "they were")} applied — {string.Join("; ", unityChanged)}";
                Set(TwinStatus.Synced, what);
                Debug.Log($"[Scenoco] {what}");
            });
        }

        // --- asset → file ------------------------------------------------------------------------

        /// <summary>The asset was saved: write what changed into the file, in place.</summary>
        public void AssetSaved()
        {
            using var _ = Defaults.Operation();
            if (TwinTarget.Saving || !File.Exists(TextPath) || !File.Exists(AssetPath)) return;
            Target.With(asset =>
            {
                var objects = asset.Objects();
                if (state == null)
                {
                    Bootstrap(asset, objects);
                    return;
                }
                var text = File.ReadAllText(TextPath);
                if (text != state.text)
                {
                    if (ConflictMarkers.In(text)) return;
                    Problem(TwinStatus.Conflict, $"{asset.Name} was saved while {Name} had changes not applied to it yet. Keep your changes, take the file, or merge?");
                    TwinWindow.Notify();
                    return;
                }
                var mine = SceneDiff.Diff(BaseExport(), Export(asset, objects));
                if (mine.Count == 0) return;
                var tree = BaseText();
                TextPatch.Apply(tree, mine);
                var written = Markup.Print(tree.Root);
                WriteFile(written);
                Record(asset, written, tree, objects);
                Set(TwinStatus.Synced, $"wrote {Count(mine)} to {Name}: {Summary(mine)}");
                Debug.Log($"[Scenoco] {asset.Name}: wrote {Count(mine)} to {Name}: {Summary(mine)}");
            });
        }

        // --- a choice ----------------------------------------------------------------------------

        public string Resolve(Resolution choice, bool openEditor = false)
        {
            using var _ = Defaults.Operation();
            if (!NeedsChoice) return $"{Name}: nothing to resolve ({Status})";
            if (!Target.IsOpen)
            {
                // Nobody has it open any more, so there is no unsaved work left to weigh against the file.
                Poll(true);
                return $"{Name}: {Status}, {Message}";
            }
            string result = null;
            Target.With(asset => result = Resolve(asset, choice, openEditor));
            return result;
        }

        string Resolve(LoadedAsset asset, Resolution choice, bool openEditor)
        {
            var text = File.ReadAllText(TextPath);
            var objects = asset.Objects();
            var mine = SceneDiff.Diff(BaseExport(), Export(asset, objects));
            var ours = BaseText();
            TextPatch.Apply(ours, mine);
            var oursText = Markup.Print(ours.Root);

            switch (choice)
            {
                case Resolution.KeepAsset:
                {
                    WriteFile(oursText);
                    asset.Save();
                    Record(asset, oursText, ours, objects);
                    return Set(TwinStatus.Synced, $"kept {asset.Name}'s changes: {Name} was rewritten from it");
                }
                case Resolution.TakeFile:
                {
                    if (ConflictMarkers.In(text)) return Set(TwinStatus.Merging, $"{Name} still has conflict markers");
                    var after = TreeMatcher.Match(ours, Markup.Parse(text, TextPath));
                    var changes = SceneDiff.Diff(ours, after);
                    SceneApply.Apply(asset.Scene, after, objects, changes, true, $"Apply {Name}");
                    asset.Save();
                    Record(asset, text, after, objects);
                    return Set(TwinStatus.Synced, $"took the file: applied {Count(changes)} from {Name}, replacing {asset.Name}'s unsaved changes");
                }
                default:
                {
                    if (ConflictMarkers.In(text)) return Set(TwinStatus.Merging, $"{Name} already has conflict markers to resolve");
                    var before = BaseText();
                    var theirsTree = TreeMatcher.Match(before, Markup.Parse(text, TextPath));
                    var theirs = SceneDiff.Diff(before, theirsTree);
                    var conflicts = SceneDiff.Conflicts(mine, theirs);
                    var mineOut = new HashSet<Change>(conflicts.Select(c => c.mine));
                    var theirsOut = new HashSet<Change>(conflicts.Select(c => c.theirs));

                    // The file's changes into the asset, the asset's into the file — except where they clash.
                    SceneApply.Apply(asset.Scene, theirsTree, objects, theirs.Where(t => !theirsOut.Contains(t)).ToList(), true, $"Merge {Name}");
                    TextPatch.Apply(theirsTree, mine.Where(m => !mineOut.Contains(m)));
                    var merged = Markup.Print(theirsTree.Root);
                    if (conflicts.Count == 0)
                    {
                        WriteFile(merged);
                        asset.Save();
                        Record(asset, merged, theirsTree, objects);
                        return Set(TwinStatus.Synced, $"merged {Count(mine)} from {asset.Name} with {Count(theirs)} from {Name}");
                    }

                    // What the asset now holds: the merge plus its own side of each clash. The file gets
                    // both sides of each clash as conflict markers; resolving them is an edit of the file.
                    var held = KeyedTree.Parse(merged, theirsTree.KeyList(), TextPath);
                    TextPatch.Apply(held, mineOut);
                    var heldText = Markup.Print(held.Root);
                    var marked = ConflictMarkers.Merge(heldText, state.text, merged, $"{asset.Name} (your changes)", Name, out _);
                    var line = ConflictMarkers.FirstLine(marked);
                    WriteFile(marked);
                    asset.Save();
                    Record(asset, heldText, held, objects);
                    if (openEditor) UnityEditorInternal.InternalEditorUtility.OpenFileAtLineExternal(Path.GetFullPath(TextPath), line);
                    return Set(TwinStatus.Merging, $"{conflicts.Count} conflicting change{(conflicts.Count == 1 ? "" : "s")} marked in {Name} from line {line}: keep one side of each and save; the rest is merged");
                }
            }
        }

        // --- the base ------------------------------------------------------------------------------

        /// <summary>A twin with no base yet (a fresh clone, or Library cleared): the asset as it is becomes the base.</summary>
        void Bootstrap(LoadedAsset asset, Dictionary<string, GameObject> objects)
        {
            var export = Export(asset, objects);
            var text = Markup.Write(export.Root);
            state = new Base { text = text, textKeys = export.KeyList(), export = text, exportKeys = export.KeyList() };
            Store();
        }

        /// <summary>The open asset has unsaved changes that its text doesn't have.</summary>
        bool Changed(LoadedAsset asset, Dictionary<string, GameObject> objects) =>
            asset.Unsaved(objects) && Markup.Write(Export(asset, objects).Root) != state.export;

        KeyedTree BaseText() => KeyedTree.Parse(state.text, state.textKeys, TextPath);

        KeyedTree BaseExport() => KeyedTree.Parse(state.export, state.exportKeys);

        KeyedTree Export(LoadedAsset asset, Dictionary<string, GameObject> objects)
        {
            // Ids the file already uses stay the ids its objects are referenced by.
            var preferred = new Dictionary<GameObject, string>();
            if (state != null)
            {
                var text = BaseText();
                foreach (var e in text.Objects())
                    if (e.Attr("id") is { } id && text.Keys.TryGetValue(e, out var key) && objects.TryGetValue(key, out var go) && go != null)
                        preferred[go] = id;
            }
            return asset.Export(preferred);
        }

        /// <summary>The two agree: remember the text and the asset's export, with every object's key.</summary>
        void Record(LoadedAsset asset, string text, KeyedTree tree, Dictionary<string, GameObject> objects)
        {
            // Objects made during this pass had session keys; saved, they have their file ids.
            var keys = tree.KeyList().Select(k => k != null && objects.TryGetValue(k, out var go) && go != null ? LoadedAsset.KeyOf(go) : k).ToList();
            state = new Base { text = text, textKeys = keys };
            var export = Export(asset, asset.Objects());
            state.export = Markup.Write(export.Root);
            state.exportKeys = export.KeyList();
            Store();
        }

        string StatePath
        {
            get
            {
                using var sha = SHA1.Create();
                var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(TextPath))).Replace("-", "").Substring(0, 16);
                return $"Library/Scenoco/{hash}.json";
            }
        }

        /// <summary>Drop the base: the next check starts over from the asset as it is.</summary>
        public void Forget()
        {
            state = null;
            if (File.Exists(StatePath)) File.Delete(StatePath);
            Set(TwinStatus.Synced, "");
        }

        Base Load() => File.Exists(StatePath) ? JsonUtility.FromJson<Base>(File.ReadAllText(StatePath)) : null;

        void Store()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonUtility.ToJson(state));
        }

        // --- reporting -------------------------------------------------------------------------------

        void WriteFile(string text)
        {
            File.WriteAllText(TextPath, text);
            var info = new FileInfo(TextPath);
            seenTime = info.LastWriteTimeUtc;
            seenLength = info.Length;
        }

        string Set(TwinStatus status, string message)
        {
            Status = status;
            Message = message;
            if (status == TwinStatus.Synced) loggedProblem = null;
            TwinWindow.Refresh();
            return $"{Name}: {message}";
        }

        internal void Problem(TwinStatus status, string message)
        {
            Set(status, message);
            if (loggedProblem == message) return;
            loggedProblem = message;
            var answer = status == TwinStatus.Conflict
                ? $" — choose in Window ▸ Scenoco, or Scenoco.Files.Resolve(\"{TextPath}\", \"keep\" | \"file\" | \"merge\")"
                : "";
            Debug.LogWarning($"[Scenoco] {message}{answer}");
        }

        /// <summary>What was changed, by kind, so a report shows at a glance if anything was added or removed.</summary>
        static string Summary(IEnumerable<Change> changes) =>
            string.Join(", ", changes.GroupBy(c => c.Kind).Select(g => $"{g.Count()} {g.Key}"));

        static string Count(ICollection<Change> changes) => changes.Count == 1 ? "1 change" : $"{changes.Count} changes";
    }
}
