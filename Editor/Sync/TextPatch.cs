using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using UnityEngine;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Sync layer: changes → an edit of a file's tree. Only what a change names is touched: one
    // attribute's value, one property element, one component element, one object element.
    // Everything else — other attributes, comments, blank lines, the order things were written
    // in — stays as the person or agent wrote it (Markup.Insert / Remove / Print).
    //
    // A value the file writes under another name that stores the same data (localEulerAngles,
    // read back as localRotation) is replaced where it was written.
    // ------------------------------------------------------------------------------------------

    public static class TextPatch
    {
        public static void Apply(KeyedTree tree, IEnumerable<Change> changes)
        {
            var byKey = tree.ByKey();
            changes = changes.ToList();
            // Names a change sets: a removal never takes one of them as its alias (localEulerAngles
            // removed and localRotation set in one save is one value written under a new name).
            var set = new HashSet<(string, string, string)>(changes.Where(c => c.Kind == ChangeKind.SetAttribute).Select(c => (c.Object, c.Component, c.Field)));
            XElement Object(string key) => string.IsNullOrEmpty(key) ? tree.Root : byKey.TryGetValue(key, out var e) ? e : null;

            // After the sibling it follows; else first among the objects, after the components.
            void Place(XElement parent, XElement e, XElement after)
            {
                if (after != null && after.Parent == parent) Markup.Insert(parent, e, after: after);
                else if (parent.Elements().LastOrDefault(c => !KeyedTree.IsObject(c)) is { } lastComponent) Markup.Insert(parent, e, after: lastComponent);
                else if (parent.Elements().FirstOrDefault() is { } first) Markup.Insert(parent, e, before: first);
                else Markup.Insert(parent, e);
            }

            foreach (var c in changes)
            {
                switch (c.Kind)
                {
                    case ChangeKind.AddObject:
                    {
                        var e = Markup.Plain(c.Element);
                        e.Elements().Where(KeyedTree.IsObject).ToList().Remove();       // its children arrive as their own changes
                        Place(Object(c.Parent) ?? tree.Root, e, c.After != null ? Object(c.After) : null);
                        tree.Keys[e] = c.Object;
                        byKey[c.Object] = e;
                        break;
                    }
                    case ChangeKind.MoveObject:
                    {
                        var e = Object(c.Object);
                        if (e == null) break;
                        Markup.Remove(e);
                        Place(Object(c.Parent) ?? tree.Root, e, c.After != null ? Object(c.After) : null);
                        break;
                    }
                    case ChangeKind.RemoveObject:
                    {
                        var e = Object(c.Object);
                        if (e == null || e == tree.Root) break;
                        foreach (var gone in e.DescendantsAndSelf().Where(tree.Keys.ContainsKey).ToList())
                        {
                            byKey.Remove(tree.Keys[gone]);
                            tree.Keys.Remove(gone);
                        }
                        Markup.Remove(e);
                        break;
                    }
                    case ChangeKind.SetAttribute:
                    case ChangeKind.RemoveAttribute:
                    {
                        var target = Target(Object(c.Object), c.Component);
                        if (target == null) break;
                        var existing = target.Attribute(c.Field);
                        var alias = existing == null && c.Component != null ? Alias(target, c.Field) : null;
                        if (alias != null && c.Kind == ChangeKind.RemoveAttribute && set.Contains((c.Object, c.Component, alias.Name.LocalName))) alias = null;
                        if (c.Kind == ChangeKind.RemoveAttribute) (existing ?? alias)?.Remove();
                        else if (existing != null) existing.Value = c.Value;
                        else if (alias != null)
                        {
                            // Written under the name the object reads back as, where the other name was.
                            Markup.InsertAttribute(target, target.Attributes().TakeWhile(x => x != alias).Count(), c.Field, c.Value);
                            alias.Remove();
                        }
                        else if (c.Component == null && c.Field == "id") Markup.InsertAttribute(target, 1, "id", c.Value);
                        else target.SetAttributeValue(c.Field, c.Value);
                        break;
                    }
                    case ChangeKind.SetProperty:
                    case ChangeKind.RemoveProperty:
                    {
                        var target = Target(Object(c.Object), c.Component);
                        if (target == null) break;
                        var old = SceneDiff.PropertyElements(target, c.Component).FirstOrDefault(p => (p.FieldIn(target) ?? p.Tag()) == c.Field);
                        if (c.Kind == ChangeKind.SetProperty)
                        {
                            var e = Markup.Plain(c.Element);
                            e.Name = $"{target.Tag()}.{c.Field}";
                            // A new one of an object's own goes after the others it has, ahead of its components and children.
                            var after = old ?? (c.Component == null ? SceneDiff.PropertyElements(target, null).LastOrDefault() : null);
                            if (after == null && c.Component == null && target.Elements().FirstOrDefault() is { } first) Markup.Insert(target, e, before: first);
                            else Markup.Insert(target, e, after: after);
                        }
                        if (old != null) Markup.Remove(old);
                        break;
                    }
                    case ChangeKind.AddComponent:
                    {
                        var go = Object(c.Object);
                        if (go == null) break;
                        Place(go, Markup.Plain(c.Element), c.After != null ? Target(go, c.After) : null);
                        break;
                    }
                    case ChangeKind.RemoveComponent:
                    {
                        var target = Target(Object(c.Object), c.Component);
                        if (target != null) Markup.Remove(target);
                        break;
                    }
                }
            }
        }

        static XElement Target(XElement go, string component)
        {
            if (go == null || component == null) return go;
            return SceneDiff.Components(go).FirstOrDefault(c => c.key == component).element;
        }

        /// <summary>
        /// The attribute that stores the same data as <paramref name="field"/> under another name (the text
        /// writes localEulerAngles, the object reads back as localRotation) — Unity says so: setting it, on a
        /// fresh object, changes what <paramref name="field"/> reads, and both store the same fields.
        /// </summary>
        static XAttribute Alias(XElement component, string field)
        {
            var type = FieldTypes.Unity(component.Tag(), typeof(Component));
            var x = type != null ? Members.Property(type, field) : null;
            var fresh = x != null ? Defaults.Fresh(type) : null;
            if (fresh == null) return null;
            foreach (var a in component.Attributes())
            {
                var p = a.Name.LocalName != field ? Members.Property(type, a.Name.LocalName) : null;
                if (p == null) continue;
                object value;
                try { value = ValueText.Parse(a.Value, p.PropertyType, new ImportContext(), component); }
                catch { continue; }
                if (Members.Probe(type, fresh, p, value) is not { } byA) continue;
                var reads = Members.Get(x, byA.after.targetObject, out var failed);
                if (failed || Members.Same(reads, Members.Get(x, fresh, out _))) continue;
                if (Members.Probe(type, fresh, x, reads) is { } byX && byX.changed.Intersect(byA.changed).Any()) return a;
            }
            return null;
        }
    }
}
