using System.Xml.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Sync layer: changes → an edit of a scene's objects, as the Editor makes edits: objects and
    // components through ObjectFactory and Undo, values through their properties and fields
    // (ObjectWriter). With undo on, the whole edit is one Undo step named after the file.
    //
    // A value the text no longer writes goes back to its default: a fresh object's value
    // (Defaults), the same baseline the reader leaves out. Only names a change names are touched,
    // so data the text never describes (bake output, editor state) is never reset.
    //
    // A placed prefab (PrefabInstances) follows Unity's override rule: writing a value makes an
    // override; a value, component or child element the text stops writing goes back to the
    // prefab's — it's reverted, never destroyed — and an element naming a child the prefab already
    // has means that child. What the prefab has and the placement doesn't is listed, not omitted:
    // removedComponents, removedGameObjects.
    //
    // Order: a variant's base; what placements get back of their prefab (so an element naming it
    // finds it); objects (created, moved); what placements no longer have (so a component's ordinal
    // means what it means in the text); names and GameObject attributes,
    // then component values — so a reference to a new or renamed object resolves — then components
    // and objects removed. Components a change removes are found first, by the old text's ordinals.
    // ------------------------------------------------------------------------------------------

    public static class SceneApply
    {
        /// <param name="target">The text the scene is being brought to, keyed: it gives references and error lines.</param>
        /// <param name="objects">Every object of the scene by key; objects this creates are added to it.</param>
        /// <returns>Values Unity changed as they were applied (ObjectWriter), as "file:line: …".</returns>
        public static IReadOnlyList<string> Apply(Scene scene, KeyedTree target, Dictionary<string, GameObject> objects, IReadOnlyList<Change> changes, bool undo, string label)
        {
            if (changes.Count == 0) return Array.Empty<string>();
            var group = Undo.GetCurrentGroup();
            if (undo)
            {
                Undo.IncrementCurrentGroup();
                group = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName(label);
            }
            var ctx = new ImportContext { RecordUndo = undo, Target = scene };
            var elements = target.ByKey();
            var created = new List<(GameObject go, XElement e)>();
            var whole = new List<(Component c, XElement e)>();
            GameObject Get(string key) => key != null && objects.TryGetValue(key, out var go) && go != null ? go : null;

            var doomed = changes.Where(c => c.Kind == ChangeKind.RemoveComponent)
                .Select(c => Get(c.Object) is { } go ? Find(go, c.Component) : null).Where(c => c != null).ToList();

            // 0. A variant's base, which everything else of the prefab sits on.
            foreach (var c in changes.Where(c => c.Component == null && c.Field == PrefabInstances.Base && c.Kind is ChangeKind.SetAttribute or ChangeKind.RemoveAttribute))
                if (Get(c.Object) is { } root && elements.TryGetValue(c.Object, out var at)) PrefabInstances.SetBase(root, at, undo);

            // What placements don't have of their prefab: what comes back now, what goes after the objects.
            var lists = changes.Where(c => c.Component == null && c.Kind is ChangeKind.SetProperty or ChangeKind.RemoveProperty)
                .Select(c => (go: Get(c.Object), c.Field, list: c.Kind == ChangeKind.SetProperty ? c.Element : null)).Where(x => x.go != null).ToList();
            foreach (var (go, field, list) in lists) PrefabInstances.Restore(go, field, list, undo);

            // 1. Objects created and moved, parents before children.
            foreach (var c in changes)
            {
                if (c.Kind == ChangeKind.AddObject)
                {
                    var parent = Get(c.Parent);
                    GameObject go = null;
                    if (c.Element.Name == PrefabInstances.Element) go = PrefabInstances.Create(c.Element, scene);
                    else
                    {
                        var name = c.Element.Attr("name") ?? throw new ScenocoException(c.Element, "a GameObject needs a name");
                        var existing = parent != null ? PrefabInstances.ChildNamed(parent, name, c.Element) : null;
                        if (existing != null) objects[c.Object] = existing;     // the prefab's own child: overrides, nothing new
                        else go = BuildScene.CreateIn(scene, name);
                    }
                    if (go != null)
                    {
                        if (undo) Undo.RegisterCreatedObjectUndo(go, label);
                        objects[c.Object] = go;
                        Place(go, parent, Get(c.After), undo, label);
                    }
                    go = objects[c.Object];
                    foreach (var p in SceneDiff.PropertyElements(c.Element, null))
                    {
                        PrefabInstances.Restore(go, p.FieldIn(c.Element), p, undo);
                        PrefabInstances.Remove(go, p.FieldIn(c.Element), p, undo);
                    }
                    var added = new Dictionary<Type, int>();
                    foreach (var (_, e) in SceneDiff.Components(c.Element))
                        whole.Add((GameObjectTreeAdapter.ComponentFor(go, ComponentType(e), added, e), e));
                    created.Add((go, c.Element));
                }
                else if (c.Kind == ChangeKind.MoveObject && Get(c.Object) is { } moved && !PrefabInstances.IsChild(moved))
                    Place(moved, Get(c.Parent), Get(c.After), undo, label);
            }

            // Everything in the asset can be referenced, written or not (a placed prefab's untouched child) —
            // but not what this change removes: a name it shared with a new object is the new one's now.
            var leaving = changes.Where(c => c.Kind == ChangeKind.RemoveObject).Select(c => Get(c.Object)).Where(go => go != null).ToList();
            foreach (var go in objects.Values.Where(go => go != null).Distinct())
                if (!leaving.Any(l => go.transform.IsChildOf(l.transform))) ctx.References.RegisterUnwritten(go);
            foreach (var kv in elements)
                if (Get(kv.Key) is { } go) ctx.References.Register(go, kv.Value);

            foreach (var (go, field, list) in lists) PrefabInstances.Remove(go, field, list, undo);

            // 2. Components added to existing objects.
            foreach (var c in changes.Where(c => c.Kind == ChangeKind.AddComponent))
            {
                var go = Get(c.Object);
                if (go == null) continue;
                var (type, ordinal) = SceneDiff.ComponentOf(c.Component);
                type ??= ComponentType(c.Element);
                var existing = Of(go, type);
                var component = ordinal < existing.Length ? existing[ordinal]
                    : undo ? Undo.AddComponent(go, type) : ObjectFactory.AddComponent(go, type);
                if (component == null) throw new ScenocoException(c.Element, $"{type.Name} can't be added to \"{go.name}\"");
                whole.Add((component, c.Element));
            }

            // 3. GameObject attributes: new objects whole, existing ones by change.
            foreach (var (go, e) in created)
                GameObjectTreeAdapter.WriteObject(go, e, ctx);
            foreach (var g in changes.Where(c => c.Component == null && c.Kind is ChangeKind.SetAttribute or ChangeKind.RemoveAttribute).GroupBy(c => c.Object))
            {
                var go = Get(g.Key);
                if (go == null) continue;
                var at = elements.TryGetValue(g.Key, out var el) ? el : null;
                var partial = new XElement("GameObject").StandingFor(at);
                foreach (var c in g)
                {
                    if (c.Field == "id") continue;
                    if (c.Field == "name")
                    {
                        if (c.Kind == ChangeKind.RemoveAttribute) throw new ScenocoException(at, "a GameObject needs a name");
                        var so = new SerializedObject(go);
                        so.FindProperty("m_Name").stringValue = c.Value;
                        ApplyProperties(so, undo);
                        continue;
                    }
                    if (c.Field is "prefab" or PrefabInstances.Base) continue;
                    if (c.Kind == ChangeKind.SetAttribute) partial.SetAttributeValue(c.Field, c.Value);
                    else ObjectWriter.Reset(go, c.Field, at, ctx);
                }
                if (partial.HasAttributes) ObjectWriter.Apply(go, partial, ctx, GameObjectTreeAdapter.Own);
            }

            // 4. Component values: new components whole, existing ones by change.
            foreach (var (component, e) in whole)
                ObjectWriter.Apply(component, e, ctx);
            foreach (var g in changes.Where(c => c.Component != null && c.Kind is ChangeKind.SetAttribute or ChangeKind.RemoveAttribute or ChangeKind.SetProperty or ChangeKind.RemoveProperty).GroupBy(c => (c.Object, c.Component)))
            {
                var go = Get(g.Key.Object);
                var component = go != null ? Find(go, g.Key.Component) : null;
                if (component == null) continue;
                var at = elements.TryGetValue(g.Key.Object, out var el) ? SceneDiff.Components(el).FirstOrDefault(x => x.key == g.Key.Component).element : null;
                var name = at?.Tag() ?? component.GetType().Name;
                foreach (var c in g.Where(c => c.Kind is ChangeKind.RemoveAttribute or ChangeKind.RemoveProperty))
                    ObjectWriter.Reset(component, c.Field, at, ctx);

                var partial = new XElement(name).StandingFor(at);
                foreach (var c in g)
                {
                    if (c.Kind == ChangeKind.SetAttribute)
                        partial.SetAttributeValue(c.Field, c.Value);
                    else if (c.Kind == ChangeKind.SetProperty)
                    {
                        var e = Markup.Plain(c.Element).StandingFor(c.Element);
                        e.Name = $"{name}.{c.Field}";
                        partial.Add(e);
                    }
                }
                if (partial.HasAttributes || partial.HasElements) ObjectWriter.Apply(component, partial, ctx);
            }

            // 5. Removed: components (found at the start), then objects.
            foreach (var component in doomed)
            {
                if (component == null) continue;                                                       // gone already: listed as removed
                if (PrefabInstances.Source(component) != null) PrefabInstances.Revert(component, undo);   // the prefab's: back to its values
                else Destroy(component, undo);
            }
            foreach (var c in changes.Where(c => c.Kind == ChangeKind.RemoveObject))
            {
                if (Get(c.Object) is not { } go) continue;
                if (!PrefabInstances.IsChild(go)) Destroy(go, undo);
                else
                    foreach (var part in go.GetComponents<Component>().Cast<Object>().Prepend(go))
                        if (part != null && PrefabInstances.Source(part) != null) PrefabInstances.Revert(part, undo);
            }

            if (!EditorSceneManager.IsPreviewScene(scene)) EditorSceneManager.MarkSceneDirty(scene);
            if (undo) Undo.CollapseUndoOperations(group);
            return ctx.Changed;
        }

        static void Place(GameObject go, GameObject parent, GameObject after, bool undo, string label)
        {
            var t = go.transform;
            var p = parent != null ? parent.transform : null;
            if (t.parent != p)
            {
                if (undo) Undo.SetTransformParent(t, p, false, label);
                else t.SetParent(p, false);
            }
            var index = 0;
            if (after != null && after.transform.parent == p)
            {
                index = after.transform.GetSiblingIndex();
                if (t.GetSiblingIndex() > index) index++;
            }
            if (t.GetSiblingIndex() == index) return;
            if (undo) Undo.SetSiblingIndex(t, index, label);
            else t.SetSiblingIndex(index);
        }

        /// <summary>The component a key names on an object: exact type, by ordinal — as the reader lists them.</summary>
        static Component Find(GameObject go, string key)
        {
            var (type, ordinal) = SceneDiff.ComponentOf(key);
            if (type == null) return null;
            var all = Of(go, type);
            return ordinal < all.Length ? all[ordinal] : null;
        }

        static Component[] Of(GameObject go, Type type) => go.GetComponents(type).Where(c => c.GetType() == type).ToArray();

        static Type ComponentType(XElement e) =>
            FieldTypes.Unity(e.Tag(), typeof(Component)) ?? throw new ScenocoException(e, $"no component is named {e.Name}");

        static void ApplyProperties(SerializedObject so, bool undo)
        {
            if (undo) so.ApplyModifiedProperties();
            else so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void Destroy(Object o, bool undo)
        {
            if (undo) Undo.DestroyObjectImmediate(o);
            else Object.DestroyImmediate(o);
        }
    }
}
