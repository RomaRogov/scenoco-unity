using System.Xml.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Values layer: references — the text for "another object" and "an asset".
    //
    //   #Name           the object of that name, when exactly one has it (nearly always)
    //   #Parent/Child   a path from an ancestor whose name is unique
    //   #id             an object given id="…" (for identical siblings)
    //   Assets/a.png    an asset by path; Assets/a.png#Sprite for a sub-asset
    // An ambiguous reference is an error listing the candidates; nothing is ever the "first match".
    // A reference to a component is written as its object: the field's type picks the component.
    // ------------------------------------------------------------------------------------------

    /// <summary>Import: text → object. Knows every object the file declares.</summary>
    public sealed class ReferenceTable
    {
        readonly List<(GameObject go, XElement at)> objects = new();
        readonly Dictionary<string, GameObject> ids = new();

        /// <summary>Make an object referable that no element describes (a placed prefab's untouched child).</summary>
        public void RegisterUnwritten(GameObject go)
        {
            if (objects.All(o => o.go != go)) objects.Add((go, null));
        }

        public void Register(GameObject go, XElement at)
        {
            objects.RemoveAll(o => o.go == go);
            objects.Add((go, at));
            var id = at?.Attr("id");
            if (id == null) return;
            if (ids.ContainsKey(id)) throw new ScenocoException(at, $"two objects have id \"{id}\"");
            ids[id] = go;
        }

        public Object Resolve(string text, Type want, XElement at)
        {
            if (string.IsNullOrEmpty(text) || text == "null") return null;
            if (text.StartsWith("#")) return Component(Find(text.Substring(1), at), want, text, at);
            return Asset(text, want, at);
        }

        GameObject Find(string token, XElement at)
        {
            if (ids.TryGetValue(token, out var byId)) return byId;
            var steps = token.Split('/');
            var first = objects.Where(o => o.go.name == steps[0]).ToList();
            if (first.Count == 0)
                throw new ScenocoException(at, $"no object is named \"{steps[0]}\"");
            if (first.Count > 1)
                throw new ScenocoException(at, $"\"#{token}\" is ambiguous: {first.Count} objects are named \"{steps[0]}\" (lines {string.Join(", ", first.Select(o => o.at?.Line()))}). Write a path from a uniquely named ancestor, #Parent/{steps[0]}, or give the one you mean an id=\"…\" and write #id");
            var go = first[0].go;
            for (var i = 1; i < steps.Length; i++)
            {
                var kids = Enumerable.Range(0, go.transform.childCount).Select(k => go.transform.GetChild(k).gameObject).Where(c => c.name == steps[i]).ToList();
                if (kids.Count != 1)
                    throw new ScenocoException(at, kids.Count == 0
                        ? $"\"{string.Join("/", steps, 0, i)}\" has no child named \"{steps[i]}\""
                        : $"\"{string.Join("/", steps, 0, i)}\" has {kids.Count} children named \"{steps[i]}\": give the one you mean an id=\"…\"");
                go = kids[0];
            }
            return go;
        }

        static Object Component(GameObject go, Type want, string text, XElement at)
        {
            if (want == null || want == typeof(GameObject) || want == typeof(Object)) return go;
            if (want == typeof(Transform)) return go.transform;
            if (!typeof(Component).IsAssignableFrom(want)) throw new ScenocoException(at, $"{text} is an object, but this field takes a {want.Name}");
            var found = go.GetComponents(want);
            if (found.Length == 0) throw new ScenocoException(at, $"{text} has no {want.Name}");
            if (found.Length > 1) throw new ScenocoException(at, $"{text} has {found.Length} {want.Name} components: the reference is ambiguous");
            return found[0];
        }

        static Object Asset(string text, Type want, XElement at)
        {
            var hash = text.IndexOf('#');
            var path = hash > 0 ? text.Substring(0, hash) : text;
            var sub = hash > 0 ? text.Substring(hash + 1) : null;
            want ??= typeof(Object);
            Object found;
            if (sub == null) found = AssetDatabase.LoadAssetAtPath(path, want);
            else found = AssetDatabase.LoadAllAssetsAtPath(path).FirstOrDefault(o => o != null && o.name == sub && want.IsInstanceOfType(o));
            if (found != null) return found;
            var exists = AssetDatabase.LoadMainAssetAtPath(path) != null;
            throw new ScenocoException(at, exists
                ? $"{path} has no {want.Name}{(sub != null ? $" named \"{sub}\"" : "")} — it holds: {string.Join(", ", AssetDatabase.LoadAllAssetsAtPath(path).Where(o => o != null).Select(o => $"{o.name} ({o.GetType().Name})"))}"
                : $"no asset at {path}");
        }
    }

    /// <summary>Export: object → text. Gives each referenced object its shortest unambiguous name.</summary>
    public sealed class ReferenceNames
    {
        readonly Func<GameObject, bool> inside;
        readonly Dictionary<string, int> nameCounts = new();
        readonly Dictionary<GameObject, string> ids = new();
        readonly IReadOnlyDictionary<GameObject, string> preferred;

        /// <summary>The objects of one GameObject tree (a prefab, or a scene built under a container).</summary>
        public ReferenceNames(GameObject root)
            : this(root == null ? Array.Empty<GameObject>() : root.GetComponentsInChildren<Transform>(true).Select(t => t.gameObject),
                   go => root != null && go.transform.IsChildOf(root.transform)) { }

        /// <summary>
        /// Any set of objects, such as every object of a scene. <paramref name="preferredIds"/> are the
        /// ids a file already gives objects: an object that needs an id keeps the one it was written with.
        /// </summary>
        public ReferenceNames(IEnumerable<GameObject> objects, Func<GameObject, bool> inside, IReadOnlyDictionary<GameObject, string> preferredIds = null)
        {
            this.inside = inside;
            preferred = preferredIds;
            foreach (var go in objects)
                nameCounts[go.name] = nameCounts.TryGetValue(go.name, out var n) ? n + 1 : 1;
        }

        /// <summary>Objects that needed an id to be referenced: the adapter writes id="…" on them.</summary>
        public IReadOnlyDictionary<GameObject, string> Ids => ids;

        public string Name(Object o)
        {
            if (o == null) return null;
            var go = o is Component c ? c.gameObject : o as GameObject;
            if (go != null && inside(go)) return "#" + Token(go);
            var path = AssetDatabase.GetAssetPath(o);
            if (string.IsNullOrEmpty(path)) return null;                         // a scene object outside this file: not written
            // Anything but a file's main asset is named inside it: a sprite in a texture, and Unity's built-in
            // resources (Resources/unity_builtin_extra#UISprite), which are neither main nor sub-assets.
            return AssetDatabase.IsMainAsset(o) ? path : $"{path}#{o.name}";
        }

        string Token(GameObject go)
        {
            if (ids.TryGetValue(go, out var id)) return id;
            if (nameCounts[go.name] == 1) return go.name;
            // A path from the nearest ancestor with a unique name, if every step is unique among siblings.
            var steps = new List<string> { go.name };
            for (var t = go.transform; t.parent != null; t = t.parent)
            {
                if (!UniqueAmongSiblings(t)) break;
                var parent = t.parent;
                steps.Insert(0, parent.name);
                if (nameCounts[parent.name] == 1) return string.Join("/", steps);
            }
            // Identical siblings: give it an id — the one the file already gives it, if any.
            if (preferred != null && preferred.TryGetValue(go, out var kept) && !ids.ContainsValue(kept)) return ids[go] = kept;
            var n = 2;
            var candidate = go.name.Replace(' ', '-');
            while (nameCounts.ContainsKey(candidate) || ids.ContainsValue(candidate)) candidate = $"{go.name.Replace(' ', '-')}-{n++}";
            ids[go] = candidate;
            return candidate;
        }

        static bool UniqueAmongSiblings(Transform t)
        {
            if (t.parent == null) return true;
            var count = 0;
            for (var i = 0; i < t.parent.childCount; i++) if (t.parent.GetChild(i).name == t.name) count++;
            return count == 1;
        }
    }
}
