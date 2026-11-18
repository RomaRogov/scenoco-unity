using System.Xml.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Values layer: a single value as attribute text — a C# value of a known type (a property's)
    // or a SerializedProperty (a field's). The text is the value itself, nothing derived:
    //
    //   numbers, true/false, text           3   0.35   true   Opening
    //   vectors, Rect, Bounds                "x y z"   "x y width height"   "cx cy cz sx sy sz" (center, size)
    //   Quaternion                           "x y z w"   (for degrees, a type's own Euler property: localEulerAngles)
    //   Color                                "r g b" / "r g b a" (or #rrggbb[aa], which loses precision)
    //   enums                                by name; flags "Left, Top"
    //   LayerMask                            layer names "Default, Water", Everything, Nothing, or a number
    //   references                           #Name, #Parent/Child, #id, Assets/path[#sub]
    //   a list of references or numbers      space-separated: "#A #B", "1 2 3"
    // Everything else (lists of structs, structs, curves, gradients, events) is structured: see
    // StructuredValues. Reading writes the shortest text that reads back to the same value.
    // ------------------------------------------------------------------------------------------

    public static class ValueText
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static readonly HashSet<Type> Single = new()
        {
            typeof(bool), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(short), typeof(ushort), typeof(byte), typeof(sbyte),
            typeof(float), typeof(double), typeof(string), typeof(char),
            typeof(Vector2), typeof(Vector3), typeof(Vector4), typeof(Vector2Int), typeof(Vector3Int), typeof(Quaternion),
            typeof(Color), typeof(Color32), typeof(Rect), typeof(RectInt), typeof(Bounds), typeof(BoundsInt), typeof(LayerMask),
        };

        /// <summary>Whether a C# type is written as attribute text.</summary>
        public static bool IsText(Type t) =>
            Single.Contains(t) || t.IsEnum || typeof(Object).IsAssignableFrom(t)
            || FieldTypes.ElementOf(t) is { } e && e != t && (typeof(Object).IsAssignableFrom(e) || e.IsPrimitive && e != typeof(char) || e.IsEnum);

        /// <summary>Whether a C# type can be written at all: as text, or structured (StructuredValues).</summary>
        public static bool Writable(Type t) => IsText(t) || StructuredValues.Writable(t);

        /// <summary>Whether this field is written as attribute text (otherwise as elements).</summary>
        public static bool IsText(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: case SerializedPropertyType.Boolean: case SerializedPropertyType.Float:
                case SerializedPropertyType.String: case SerializedPropertyType.Enum: case SerializedPropertyType.Character:
                case SerializedPropertyType.Vector2: case SerializedPropertyType.Vector3: case SerializedPropertyType.Vector4:
                case SerializedPropertyType.Vector2Int: case SerializedPropertyType.Vector3Int:
                case SerializedPropertyType.Quaternion: case SerializedPropertyType.Color: case SerializedPropertyType.Rect:
                case SerializedPropertyType.RectInt: case SerializedPropertyType.Bounds: case SerializedPropertyType.BoundsInt:
                case SerializedPropertyType.LayerMask: case SerializedPropertyType.ObjectReference:
                    return true;
                case SerializedPropertyType.Generic when p.isArray:
                    var element = FieldTypes.ElementOf(FieldTypes.Of(p));
                    if (element != null) return typeof(Object).IsAssignableFrom(element) || element.IsPrimitive && element != typeof(char);
                    return p.arraySize > 0 && p.GetArrayElementAtIndex(0) is var first && (first.propertyType is SerializedPropertyType.ObjectReference or SerializedPropertyType.Integer or SerializedPropertyType.Float);
            }
            return false;
        }

        // --- C# values ------------------------------------------------------------------------------

        /// <summary>Text → a value of type <paramref name="t"/>. A FormatException says what's accepted.</summary>
        public static object Parse(string text, Type t, ImportContext ctx, XElement at)
        {
            if (t == typeof(string)) return text;
            if (t == typeof(bool)) return bool.Parse(text);
            if (t == typeof(float)) return Float(text);
            if (t == typeof(double)) return double.Parse(text, NumberStyles.Float, Inv);
            if (t == typeof(char)) return text.Length == 1 ? text[0] : throw new FormatException("one character");
            if (t.IsEnum) return ParseEnum(t, text);
            if (t.IsPrimitive) return Convert.ChangeType(long.Parse(text, Inv), t, Inv);
            if (t == typeof(Vector2)) { var v = Numbers(text, 2); return new Vector2(v[0], v[1]); }
            if (t == typeof(Vector3)) { var v = Numbers(text, 3); return new Vector3(v[0], v[1], v[2]); }
            if (t == typeof(Vector4)) { var v = Numbers(text, 4); return new Vector4(v[0], v[1], v[2], v[3]); }
            if (t == typeof(Vector2Int)) { var v = Numbers(text, 2); return new Vector2Int((int)v[0], (int)v[1]); }
            if (t == typeof(Vector3Int)) { var v = Numbers(text, 3); return new Vector3Int((int)v[0], (int)v[1], (int)v[2]); }
            if (t == typeof(Quaternion)) return Rotation(text);
            if (t == typeof(Color)) return Colour(text);
            if (t == typeof(Color32)) return (Color32)Colour(text);
            if (t == typeof(Rect)) { var v = Numbers(text, 4); return new Rect(v[0], v[1], v[2], v[3]); }
            if (t == typeof(RectInt)) { var v = Numbers(text, 4); return new RectInt((int)v[0], (int)v[1], (int)v[2], (int)v[3]); }
            if (t == typeof(Bounds)) { var v = Numbers(text, 6); return new Bounds(new Vector3(v[0], v[1], v[2]), new Vector3(v[3], v[4], v[5])); }
            if (t == typeof(BoundsInt)) { var v = Numbers(text, 6); return new BoundsInt((int)v[0], (int)v[1], (int)v[2], (int)v[3], (int)v[4], (int)v[5]); }
            if (t == typeof(LayerMask)) return (LayerMask)Layers(text);
            if (typeof(Object).IsAssignableFrom(t)) return ctx.References.Resolve(text, t, at);
            if (FieldTypes.ElementOf(t) is { } element && element != t)
            {
                var items = Items(text, typeof(Object).IsAssignableFrom(element), element, ctx, at).Select(s => Parse(s, element, ctx, at)).ToList();
                if (t.IsArray)
                {
                    var array = Array.CreateInstance(element, items.Count);
                    for (var i = 0; i < items.Count; i++) array.SetValue(items[i], i);
                    return array;
                }
                var list = (IList)Activator.CreateInstance(t);
                foreach (var item in items) list.Add(item);
                return list;
            }
            throw new FormatException($"a {t.Name} is written as elements, not as text");
        }

        /// <summary>A value of type <paramref name="t"/> → text.</summary>
        public static string Format(object v, Type t, ExportContext ctx)
        {
            switch (v)
            {
                case null: return "null";
                case string s: return s;
                case bool b: return b ? "true" : "false";
                case float f: return F(f);
                case double d: return d.ToString("R", Inv);
                case char c: return c.ToString();
                case Enum e: return e.ToString();
                case Vector2 a: return V(a.x, a.y);
                case Vector3 a: return V(a.x, a.y, a.z);
                case Vector4 a: return V(a.x, a.y, a.z, a.w);
                case Vector2Int a: return $"{a.x} {a.y}";
                case Vector3Int a: return $"{a.x} {a.y} {a.z}";
                case Quaternion q: return V(q.x, q.y, q.z, q.w);
                case Color c: return ColourText(c);
                case Color32 c: return ColourText(c);
                case Rect r: return V(r.x, r.y, r.width, r.height);
                case RectInt r: return $"{r.x} {r.y} {r.width} {r.height}";
                case Bounds b: return V(b.center.x, b.center.y, b.center.z, b.size.x, b.size.y, b.size.z);
                case BoundsInt b: return $"{b.x} {b.y} {b.z} {b.size.x} {b.size.y} {b.size.z}";
                case LayerMask m: return LayerNames(m.value);
                case Object o: return ctx?.Names.Name(o) ?? "null";
                case IList list:
                    var element = FieldTypes.ElementOf(t);
                    var items = list.Cast<object>().Select(x => Format(x, element, ctx)).ToList();
                    return string.Join(items.Any(x => x.Contains(' ')) ? ", " : " ", items);
            }
            return v is IFormattable n ? n.ToString(null, Inv) : v.ToString();
        }

        // --- serialized fields ----------------------------------------------------------------------

        public static void Write(SerializedProperty p, string text, ImportContext ctx, XElement at, XAttribute attr = null)
        {
            try
            {
                var enumType = FieldTypes.Of(p) is { IsEnum: true } et ? et : null;
                switch (p.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        p.longValue = enumType != null ? Convert.ToInt64(ParseEnum(enumType, text)) : long.Parse(text, Inv);
                        return;
                    case SerializedPropertyType.Boolean: p.boolValue = bool.Parse(text); return;
                    case SerializedPropertyType.Float: p.floatValue = Float(text); return;
                    case SerializedPropertyType.String: p.stringValue = text; return;
                    case SerializedPropertyType.Character: p.intValue = text.Length == 1 ? text[0] : throw new FormatException("one character"); return;
                    case SerializedPropertyType.Enum:
                        if (enumType != null) p.intValue = Convert.ToInt32(ParseEnum(enumType, text));
                        else
                        {
                            var i = Array.IndexOf(p.enumNames, text);
                            if (i >= 0) p.enumValueIndex = i;
                            else if (int.TryParse(text, NumberStyles.Integer, Inv, out var number)) p.intValue = number;
                            else throw new FormatException($"\"{text}\" is not one of {string.Join(", ", p.enumNames)}");
                        }
                        return;
                    case SerializedPropertyType.Vector2: p.vector2Value = (Vector2)Parse(text, typeof(Vector2), ctx, at); return;
                    case SerializedPropertyType.Vector3: p.vector3Value = (Vector3)Parse(text, typeof(Vector3), ctx, at); return;
                    case SerializedPropertyType.Vector4: p.vector4Value = (Vector4)Parse(text, typeof(Vector4), ctx, at); return;
                    case SerializedPropertyType.Vector2Int: p.vector2IntValue = (Vector2Int)Parse(text, typeof(Vector2Int), ctx, at); return;
                    case SerializedPropertyType.Vector3Int: p.vector3IntValue = (Vector3Int)Parse(text, typeof(Vector3Int), ctx, at); return;
                    case SerializedPropertyType.Quaternion: p.quaternionValue = Rotation(text); return;
                    case SerializedPropertyType.Color: p.colorValue = Colour(text); return;
                    case SerializedPropertyType.Rect: p.rectValue = (Rect)Parse(text, typeof(Rect), ctx, at); return;
                    case SerializedPropertyType.RectInt: p.rectIntValue = (RectInt)Parse(text, typeof(RectInt), ctx, at); return;
                    case SerializedPropertyType.Bounds: p.boundsValue = (Bounds)Parse(text, typeof(Bounds), ctx, at); return;
                    case SerializedPropertyType.BoundsInt: p.boundsIntValue = (BoundsInt)Parse(text, typeof(BoundsInt), ctx, at); return;
                    case SerializedPropertyType.LayerMask: p.intValue = Layers(text); return;
                    case SerializedPropertyType.ObjectReference:
                        var want = FieldTypes.Of(p) is { } ft && typeof(Object).IsAssignableFrom(ft) ? ft : typeof(Object);
                        var o = ctx.References.Resolve(text, want, at);
                        p.objectReferenceValue = o;
                        // An object for a component field: its component the field takes, as dropping it on the Inspector's field does.
                        if (o is GameObject go && p.objectReferenceValue != o)
                            foreach (var c in go.GetComponents<Component>())
                            {
                                p.objectReferenceValue = c;
                                if (c != null && p.objectReferenceValue == c) return;
                            }
                        if (o != null && p.objectReferenceValue != o)
                            throw new FormatException($"{text} is a {o.GetType().Name}{(o is GameObject ? " with no component" : "")}, which this field doesn't take");
                        return;
                    case SerializedPropertyType.Generic when p.isArray:
                        var elementType = FieldTypes.ElementOf(FieldTypes.Of(p));
                        var references = elementType != null ? typeof(Object).IsAssignableFrom(elementType)
                            : p.arraySize > 0 && p.GetArrayElementAtIndex(0).propertyType == SerializedPropertyType.ObjectReference;
                        var items = Items(text, references, elementType ?? typeof(Object), ctx, at);
                        p.arraySize = items.Length;
                        for (var k = 0; k < items.Length; k++) Write(p.GetArrayElementAtIndex(k), items[k], ctx, at, attr);
                        return;
                }
            }
            catch (ScenocoException) { throw; }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                throw new ScenocoException((XObject)attr ?? at, $"{(attr != null ? $"{p.serializedObject.targetObject.GetType().Name}.{attr.Name}" : Label(p))}: {ex.Message}");
            }
            throw new ScenocoException((XObject)attr ?? at, $"{Label(p)} holds a {p.propertyType}, which is written as elements, not as text");
        }

        public static string Read(SerializedProperty p, ExportContext ctx)
        {
            var enumType = FieldTypes.Of(p) is { IsEnum: true } et ? et : null;
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return enumType != null ? Enum.ToObject(enumType, p.longValue).ToString() : p.longValue.ToString(Inv);
                case SerializedPropertyType.Boolean: return p.boolValue ? "true" : "false";
                case SerializedPropertyType.Float: return F(p.floatValue);
                case SerializedPropertyType.String: return p.stringValue;
                case SerializedPropertyType.Character: return ((char)p.intValue).ToString();
                case SerializedPropertyType.Enum:
                    if (enumType != null) return Enum.ToObject(enumType, p.intValue).ToString();
                    return p.enumValueIndex >= 0 && p.enumValueIndex < p.enumNames.Length ? p.enumNames[p.enumValueIndex] : p.intValue.ToString(Inv);
                case SerializedPropertyType.Vector2: return Format(p.vector2Value, null, ctx);
                case SerializedPropertyType.Vector3: return Format(p.vector3Value, null, ctx);
                case SerializedPropertyType.Vector4: return Format(p.vector4Value, null, ctx);
                case SerializedPropertyType.Vector2Int: return Format(p.vector2IntValue, null, ctx);
                case SerializedPropertyType.Vector3Int: return Format(p.vector3IntValue, null, ctx);
                case SerializedPropertyType.Quaternion: return Format(p.quaternionValue, null, ctx);
                case SerializedPropertyType.Color: return ColourText(p.colorValue);
                case SerializedPropertyType.Rect: return Format(p.rectValue, null, ctx);
                case SerializedPropertyType.RectInt: return Format(p.rectIntValue, null, ctx);
                case SerializedPropertyType.Bounds: return Format(p.boundsValue, null, ctx);
                case SerializedPropertyType.BoundsInt: return Format(p.boundsIntValue, null, ctx);
                case SerializedPropertyType.LayerMask: return LayerNames(p.intValue);
                case SerializedPropertyType.ObjectReference: return ctx.Names.Name(p.objectReferenceValue) ?? "null";
                case SerializedPropertyType.Generic when p.isArray:
                    var items = Enumerable.Range(0, p.arraySize).Select(i => Read(p.GetArrayElementAtIndex(i), ctx)).ToList();
                    return string.Join(items.Any(x => x != null && x.Contains(' ')) ? ", " : " ", items);
            }
            return null;
        }

        // --- pieces -----------------------------------------------------------------------------

        public static string Label(SerializedProperty p) => $"{p.serializedObject.targetObject.GetType().Name}.{p.propertyPath}";

        /// <summary>
        /// A list's items: separated by spaces, or by commas when one of them has a space (an asset path).
        /// One reference with a space ("Library/unity default resources#Arial") is one item.
        /// </summary>
        static string[] Items(string text, bool references, Type element, ImportContext ctx, XElement at)
        {
            if (text.Contains(',')) return text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            if (references && text.Trim().Contains(' ') && OneReference(text.Trim(), element, ctx, at)) return new[] { text.Trim() };
            return text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        }

        static bool OneReference(string text, Type want, ImportContext ctx, XElement at)
        {
            try { return ctx.References.Resolve(text, want, at) != null; }
            catch (ScenocoException) { return false; }
        }

        static object ParseEnum(Type t, string text)
        {
            if (long.TryParse(text, NumberStyles.Integer, Inv, out var n)) return Enum.ToObject(t, n);
            try { return Enum.Parse(t, text); }
            catch (ArgumentException) { throw new FormatException($"\"{text}\" is not one of {string.Join(", ", Enum.GetNames(t))}"); }
        }

        static Quaternion Rotation(string text)
        {
            var v = text.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(Float).ToArray();
            if (v.Length != 4) throw new FormatException($"\"{text}\" is not a Quaternion: write its 4 numbers \"x y z w\" (for degrees, use the Euler property: localEulerAngles, eulerAngles)");
            return new Quaternion(v[0], v[1], v[2], v[3]);
        }

        public static float Float(string text) => float.Parse(text.TrimEnd('f', 'F'), NumberStyles.Float, Inv);

        public static float[] Numbers(string text, int n)
        {
            var v = text.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(Float).ToArray();
            if (v.Length != n) throw new FormatException($"\"{text}\" is not {n} numbers");
            return v;
        }

        public static Color Colour(string text)
        {
            if (text.StartsWith("#")) return ColorUtility.TryParseHtmlString(text, out var c) ? c : throw new FormatException($"\"{text}\" is not a colour");
            var v = text.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(Float).ToArray();
            if (v.Length is < 3 or > 4) throw new FormatException($"\"{text}\" is not a colour: write \"r g b\" or \"r g b a\" (0 to 1)");
            return new Color(v[0], v[1], v[2], v.Length > 3 ? v[3] : 1);
        }

        public static string ColourText(Color c) => c.a == 1 ? V(c.r, c.g, c.b) : V(c.r, c.g, c.b, c.a);

        static int Layers(string text)
        {
            if (int.TryParse(text, NumberStyles.Integer, Inv, out var mask)) return mask;
            if (text == "Everything") return -1;
            if (text == "Nothing") return 0;
            var names = text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            var unknown = names.Where(n => LayerMask.NameToLayer(n) < 0).ToList();
            if (unknown.Count > 0) throw new FormatException($"no layer is named {string.Join(", ", unknown)}");
            return LayerMask.GetMask(names);
        }

        static string LayerNames(int mask)
        {
            if (mask == 0) return "Nothing";
            if (mask == -1) return "Everything";
            var names = Enumerable.Range(0, 32).Where(i => (mask & (1 << i)) != 0).Select(LayerMask.LayerToName).ToList();
            return names.Any(string.IsNullOrEmpty) ? mask.ToString(Inv) : string.Join(", ", names);
        }

        /// <summary>The shortest text that reads back as the same float.</summary>
        public static string F(float f)
        {
            if (f == 0) return "0";
            for (var digits = 1; digits <= 9; digits++)
            {
                var s = f.ToString("G" + digits, Inv);
                if (float.Parse(s, Inv) == f) return s.Contains('E') ? f.ToString("R", Inv) : s;
            }
            return f.ToString("R", Inv);
        }

        static string V(params float[] xs) => string.Join(" ", xs.Select(F));
    }
}
