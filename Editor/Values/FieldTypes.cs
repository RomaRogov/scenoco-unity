using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Values layer: the C# type behind a serialized property — read by reflection, never invoked.
    // SerializedProperty says "Generic" or "Enum"; the value text needs the real type: which enum
    // (to read and write names), which element type a list holds, which class a
    // [SerializeReference] item is, whether a field is a UnityEvent.
    // ------------------------------------------------------------------------------------------

    public static class FieldTypes
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>The declared type of the field a property is (for an array element: the element type).</summary>
        public static Type Of(SerializedProperty p)
        {
            Type t = p.serializedObject.targetObject.GetType();
            var path = p.propertyPath.Replace(".Array.data[", "[");
            var parts = path.Split('.');
            var prefix = "";
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                var name = part.Split('[')[0];
                prefix = prefix.Length == 0 ? name : prefix + "." + name;
                var f = Field(t, name);
                if (f == null) return null;
                t = f.FieldType;
                if (part.Contains('['))
                {
                    t = ElementOf(t);
                    prefix += part.Substring(part.IndexOf('['));
                }
                // Inside a [SerializeReference] value, keep walking through the value's actual class.
                if (i < parts.Length - 1)
                {
                    var here = p.serializedObject.FindProperty(prefix.Replace("[", ".Array.data["));
                    if (here != null && here.propertyType == SerializedPropertyType.ManagedReference && here.managedReferenceValue != null)
                        t = here.managedReferenceValue.GetType();
                }
            }
            return t;
        }

        public static Type ElementOf(Type t) =>
            t == null ? null : t.IsArray ? t.GetElementType() : t.IsGenericType && typeof(IList).IsAssignableFrom(t) ? t.GetGenericArguments()[0] : t;

        public static FieldInfo Field(Type t, string name)
        {
            for (; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, Fields);
                if (f != null) return f;
            }
            return null;
        }

        static readonly Dictionary<string, Type> named = new();

        /// <summary>A class by its short name that fits a base type: what a [SerializeReference] item's tag names.</summary>
        public static Type Named(string name, Type baseType)
        {
            var key = name + "|" + baseType?.FullName;
            if (named.TryGetValue(key, out var t)) return t;
            var candidates = (baseType != null ? TypeCache.GetTypesDerivedFrom(baseType).Append(baseType) : AppDomain.CurrentDomain.GetAssemblies().SelectMany(Types))
                .Where(x => x.Name == name || x.FullName == name).Where(x => !x.IsAbstract).Distinct().ToList();
            return named[key] = candidates.Count == 1 ? candidates[0] : candidates.FirstOrDefault(x => x.FullName == name);
        }

        /// <summary>A component or ScriptableObject class by its short or full name.</summary>
        public static Type Unity(string name, Type baseType)
        {
            var key = "unity|" + name + "|" + baseType.FullName;
            if (named.TryGetValue(key, out var t)) return t;
            var candidates = TypeCache.GetTypesDerivedFrom(baseType).Where(x => (x.Name == name || x.FullName == name) && !x.IsAbstract).ToList();
            // Unity's own type wins a tie with a game type of the same short name only when asked by full name.
            var pick = candidates.FirstOrDefault(x => x.FullName == name)
                       ?? (candidates.Count == 1 ? candidates[0] : candidates.FirstOrDefault(x => x.Namespace == "UnityEngine"));
            return named[key] = pick;
        }

        /// <summary>How a component or ScriptableObject class is written: its short name, or its full name when the short one names another class.</summary>
        public static string TagOf(Type t, Type baseType) => Unity(t.Name, baseType) == t ? t.Name : t.FullName;

        static IEnumerable<Type> Types(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(x => x != null); }
        }
    }
}
