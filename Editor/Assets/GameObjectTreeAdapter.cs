using System.Xml.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Assets layer: prefabs and scenes — trees of GameObjects with components.
    //
    //   <Prefab name="Spawner">            the root element is the prefab's root object: components and
    //     <WaveSpawner … />                GameObject attributes written directly in it belong to that root
    //     <GameObject name="PointA">
    //       <Transform localPosition="-4 0 0" />
    //     </GameObject>
    //   </Prefab>
    //   <PrefabInstance prefab="Assets/Enemy.prefab" name="Enemy">
    //                                      a placed prefab; what it writes are overrides (PrefabInstances)
    //   <Prefab base="Assets/Enemy.prefab" name="Boss">
    //                                      a prefab variant: its root is a placed prefab, the same way
    //   <Scene name="Level1"> …            the root objects of a scene. Built detached, under a
    //                                      container; kept in step with a real scene by Sync/

    //
    // Building happens in three passes, so references and codecs never depend on file order:
    //   1. structure   every GameObject and every component exists (ObjectFactory, as the Editor's
    //                  menus do — presets and RequireComponent apply), and is registered for #references
    //   2. objects     each GameObject's own attributes: GameObject's properties (layer, tag) and fields (ObjectWriter)
    //   3. components  each component's values (ObjectWriter)
    // A GameObject's element names its object (name, and id to tell identical siblings apart); its
    // Transform is a component element like any other, written when it holds something.
    // ------------------------------------------------------------------------------------------

    public sealed class GameObjectTreeAdapter : IAssetAdapter
    {
        public IReadOnlyList<string> Roots { get; } = new[] { "Prefab", "Scene" };

        /// <summary>A GameObject element's own attributes, which aren't the GameObject's values.</summary>
        public static readonly string[] Own = { "name", "id", "prefab", PrefabInstances.Base };

        public bool CanDescribe(Object o) => o is GameObject;

        /// <summary>A GameObject's own values from its element: its name, then its attributes.</summary>
        public static void WriteObject(GameObject go, XElement e, ImportContext ctx)
        {
            if (e.Attr("name") is { } name && go.name != name)
            {
                if (ctx.RecordUndo) Undo.RecordObject(go, "Scenoco");
                go.name = name;
            }
            ObjectWriter.Apply(go, new XElement(e.Name, e.Attributes()).StandingFor(e), ctx, Own);
        }

        // --- text → objects ---------------------------------------------------------------------

        public Object Build(XElement root, ImportContext ctx)
        {
            var name = root.Attr("name") ?? throw new ScenocoException(root, $"<{root.Name}> needs a name");
            var top = root.Name == "Prefab" && root.Attr(PrefabInstances.Base) != null
                ? PrefabInstances.Place(PrefabInstances.Asset(root, PrefabInstances.Base), ctx.Target)   // a variant: saved, it's one
                : Create(name, null, ctx);
            var objects = new List<(GameObject go, XElement e)>();
            var components = new List<(Component c, XElement e)>();
            try
            {
                if (root.Name == "Prefab")
                {
                    ctx.References.Register(top, root);
                    objects.Add((top, root));
                }
                Structure(top, root, ctx, objects, components);
                foreach (var (go, e) in objects) WriteObject(go, e, ctx);
                foreach (var (c, e) in components) ObjectWriter.Apply(c, e, ctx);
                return top;
            }
            catch
            {
                Object.DestroyImmediate(top);
                throw;
            }
        }

        static void Structure(GameObject go, XElement e, ImportContext ctx, List<(GameObject, XElement)> objects, List<(Component, XElement)> components)
        {
            // What a placement doesn't have of its prefab goes first: the components written after are the ones it keeps.
            foreach (var list in SceneDiff.PropertyElements(e, null)) PrefabInstances.Remove(go, list.FieldIn(e), list, ctx.RecordUndo);
            var added = new Dictionary<Type, int>();
            foreach (var child in e.Elements())
            {
                if (child.FieldIn(e) != null) continue;
                if (child.Name == PrefabInstances.Element)
                {
                    // A placed prefab: everything it brings can be referenced; what the element writes are overrides.
                    var c = PrefabInstances.Create(child, ctx.Target);
                    c.transform.SetParent(go.transform, false);
                    foreach (var t in c.GetComponentsInChildren<Transform>(true)) ctx.References.RegisterUnwritten(t.gameObject);
                    ctx.References.Register(c, child);
                    objects.Add((c, child));
                    Structure(c, child, ctx, objects, components);
                }
                else if (child.Name == "GameObject")
                {
                    var name = child.Attr("name") ?? throw new ScenocoException(child, "a GameObject needs a name");
                    // Inside a placed prefab, the name of a child it already has means that child (an override).
                    var c = PrefabInstances.ChildNamed(go, name, child) ?? Create(name, go.transform, ctx);
                    ctx.References.Register(c, child);
                    objects.Add((c, child));
                    Structure(c, child, ctx, objects, components);
                }
                else
                {
                    var tag = child.Tag();
                    var owner = tag.Contains('.') ? tag.Substring(0, tag.LastIndexOf('.')) : null;
                    var type = FieldTypes.Unity(tag, typeof(Component))
                               ?? throw new ScenocoException(child, owner != null && FieldTypes.Unity(owner, typeof(Component)) != null
                                   ? $"<{tag}> is a field of a component: put it inside <{owner}>"
                                   : $"no component is named {tag}");
                    components.Add((ComponentFor(go, type, added, child), child));
                }
            }
        }

        static GameObject Create(string name, Transform parent, ImportContext ctx)
        {
            var go = BuildScene.CreateIn(ctx.Target, name);
            if (parent == null) return go;
            if (ctx.RecordUndo) Undo.SetTransformParent(go.transform, parent, "Scenoco");
            else go.transform.SetParent(parent, false);
            return go;
        }

        /// <summary>The component an element describes: the existing one (Transform, or one RequireComponent added) or a new one.</summary>
        internal static Component ComponentFor(GameObject go, Type type, Dictionary<Type, int> added, XElement at)
        {
            if (type == typeof(Transform)) return go.transform;
            if (type == typeof(RectTransform))
                return go.transform as RectTransform ?? ObjectFactory.AddComponent<RectTransform>(go);
            var nth = added.TryGetValue(type, out var n) ? n : 0;
            added[type] = nth + 1;
            var existing = go.GetComponents(type);
            if (nth < existing.Length) return existing[nth];
            var c = ObjectFactory.AddComponent(go, type);
            if (c == null) throw new ScenocoException(at, $"{type.Name} can't be added to \"{go.name}\" (another component may exclude it)");
            return c;
        }

        // --- objects → text ---------------------------------------------------------------------

        public XElement Describe(Object o) => Describe((GameObject)o, "Prefab");

        /// <summary>A GameObject tree as text: as a prefab (the object is the root) or a scene (its children are).</summary>
        public static XElement Describe(GameObject top, string kind) => kind == "Prefab"
            ? DescribePrefab(top, null, null)
            : Tree("Scene", top.name, null, Children(top), new ReferenceNames(top), null);

        /// <summary>
        /// A prefab's root object and everything under it. <paramref name="objects"/> receives the object
        /// each element describes; <paramref name="preferredIds"/> are ids its file already uses (References).
        /// </summary>
        public static XElement DescribePrefab(GameObject root, IReadOnlyDictionary<GameObject, string> preferredIds, Dictionary<XElement, GameObject> objects)
        {
            var all = root.GetComponentsInChildren<Transform>(true).Select(t => t.gameObject).ToList();
            return Tree("Prefab", root.name, root, Children(root), new ReferenceNames(all, go => go.transform.IsChildOf(root.transform), preferredIds), objects);
        }

        /// <summary>A loaded scene: its root objects, in hierarchy order. Parameters as for <see cref="DescribePrefab"/>.</summary>
        public static XElement Describe(Scene scene, IReadOnlyDictionary<GameObject, string> preferredIds, Dictionary<XElement, GameObject> objects)
        {
            var roots = scene.GetRootGameObjects().Where(Saved).ToList();
            var all = roots.SelectMany(r => r.GetComponentsInChildren<Transform>(true)).Select(t => t.gameObject).ToList();
            return Tree("Scene", scene.name, null, roots, new ReferenceNames(all, go => go.scene == scene, preferredIds), objects);
        }

        static IEnumerable<GameObject> Children(GameObject go) =>
            Enumerable.Range(0, go.transform.childCount).Select(i => go.transform.GetChild(i).gameObject).Where(Saved);

        static bool Saved(GameObject go) => (go.hideFlags & HideFlags.DontSave) == 0;

        /// <summary>What one export carries: the context, each object's element, and the elements a placed prefab's own children got.</summary>
        sealed class Export
        {
            public ExportContext Ctx;
            public readonly Dictionary<GameObject, XElement> Elements = new();
            public readonly List<(GameObject go, XElement e)> PrefabChildren = new();
        }

        static XElement Tree(string kind, string name, GameObject prefabRoot, IEnumerable<GameObject> children, ReferenceNames names, Dictionary<XElement, GameObject> objects)
        {
            var x = new Export { Ctx = new ExportContext(names) };
            var root = new XElement(kind);
            if (prefabRoot != null)
            {
                x.Elements[prefabRoot] = root;
                if (PrefabInstances.IsRoot(prefabRoot)) root.Set(PrefabInstances.Base, PrefabInstances.AssetPath(prefabRoot));   // a variant
                root.Set("name", prefabRoot.name);
                ObjectReader.Read(prefabRoot, root, x.Ctx, PrefabInstances.Source(prefabRoot));
                root.Add(PrefabInstances.Removed(prefabRoot, kind));
                Components(prefabRoot, root, x);
            }
            else root.Set("name", name);
            foreach (var child in children) root.Add(Element(child, x));

            // Objects that needed an id to be referenced get it, after their name.
            foreach (var kv in x.Ctx.Names.Ids)
                if (x.Elements.TryGetValue(kv.Key, out var e))
                    Markup.InsertAttribute(e, e.Attributes().TakeWhile(a => a.Name != "name").Count() + 1, "id", kv.Value);

            // A placed prefab's child with nothing overridden isn't written: children first.
            for (var i = x.PrefabChildren.Count - 1; i >= 0; i--)
            {
                var (go, e) = x.PrefabChildren[i];
                if (e.HasElements || e.Attributes().Any(a => a.Name != "name")) continue;
                e.Remove();
                x.Elements.Remove(go);
            }
            if (objects != null)
                foreach (var kv in x.Elements) objects[kv.Value] = kv.Key;
            return root;
        }

        static XElement Element(GameObject go, Export x)
        {
            var source = PrefabInstances.Source(go);                      // the prefab's object, when this one came with a placed prefab
            var placed = source != null && PrefabInstances.IsRoot(go);
            var e = new XElement(placed ? PrefabInstances.Element : "GameObject");
            if (placed) e.Set("prefab", PrefabInstances.AssetPath(go));
            else if (source != null) x.PrefabChildren.Add((go, e));
            x.Elements[go] = e;
            e.Set("name", go.name);
            ObjectReader.Read(go, e, x.Ctx, source);
            e.Add(PrefabInstances.Removed(go, e.Tag()));
            Components(go, e, x);
            foreach (var child in Children(go)) e.Add(Element(child, x));
            return e;
        }

        static void Components(GameObject go, XElement e, Export x)
        {
            var read = new List<(Type type, XElement e, bool write)>();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;                                       // a missing script
                var type = c.GetType();
                var ce = new XElement(FieldTypes.TagOf(type, typeof(Component)));
                var source = PrefabInstances.Source(c);
                ObjectReader.Read(c, ce, x.Ctx, source);
                // A plain Transform holding nothing, and the prefab's component with nothing overridden,
                // aren't written — unless a later one of its type is, so that one is still the n-th of
                // its type in the text, as on the object.
                var write = source == null && type != typeof(Transform) || ce.HasAttributes || ce.HasElements;
                if (write)
                    for (var i = 0; i < read.Count; i++)
                        if (read[i].type == type) read[i] = (type, read[i].e, true);
                read.Add((type, ce, write));
            }
            e.Add(read.Where(r => r.write).Select(r => r.e));
        }
    }
}
