using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Objects layer: what an object of a type holds when it's freshly made — the baseline the
    // reader compares against. Made the way the writer makes objects (ObjectFactory, so default
    // presets apply), in the build scene, and cached for one operation: everything Files or a twin
    // does in one call (Operation). The outermost one's end destroys them, at a moment Unity allows
    // it — not before a script reload, when Unity refuses to destroy some objects and closing a scene
    // that still holds them never returns (BuildScene).
    // ------------------------------------------------------------------------------------------

    public static class Defaults
    {
        static readonly Dictionary<Type, SerializedObject> cache = new();
        static readonly Dictionary<Type, Object> scratch = new();
        static readonly List<Object> owned = new();

        /// <summary>A fresh instance of a type, or null when one can't be made.</summary>
        public static Object Fresh(Type type) => For(type)?.targetObject;

        /// <summary>
        /// A second instance of a type to try values on, holding <paramref name="from"/>'s serialized values
        /// (a fresh object's, or a placed prefab's own) — Members.Probe sets a property on it to see what it stores.
        /// </summary>
        public static Object Scratch(Type type, Object from)
        {
            if (!scratch.TryGetValue(type, out var o) || o == null)
            {
                o = Make(type);
                scratch[type] = o;
            }
            if (o == null || from == null) return o;
            var to = new SerializedObject(o);
            foreach (var p in Members.Fields(new SerializedObject(from))) to.CopyFromSerializedProperty(p);
            to.ApplyModifiedPropertiesWithoutUndo();
            return o;
        }

        /// <summary>A SerializedObject of a fresh instance, or null when one can't be made.</summary>
        public static SerializedObject For(Type type)
        {
            if (cache.TryGetValue(type, out var so)) return so;
            var fresh = Make(type);
            return cache[type] = fresh != null ? new SerializedObject(fresh) : null;
        }

        static Object Make(Type type)
        {
            if (owned.Count == 0 && depth == 0) EditorApplication.delayCall += Clear;   // outside an operation: at the next idle moment
            Object fresh = null;
            try
            {
                if (type == typeof(GameObject))
                {
                    fresh = BuildScene.NewObject("scenoco-defaults", HideFlags.HideAndDontSave);
                    owned.Add(fresh);
                }
                else if (typeof(Component).IsAssignableFrom(type))
                {
                    var go = BuildScene.NewObject("scenoco-defaults", HideFlags.HideAndDontSave);
                    owned.Add(go);
                    fresh = type == typeof(Transform) ? go.transform
                        : type == typeof(RectTransform) ? go.AddComponent<RectTransform>()
                        : ObjectFactory.AddComponent(go, type);
                }
                else if (typeof(ScriptableObject).IsAssignableFrom(type))
                {
                    fresh = ScriptableObject.CreateInstance(type);
                    fresh.hideFlags = HideFlags.HideAndDontSave;
                    owned.Add(fresh);
                }
                else if (type.GetConstructor(Type.EmptyTypes) != null && !typeof(AssetImporter).IsAssignableFrom(type))
                {
                    // Other assets made with new (AnimationClip, PhysicsMaterial2D, …). An importer can't be: it has no fresh state.
                    fresh = (Object)Activator.CreateInstance(type);
                    fresh.hideFlags = HideFlags.HideAndDontSave;
                    owned.Add(fresh);
                }
            }
            catch { fresh = null; }
            return fresh;
        }

        static int depth;

        /// <summary>One operation: the cache lives until the outermost one ends. <c>using var _ = Defaults.Operation();</c></summary>
        public static IDisposable Operation()
        {
            depth++;
            return new End();
        }

        sealed class End : IDisposable
        {
            bool done;

            public void Dispose()
            {
                if (done) return;
                done = true;
                if (--depth == 0) Clear();
            }
        }

        /// <summary>Objects are still cached: a script reload now would have to destroy them.</summary>
        public static bool Holding => owned.Count > 0;

        public static void Clear()
        {
            foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
            owned.Clear();
            cache.Clear();
            scratch.Clear();
        }
    }
}
