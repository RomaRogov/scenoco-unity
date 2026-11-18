using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Assets layer: a prefab placed in a scene or inside another prefab, and a prefab variant.
    //
    //   <PrefabInstance prefab="Assets/Prefabs/Enemy.prefab" name="Enemy">
    //     <PrefabInstance.removedComponents>          what of the prefab this placement doesn't have
    //       <Rigidbody2D />
    //     </PrefabInstance.removedComponents>
    //     <PrefabInstance.removedGameObjects>
    //       <GameObject name="Shield" />
    //     </PrefabInstance.removedGameObjects>
    //     <Transform localPosition="4 0 0" />         an override on the root's component
    //     <Health max="50" />
    //     <GameObject name="Muzzle"> <Transform localPosition="0 1 0" /> </GameObject>
    //                                                 an override on a child the prefab has
    //     <GameObject name="Flag"> … </GameObject>         a child the prefab doesn't have: added
    //   </PrefabInstance>
    //
    //   <Prefab base="Assets/Prefabs/Enemy.prefab" name="Boss"> … </Prefab>
    //                                                 a variant: a prefab whose root is a placed prefab,
    //                                                 its base; what it writes are overrides, the same way
    //
    // A placement gives only its differences: what's written is an override, what's left out
    // keeps the prefab's value — Unity's own prefab-override rule. So the baseline a value is
    // compared with (when reading) or goes back to (when it's removed from the text) is the
    // prefab's object, where an ordinary object's is a fresh one (Defaults).
    //
    // removedComponents and removedGameObjects are the names Unity stores these overrides under
    // (m_RemovedComponents, m_RemovedGameObjects), written on whichever object of the placement they
    // belong to (<GameObject.removedComponents> on a child). A listed component is the n-th of its
    // type on the prefab's object; a listed GameObject is the prefab's child of that name.
    //
    // This class is the one place that asks Unity what belongs to a prefab.
    // ------------------------------------------------------------------------------------------

    public static class PrefabInstances
    {
        public const string Element = "PrefabInstance";

        /// <summary>The attribute naming the prefab a variant is based on.</summary>
        public const string Base = "base";

        public const string RemovedComponents = "removedComponents";
        public const string RemovedGameObjects = "removedGameObjects";

        /// <summary>The prefab's object this one comes from; null for an ordinary or an added object or component.</summary>
        public static T Source<T>(T o) where T : Object =>
            o != null && PrefabUtility.IsPartOfPrefabInstance(o) ? PrefabUtility.GetCorrespondingObjectFromSource(o) : null;

        /// <summary>The object is the root of a placed prefab (not of one nested inside another placement), or of a variant's base.</summary>
        public static bool IsRoot(GameObject go) => PrefabUtility.IsOutermostPrefabInstanceRoot(go);

        /// <summary>The object came with a placed prefab, below its root: it can be overridden or removed, not moved.</summary>
        public static bool IsChild(GameObject go) => Source(go) != null && !IsRoot(go);

        public static string AssetPath(GameObject root) => PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);

        /// <summary>The prefab an attribute of an element names: prefab="…" on a placement, base="…" on a variant.</summary>
        public static GameObject Asset(XElement e, string attribute)
        {
            var path = e.Attr(attribute) ?? throw new ScenocoException(e, $"<{e.Name}> needs {attribute}=\"Assets/….prefab\"");
            return AssetDatabase.LoadAssetAtPath<GameObject>(path) ?? throw new ScenocoException(e, $"no prefab at {path}");
        }

        /// <summary>Place the prefab an element names into a scene.</summary>
        public static GameObject Create(XElement e, Scene scene) => Place(Asset(e, "prefab"), scene);

        public static GameObject Place(GameObject asset, Scene scene) => (GameObject)PrefabUtility.InstantiatePrefab(asset, scene);

        /// <summary>The child a placed prefab already has under <paramref name="parent"/> with this name, if exactly one.</summary>
        public static GameObject ChildNamed(GameObject parent, string name, XElement at)
        {
            if (Source(parent) == null) return null;
            var found = Children(parent).Where(c => c.name == name && IsChild(c)).ToList();
            if (found.Count > 1) throw new ScenocoException(at, $"the prefab has {found.Count} children named \"{name}\" here: an override can't tell them apart");
            return found.FirstOrDefault();
        }

        /// <summary>Drop every override on an object or component: it shows the prefab's values again.</summary>
        public static void Revert(Object o, bool undo) => PrefabUtility.RevertObjectOverride(o, Mode(undo));

        // --- variants -------------------------------------------------------------------------------

        /// <summary>
        /// Follow a prefab root's element losing its base: unpacked, as Unity's Unpack does, it's a
        /// prefab of its own. A base can't be given or swapped in place — Unity doesn't, since every
        /// placement of the variant would lose its overrides.
        /// </summary>
        public static void SetBase(GameObject root, XElement e, bool undo)
        {
            if (e.Attr(Base) == null)
            {
                if (IsRoot(root)) PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.OutermostRoot, Mode(undo));
                return;
            }
            throw new ScenocoException(e, IsRoot(root)
                ? $"a variant's {Base} can't be changed (Unity would drop the overrides of its placements): make a new variant, or remove {Base}=\"…\" to unpack this one"
                : $"a prefab of its own can't become a variant in place: make a new .uprefab with {Base}=\"…\"");
        }

        // --- what a placement doesn't have of its prefab ------------------------------------------------

        /// <summary>The removedComponents and removedGameObjects of an object of a placement, as property elements of <paramref name="owner"/>.</summary>
        public static IEnumerable<XElement> Removed(GameObject go, string owner)
        {
            if (Source(go) == null) yield break;
            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            var components = PrefabUtility.GetRemovedComponents(root)
                .Where(r => r.containingInstanceGameObject == go).Select(r => r.assetComponent)
                .OrderBy(c => Array.IndexOf(c.gameObject.GetComponents<Component>(), c)).ToList();
            if (components.Count > 0)
                yield return new XElement($"{owner}.{RemovedComponents}", components.Select(c => new XElement(FieldTypes.TagOf(c.GetType(), typeof(Component)))));
            var children = PrefabUtility.GetRemovedGameObjects(root)
                .Where(r => r.parentOfRemovedGameObjectInInstance == go).Select(r => r.assetGameObject)
                .OrderBy(c => c.transform.GetSiblingIndex()).ToList();
            if (children.Count > 0)
                yield return new XElement($"{owner}.{RemovedGameObjects}", children.Select(c => new XElement("GameObject").Set("name", c.name)));
        }

        /// <summary>Bring back what the prefab has and <paramref name="list"/> (null: none) no longer names as removed.</summary>
        public static void Restore(GameObject go, string field, XElement list, bool undo)
        {
            var listed = Listed(go, field, list);
            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            if (root == null) return;
            if (field == RemovedComponents)
                foreach (var r in PrefabUtility.GetRemovedComponents(root).Where(r => r.containingInstanceGameObject == go && !listed.Contains(r.assetComponent)))
                    r.Revert(Mode(undo));
            else
                foreach (var r in PrefabUtility.GetRemovedGameObjects(root).Where(r => r.parentOfRemovedGameObjectInInstance == go && !listed.Contains(r.assetGameObject)))
                    r.Revert(Mode(undo));
        }

        /// <summary>Remove from the placement what <paramref name="list"/> names and it still has.</summary>
        public static void Remove(GameObject go, string field, XElement list, bool undo)
        {
            foreach (var asset in Listed(go, field, list))
            {
                Object instance = field == RemovedComponents
                    ? go.GetComponents<Component>().FirstOrDefault(c => c != null && Source(c) == asset)
                    : Children(go).FirstOrDefault(c => Source(c) == asset);
                if (instance == null) continue;
                if (undo) Undo.DestroyObjectImmediate(instance);
                else Object.DestroyImmediate(instance);
            }
        }

        /// <summary>The prefab's components or children a list names.</summary>
        static List<Object> Listed(GameObject go, string field, XElement list)
        {
            if (field is not (RemovedComponents or RemovedGameObjects))
                throw new ScenocoException(list, $"<{list?.Name}>: an object has no field {field} — an object of a placed prefab can have {RemovedComponents} and {RemovedGameObjects}");
            var result = new List<Object>();
            if (list == null) return result;
            var source = Source(go) ?? throw new ScenocoException(list, $"only an object of a placed prefab or a variant has {field}: \"{go.name}\" doesn't come from a prefab");
            var counts = new Dictionary<Type, int>();
            foreach (var item in list.Elements())
            {
                if (field == RemovedComponents)
                {
                    var type = FieldTypes.Unity(item.Tag(), typeof(Component)) ?? throw new ScenocoException(item, $"no component is named {item.Name}");
                    var nth = counts.TryGetValue(type, out var n) ? n : 0;
                    counts[type] = nth + 1;
                    var of = source.GetComponents(type).Where(c => c.GetType() == type).ToArray();
                    if (nth >= of.Length) throw new ScenocoException(item, $"the prefab's \"{source.name}\" has {of.Length} {type.Name}: there's no {(nth == 0 ? "" : $"{nth + 1}. ")}one to remove");
                    result.Add(of[nth]);
                }
                else
                {
                    var name = item.Name == "GameObject" ? item.Attr("name") : null;
                    if (name == null) throw new ScenocoException(item, $"{RemovedGameObjects} lists <GameObject name=\"…\" />: the prefab's children by name");
                    var found = Children(source).Where(c => c.name == name).ToList();
                    if (found.Count != 1) throw new ScenocoException(item, $"the prefab's \"{source.name}\" has {found.Count} children named \"{name}\"" + (found.Count == 0 ? "" : ": a removal can't tell them apart"));
                    result.Add(found[0]);
                }
            }
            return result;
        }

        static IEnumerable<GameObject> Children(GameObject go) =>
            Enumerable.Range(0, go.transform.childCount).Select(i => go.transform.GetChild(i).gameObject);

        static InteractionMode Mode(bool undo) => undo ? InteractionMode.UserAction : InteractionMode.AutomatedAction;
    }
}
