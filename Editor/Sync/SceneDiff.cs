using System.Xml.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Sync layer: what changed between two keyed trees, as a list of changes both sides speak.
    //
    // A change names its object by key, its component by type and ordinal ("UnityEngine.Rigidbody2D#0"),
    // and its field by the name written in the file. The same list can be applied to a file
    // (TextPatch: an edit of the text) or to a scene (SceneApply: an edit of the objects), and two
    // lists — the person's and the file's — can be checked against each other (Conflicts).
    //
    // Order of a diff: objects created and moved (parents first, siblings in order, so every
    // "after" already exists), then values and components, then objects removed.
    // ------------------------------------------------------------------------------------------

    public enum ChangeKind
    {
        AddObject, MoveObject,
        SetAttribute, RemoveAttribute, SetProperty, RemoveProperty, AddComponent, RemoveComponent,
        RemoveObject,
    }

    public sealed class Change
    {
        public ChangeKind Kind;
        public string Object;          // the GameObject's key
        public string Component;       // null: the GameObject itself (name, position, …)
        public string Field;           // an attribute or property element name
        public string Value;           // SetAttribute
        public XElement Element;  // SetProperty, AddComponent, AddObject: the new element
        public string Parent;          // AddObject, MoveObject: the parent's key ("" for the scene root)
        public string After;           // AddObject, MoveObject, AddComponent: the previous sibling's key, or null for first

        internal string Target => Component == null ? Object : Object + "|" + Component;
        internal string FieldKey => Target + "|" + Field;

        public override string ToString() => Kind switch
        {
            ChangeKind.SetAttribute => $"{Target}.{Field} = {Value}",
            ChangeKind.RemoveAttribute or ChangeKind.RemoveProperty => $"{Target}.{Field} reset",
            ChangeKind.SetProperty => $"{Target}.{Field} = <…>",
            _ => $"{Kind} {Target}",
        };
    }

    public static class SceneDiff
    {
        public static List<Change> Diff(KeyedTree a, KeyedTree b)
        {
            var before = Index(a);
            var after = Index(b);
            var structure = new List<Change>();
            var values = new List<Change>();
            var removed = new List<Change>();

            // Created and moved, walking the new tree in order.
            var oldOrder = before.Values.GroupBy(n => n.Parent).ToDictionary(g => g.Key, g => g.Select(n => n.Key).ToList());
            var stable = new HashSet<string>();
            foreach (var group in after.Values.GroupBy(n => n.Parent))
            {
                var parent = group.Key;
                var kept = group.Select(n => n.Key).Where(k => before.TryGetValue(k, out var o) && o.Parent == parent).ToList();
                if (!oldOrder.TryGetValue(parent, out var old)) continue;
                foreach (var k in LongestIncreasing(kept, k => old.IndexOf(k))) stable.Add(k);
            }
            foreach (var e in b.Objects())
            {
                var n = after[b.Keys[e]];
                if (!before.TryGetValue(n.Key, out var o))
                    structure.Add(new Change { Kind = ChangeKind.AddObject, Object = n.Key, Parent = n.Parent, After = n.Previous, Element = e });
                else if (!stable.Contains(n.Key))
                    structure.Add(new Change { Kind = ChangeKind.MoveObject, Object = n.Key, Parent = n.Parent, After = n.Previous });
                if (o != null) Content(o.Element, n.Element, n.Key, values);
            }
            foreach (var e in a.Objects().Reverse())
            {
                var key = a.Keys[e];
                if (!after.ContainsKey(key)) removed.Add(new Change { Kind = ChangeKind.RemoveObject, Object = key });
            }
            return structure.Concat(values).Concat(removed).ToList();
        }

        /// <summary>
        /// Pairs of changes that can't both be kept: one field set to two values (or set and reset),
        /// a component or object removed on one side and edited on the other, an object moved to two places.
        /// </summary>
        public static List<(Change mine, Change theirs)> Conflicts(IReadOnlyList<Change> mine, IReadOnlyList<Change> theirs)
        {
            var result = new List<(Change, Change)>();
            foreach (var m in mine)
                foreach (var t in theirs)
                    if (Clash(m, t)) result.Add((m, t));
            return result;
        }

        static bool Clash(Change m, Change t)
        {
            if (IsField(m) && IsField(t)) return m.FieldKey == t.FieldKey && !SameEffect(m, t);
            if (m.Kind == ChangeKind.RemoveObject || t.Kind == ChangeKind.RemoveObject)
            {
                if (m.Kind == t.Kind) return false;
                var gone = m.Kind == ChangeKind.RemoveObject ? m : t;
                var other = gone == m ? t : m;
                return other.Object == gone.Object || other.Parent == gone.Object;
            }
            if (m.Kind == ChangeKind.RemoveComponent || t.Kind == ChangeKind.RemoveComponent)
                return m.Kind != t.Kind && m.Component != null && m.Target == t.Target;
            if (m.Kind == ChangeKind.AddComponent && t.Kind == ChangeKind.AddComponent)
                return m.Target == t.Target && Markup.Canonical(m.Element) != Markup.Canonical(t.Element);
            if (m.Kind == ChangeKind.MoveObject && t.Kind == ChangeKind.MoveObject)
                return m.Object == t.Object && m.Parent != t.Parent;
            return false;
        }

        static bool IsField(Change c) => c.Kind is ChangeKind.SetAttribute or ChangeKind.RemoveAttribute or ChangeKind.SetProperty or ChangeKind.RemoveProperty;

        static bool SameEffect(Change m, Change t) =>
            (m.Kind is ChangeKind.RemoveAttribute or ChangeKind.RemoveProperty && t.Kind is ChangeKind.RemoveAttribute or ChangeKind.RemoveProperty)
            || (m.Kind == ChangeKind.SetAttribute && t.Kind == ChangeKind.SetAttribute && m.Value == t.Value)
            || (m.Kind == ChangeKind.SetProperty && t.Kind == ChangeKind.SetProperty && Markup.Canonical(m.Element) == Markup.Canonical(t.Element));

        // --- one object's content -----------------------------------------------------------

        static void Content(XElement a, XElement b, string key, List<Change> into)
        {
            Attributes(a, b, key, null, into);
            Properties(a, b, key, null, into);          // the object's own property elements (a placement's removedComponents, …)
            var before = Components(a);
            var after = Components(b);
            string previous = null;
            foreach (var (ck, n) in after)
            {
                var o = before.FirstOrDefault(c => c.key == ck).element;
                if (o == null) into.Add(new Change { Kind = ChangeKind.AddComponent, Object = key, Component = ck, Element = n, After = previous });
                else
                {
                    Attributes(o, n, key, ck, into);
                    Properties(o, n, key, ck, into);
                }
                previous = ck;
            }
            foreach (var (ck, _) in before)
                if (after.All(c => c.key != ck)) into.Add(new Change { Kind = ChangeKind.RemoveComponent, Object = key, Component = ck });
        }

        static void Attributes(XElement a, XElement b, string key, string component, List<Change> into)
        {
            foreach (var n in b.Attributes())
                if (a.Attr(n.Name.LocalName) != n.Value)
                    into.Add(new Change { Kind = ChangeKind.SetAttribute, Object = key, Component = component, Field = n.Name.LocalName, Value = n.Value });
            foreach (var o in a.Attributes())
                if (b.Attribute(o.Name) == null)
                    into.Add(new Change { Kind = ChangeKind.RemoveAttribute, Object = key, Component = component, Field = o.Name.LocalName });
        }

        static void Properties(XElement a, XElement b, string key, string component, List<Change> into)
        {
            foreach (var n in PropertyElements(b, component))
            {
                var field = n.FieldIn(b) ?? n.Tag();
                var o = PropertyElements(a, component).FirstOrDefault(c => (c.FieldIn(a) ?? c.Tag()) == field);
                if (o == null || Markup.Canonical(o) != Markup.Canonical(n))
                    into.Add(new Change { Kind = ChangeKind.SetProperty, Object = key, Component = component, Field = field, Element = n });
            }
            foreach (var o in PropertyElements(a, component))
            {
                var field = o.FieldIn(a) ?? o.Tag();
                if (PropertyElements(b, component).All(c => (c.FieldIn(b) ?? c.Tag()) != field))
                    into.Add(new Change { Kind = ChangeKind.RemoveProperty, Object = key, Component = component, Field = field });
            }
        }

        /// <summary>A component's elements are all its fields; an object's are its components and children, and only &lt;GameObject.field&gt; ones are fields.</summary>
        public static IEnumerable<XElement> PropertyElements(XElement e, string component) =>
            component != null ? e.Elements() : e.Elements().Where(p => p.FieldIn(e) != null);

        // --- components: keyed by type and ordinal ----------------------------------------------

        static readonly Dictionary<string, Type> types = new();

        /// <summary>A GameObject element's components, each with its key: the type's full name and its ordinal among that type.</summary>
        public static List<(string key, XElement element)> Components(XElement go)
        {
            var result = new List<(string, XElement)>();
            var counts = new Dictionary<string, int>();
            foreach (var c in go.Elements())
            {
                if (KeyedTree.IsObject(c) || c.FieldIn(go) != null) continue;
                var type = TypeName(c.Tag());
                counts[type] = counts.TryGetValue(type, out var n) ? n + 1 : 1;
                result.Add(($"{type}#{counts[type] - 1}", c));
            }
            return result;
        }

        /// <summary>The component type and ordinal a component key names.</summary>
        public static (Type type, int ordinal) ComponentOf(string key)
        {
            var hash = key.LastIndexOf('#');
            var name = key.Substring(0, hash);
            TypeName(name);
            return (types.TryGetValue(name, out var t) ? t : null, int.Parse(key.Substring(hash + 1)));
        }

        /// <summary>The type's full name, however it's written (Camera, UnityEngine.Camera) — always the same key.</summary>
        static string TypeName(string written)
        {
            if (types.TryGetValue(written, out var known)) return known.FullName;
            var type = FieldTypes.Unity(written, typeof(Component));
            if (type == null) return written;
            types[written] = types[type.FullName] = type;
            return type.FullName;
        }

        // --- structure ------------------------------------------------------------------------

        sealed class Node
        {
            public string Key, Parent, Previous;
            public XElement Element;
        }

        static Dictionary<string, Node> Index(KeyedTree tree)
        {
            var nodes = new Dictionary<string, Node>();
            void Walk(XElement parent, string parentKey)
            {
                string previous = null;
                foreach (var child in parent.Elements())
                {
                    if (!KeyedTree.IsObject(child)) continue;
                    if (!tree.Keys.TryGetValue(child, out var key))
                        throw new ScenocoException(child, "internal: an object element has no key");
                    nodes[key] = new Node { Key = key, Parent = parentKey, Previous = previous, Element = child };
                    previous = key;
                    Walk(child, key);
                }
            }
            if (tree.RootIsObject)
            {
                // A prefab's root object: it has no parent and no siblings, and is never added or removed.
                if (!tree.Keys.TryGetValue(tree.Root, out var rootKey)) throw new ScenocoException(tree.Root, "internal: the prefab root has no key");
                nodes[rootKey] = new Node { Key = rootKey, Parent = "^", Element = tree.Root };
                Walk(tree.Root, rootKey);
            }
            else Walk(tree.Root, "");
            return nodes;
        }

        /// <summary>The longest run of <paramref name="items"/> whose ranks increase: the siblings that kept their order.</summary>
        static List<string> LongestIncreasing(List<string> items, Func<string, int> rank)
        {
            var n = items.Count;
            var length = new int[n];
            var previous = new int[n];
            var best = -1;
            for (var i = 0; i < n; i++)
            {
                length[i] = 1;
                previous[i] = -1;
                for (var j = 0; j < i; j++)
                    if (rank(items[j]) < rank(items[i]) && length[j] + 1 > length[i])
                    {
                        length[i] = length[j] + 1;
                        previous[i] = j;
                    }
                if (best < 0 || length[i] > length[best]) best = i;
            }
            var result = new List<string>();
            for (var i = best; i >= 0; i = previous[i]) result.Add(items[i]);
            return result;
        }
    }
}
