using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Objects layer: the names an object's element can write, and what writing one changes.
    //
    //   Unity's own types (UnityEngine, UnityEditor, Unity.* packages): their public C# properties
    //   with a setter — mass, localPosition, sharedMaterial — set through the setter itself, so
    //   whatever else Unity's setter does happens too. A serialized field no property reaches is
    //   written by its serialized name when the Inspector shows it (m_IsActive is the GameObject
    //   header's checkbox); the ones it hides are Unity's own state (bake output, auto-tiling).
    //   A game's scripts and data assets: their serialized fields, by name. Their properties are
    //   game code and are never called.
    //
    // One property goes through the Editor instead of its setter: a Transform's localEulerAngles is
    // the rotation the Inspector shows and sets (TransformUtils), which keeps the angles as written —
    // 450 stays 450 — where the setter keeps only the quaternion. The reader writes it ahead of
    // localRotation, so a rotation reads back in degrees.
    //
    // Nothing else translates one into the other. What a property stores is learned by doing it: take a
    // snapshot of the object's serialized data (a SerializedObject), set the property, take another
    // (Changed). That's how a value that stores nothing (runtime state) is refused, and how the
    // reader tells which property writes a stored field (ObjectReader).
    // ------------------------------------------------------------------------------------------

    public static class Members
    {
        /// <summary>Serialized fields that are Unity's bookkeeping or the Inspector's own state: never written.</summary>
        public static readonly HashSet<string> Internal = new()
        {
            "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "m_GameObject",
            "m_Script", "m_EditorHideFlags", "m_EditorClassIdentifier", "m_Name", "m_Component", "m_Children", "m_Father",
            "m_RootOrder", "m_LocalEulerAnglesHint", "m_ConstrainProportionsScale",
        };

        /// <summary>The hierarchy's fields: a property that changes one (Transform.parent) is structure, written by nesting.</summary>
        public static readonly HashSet<string> Structure = new() { "m_Father", "m_Children", "m_RootOrder", "m_Component", "m_GameObject" };

        /// <summary>Whether a type is Unity's own (its properties are the vocabulary) rather than a game's script.</summary>
        public static bool IsUnitys(Type t)
        {
            var name = t.Assembly.GetName().Name;
            return name.StartsWith("UnityEngine") || name.StartsWith("UnityEditor") || name.StartsWith("Unity.");
        }

        static readonly Dictionary<Type, List<PropertyInfo>> properties = new();

        /// <summary>
        /// A Unity type's writable properties, in declaration order, the type's own before its base types'. Left out: Object's and
        /// Component's own (name, hideFlags, tag), obsolete ones, and X where sharedX exists — X makes a copy
        /// when it's read in the Editor (material, mesh).
        /// </summary>
        public static IReadOnlyList<PropertyInfo> Properties(Type t)
        {
            if (properties.TryGetValue(t, out var list)) return list;
            list = new List<PropertyInfo>();
            if (IsUnitys(t))
            {
                var chain = new List<Type>();
                for (var b = t; b != null && b != typeof(Object) && b != typeof(Component); b = b.BaseType) chain.Add(b);
                foreach (var type in chain)
                {
                    var declared = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                    foreach (var p in declared.OrderBy(p => p.MetadataToken))
                    {
                        if (!p.CanRead || p.GetSetMethod() == null || p.GetGetMethod() == null || p.GetIndexParameters().Length > 0) continue;
                        if (p.IsDefined(typeof(ObsoleteAttribute), true)) continue;
                        if (t.GetProperty("shared" + char.ToUpperInvariant(p.Name[0]) + p.Name.Substring(1), BindingFlags.Instance | BindingFlags.Public) != null) continue;
                        if (!ValueText.Writable(p.PropertyType)) continue;
                        if (list.All(x => x.Name != p.Name)) list.Add(p);
                    }
                }
            }
            return properties[t] = list;
        }

        public static PropertyInfo Property(Type t, string name) => Properties(t).FirstOrDefault(p => p.Name == name);

        /// <summary>Transform.localEulerAngles: read and set as the Inspector's rotation.</summary>
        public static readonly PropertyInfo InspectorRotation = typeof(Transform).GetProperty(nameof(Transform.localEulerAngles));

        /// <summary>A property's value: its getter, or for <see cref="InspectorRotation"/> the Inspector's rotation.</summary>
        public static object GetValue(PropertyInfo p, Object o) =>
            p == InspectorRotation ? TransformUtils.GetInspectorRotation((Transform)o) : p.GetValue(o);

        /// <summary>Set a property: its setter, or for <see cref="InspectorRotation"/> the Inspector's rotation.</summary>
        public static void SetValue(PropertyInfo p, Object o, object value)
        {
            if (p == InspectorRotation) TransformUtils.SetInspectorRotation((Transform)o, (Vector3)value);
            else p.SetValue(o, value);
        }

        /// <summary>A hidden field the Inspector still edits: the active checkbox in the GameObject header.</summary>
        static readonly HashSet<string> Shown = new() { "m_IsActive" };

        /// <summary>
        /// The serialized fields an element writes by name: all of a script's; of Unity's types, the ones
        /// the Inspector shows. Properties write the rest of what Unity stores.
        /// </summary>
        public static HashSet<string> FieldNames(SerializedObject so)
        {
            var names = new HashSet<string>();
            var unitys = IsUnitys(so.targetObject.GetType());
            var it = so.GetIterator();
            for (var enter = true; unitys ? it.NextVisible(enter) : it.Next(enter); enter = false)
                if (!Internal.Contains(it.name)) names.Add(it.name);
            if (unitys)
                foreach (var name in Shown)
                    if (so.FindProperty(name) != null) names.Add(name);
            return names;
        }

        /// <summary>Every top-level stored field but Unity's bookkeeping: what Units compares.</summary>
        public static IEnumerable<SerializedProperty> Fields(SerializedObject so)
        {
            var it = so.GetIterator();
            for (var enter = true; it.Next(enter); enter = false)
                if (!Internal.Contains(it.name)) yield return it.Copy();
        }

        /// <summary>A top-level serialized field an element can write, or null.</summary>
        public static SerializedProperty Field(SerializedObject so, string name) =>
            FieldNames(so).Contains(name) ? so.FindProperty(name) : null;

        /// <summary>Every name an element of this object can write: its properties (Unity's types), then its fields.</summary>
        public static IEnumerable<string> Names(Object o) =>
            Properties(o.GetType()).Select(p => p.Name).Concat(FieldNames(new SerializedObject(o)).Where(n => Property(o.GetType(), n) == null));

        /// <summary>
        /// An object's stored values, one per value: a field, or each member of a field that's a struct
        /// (Light's m_Shadows → m_Shadows.m_Type, m_Shadows.m_Bias …). Lists and single values are one each.
        /// </summary>
        public static List<SerializedProperty> Units(SerializedObject so)
        {
            var units = new List<SerializedProperty>();
            foreach (var p in Fields(so)) Add(p, units);
            return units;
        }

        static void Add(SerializedProperty p, List<SerializedProperty> units)
        {
            if (p.propertyType != SerializedPropertyType.Generic || p.isArray || !p.hasChildren)
            {
                units.Add(p);
                return;
            }
            var it = p.Copy();
            var end = p.GetEndProperty();
            if (!it.Next(true)) return;
            while (!SerializedProperty.EqualContents(it, end))
            {
                Add(it.Copy(), units);
                if (!it.Next(false)) break;
            }
        }

        /// <summary>What differs between two snapshots of one object: its values (Units), and any hierarchy field (Structure) by name.</summary>
        public static List<string> Changed(SerializedObject before, SerializedObject after)
        {
            var changed = new List<string>();
            foreach (var u in Units(after))
            {
                var b = before.FindProperty(u.propertyPath);
                if (b == null || !Equal(u, b)) changed.Add(u.propertyPath);
            }
            foreach (var name in Structure)
                if (after.FindProperty(name) is { } a && before.FindProperty(name) is { } b && !SerializedProperty.DataEquals(a, b)) changed.Add(name);
            return changed;
        }

        /// <summary>
        /// Two stored values are the same: a rotation when it turns the same way within float precision
        /// (the same angles give quaternions a last digit apart by different paths), a number or vector
        /// as its text says it (so -0 is 0), anything else as data.
        /// </summary>
        public static bool Equal(SerializedProperty a, SerializedProperty b)
        {
            if (a.propertyType == SerializedPropertyType.Quaternion && b.propertyType == SerializedPropertyType.Quaternion)
                return Quaternion.Angle(a.quaternionValue, b.quaternionValue) < 1e-3f;
            switch (a.propertyType)
            {
                case SerializedPropertyType.Float: case SerializedPropertyType.Vector2: case SerializedPropertyType.Vector3:
                case SerializedPropertyType.Vector4: case SerializedPropertyType.Color:
                case SerializedPropertyType.Rect: case SerializedPropertyType.Bounds:
                    return a.propertyType == b.propertyType && ValueText.Read(a, null) == ValueText.Read(b, null);
            }
            return SerializedProperty.DataEquals(a, b);
        }

        /// <summary>Set a property with Unity's log quiet; false when the setter throws.</summary>
        public static bool TrySet(PropertyInfo p, Object o, object value)
        {
            var logging = Debug.unityLogger.logEnabled;
            Debug.unityLogger.logEnabled = false;
            try
            {
                SetValue(p, o, value);
                return true;
            }
            catch { return false; }
            finally { Debug.unityLogger.logEnabled = logging; }
        }

        /// <summary>A property's value, or <paramref name="failed"/> when its getter throws. Unity's log is quiet meanwhile.</summary>
        public static object Get(PropertyInfo p, Object o, out bool failed)
        {
            var logging = Debug.unityLogger.logEnabled;
            Debug.unityLogger.logEnabled = false;
            try
            {
                failed = false;
                return GetValue(p, o);
            }
            catch
            {
                failed = true;
                return null;
            }
            finally { Debug.unityLogger.logEnabled = logging; }
        }

        /// <summary>
        /// The serialized fields setting a property to a value changes, on a copy of <paramref name="from"/>
        /// (a fresh object, or a placed prefab's own): the copy after setting it, and what changed. Null when
        /// the setter throws.
        /// </summary>
        public static (SerializedObject after, List<string> changed)? Probe(Type type, Object from, PropertyInfo p, object value)
        {
            var scratch = Defaults.Scratch(type, from);
            if (scratch == null) return null;
            var before = new SerializedObject(scratch);
            if (!TrySet(p, scratch, value)) return null;
            var after = new SerializedObject(scratch);
            return (after, Changed(before, after));
        }

        /// <summary>Two property values are the same: by reference for Unity objects, item by item for lists, else Equals.</summary>
        public static bool Same(object a, object b)
        {
            if (a is Object || b is Object) return (a as Object) == (b as Object);
            if (a == null || b == null) return a == null && b == null;
            if (a is IList la && b is IList lb)
            {
                if (la.Count != lb.Count) return false;
                for (var i = 0; i < la.Count; i++) if (!Same(la[i], lb[i])) return false;
                return true;
            }
            return a.Equals(b);
        }
    }
}
