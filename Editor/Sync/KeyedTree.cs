using System.Xml.Linq;
using System.Collections.Generic;
using System.Linq;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Sync layer: a scene's text with identity — every <GameObject> element paired with the key of
    // the object it is (its id in the asset file, see LoadedAsset.KeyOf).
    //
    // Every comparison in Sync/ is between two keyed trees: the file before and after an edit,
    // or the scene's export before and after a save. Keys are what make "Ball was renamed Paddle"
    // a rename and not a delete plus a create.
    //
    //   KeyedTree    the tree and its keys; persisted as text plus the keys in document order
    //   TreeMatcher  keys for a tree someone edited, by matching it against the tree it came from:
    //                same name under the same parent → same object (the n-th "brick" is the n-th
    //                "brick"); then, among what's left, the same components → a rename; then a
    //                uniquely named object anywhere → a move to another parent; the rest is new
    // ------------------------------------------------------------------------------------------

    public sealed class KeyedTree
    {
        public readonly XElement Root;
        public readonly Dictionary<XElement, string> Keys = new();

        public KeyedTree(XElement root) => Root = root;

        /// <summary>An element that is a GameObject below the root: an ordinary one or a placed prefab.</summary>
        public static bool IsObject(XElement e) => e.Name == "GameObject" || e.Name == PrefabInstances.Element;

        /// <summary>A prefab file's root element is its root object; a scene's root element is no object.</summary>
        public bool RootIsObject => Root.Name == "Prefab";

        /// <summary>Every object element, parents before children, in document order.</summary>
        public IEnumerable<XElement> Objects() => RootIsObject ? Below(Root).Prepend(Root) : Below(Root);

        static IEnumerable<XElement> Below(XElement e)
        {
            foreach (var child in e.Elements())
            {
                if (!IsObject(child)) continue;
                yield return child;
                foreach (var d in Below(child)) yield return d;
            }
        }

        /// <summary>The keys in document order, as persisted next to the text.</summary>
        public List<string> KeyList() => Objects().Select(e => Keys.TryGetValue(e, out var k) ? k : null).ToList();

        public Dictionary<string, XElement> ByKey()
        {
            var d = new Dictionary<string, XElement>();
            foreach (var kv in Keys) d[kv.Value] = kv.Key;
            return d;
        }

        public static KeyedTree Parse(string text, IList<string> keys, string source = null)
        {
            var tree = new KeyedTree(Markup.Parse(text, source));
            var i = 0;
            foreach (var e in tree.Objects())
            {
                if (keys != null && i < keys.Count && !string.IsNullOrEmpty(keys[i])) tree.Keys[e] = keys[i];
                i++;
            }
            return tree;
        }
    }

    public static class TreeMatcher
    {
        static int fresh;

        /// <summary>Keys for <paramref name="edited"/>, an edit of <paramref name="original"/>. New elements get new keys ("text:…").</summary>
        public static KeyedTree Match(KeyedTree original, XElement edited)
        {
            var result = new KeyedTree(edited);
            var used = new HashSet<XElement>();
            if (result.RootIsObject && original.Keys.TryGetValue(original.Root, out var rootKey)) result.Keys[edited] = rootKey;
            Children(original, original.Root, edited, result, used);

            // An object moved to another parent: a unique name with the same components.
            var left = original.Objects().Where(o => !used.Contains(o) && original.Keys.ContainsKey(o)).ToList();
            foreach (var n in result.Objects().Where(n => !result.Keys.ContainsKey(n)).ToList())
            {
                if (result.Keys.ContainsKey(n)) continue;
                var name = n.Attr("name");
                var candidates = left.Where(o => o.Attr("name") == name && Kind(o) == Kind(n) && Signature(o) == Signature(n)).ToList();
                if (candidates.Count != 1 || result.Objects().Count(x => x.Attr("name") == name && !result.Keys.ContainsKey(x)) != 1) continue;
                result.Keys[n] = original.Keys[candidates[0]];
                used.Add(candidates[0]);
                left.Remove(candidates[0]);
                Children(original, candidates[0], n, result, used);
            }

            foreach (var n in result.Objects())
                if (!result.Keys.ContainsKey(n)) result.Keys[n] = "text:" + ++fresh;
            return result;
        }

        static void Children(KeyedTree original, XElement from, XElement to, KeyedTree result, HashSet<XElement> used)
        {
            var olds = from.Elements().Where(KeyedTree.IsObject).ToList();
            var news = to.Elements().Where(KeyedTree.IsObject).ToList();
            var pairs = new List<(XElement o, XElement n)>();

            var byName = olds.GroupBy(o => Kind(o) + (o.Attr("name") ?? "")).ToDictionary(g => g.Key, g => new Queue<XElement>(g));
            var unmatched = new List<XElement>();
            foreach (var n in news)
            {
                if (byName.TryGetValue(Kind(n) + (n.Attr("name") ?? ""), out var q) && q.Count > 0) pairs.Add((q.Dequeue(), n));
                else unmatched.Add(n);
            }
            var leftOld = olds.Where(o => pairs.All(p => p.o != o)).ToList();
            foreach (var n in unmatched)
            {
                var o = leftOld.FirstOrDefault(x => Kind(x) == Kind(n) && Signature(x) == Signature(n));   // a rename
                if (o == null) continue;
                pairs.Add((o, n));
                leftOld.Remove(o);
            }

            foreach (var (o, n) in pairs)
            {
                if (!original.Keys.TryGetValue(o, out var key)) continue;
                result.Keys[n] = key;
                used.Add(o);
                Children(original, o, n, result, used);
            }
        }

        /// <summary>What an object is — an ordinary one, or a placement of one prefab. An edit never turns one into another: that's a new object.</summary>
        static string Kind(XElement go) => go.Tag() + "|" + go.Attr("prefab") + "|";

        /// <summary>The kinds of components an object has, which a rename doesn't change.</summary>
        static string Signature(XElement go) => string.Join(",", go.Elements().Where(c => !KeyedTree.IsObject(c)).Select(c => c.Name));
    }
}
