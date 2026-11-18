using System.Xml.Linq;
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Values layer: a structured value as a property element ↔ a SerializedProperty.
    //
    //   <Spawner.waves>                    a list: one element per item, named by the item's type
    //     <Wave title="Rush" delay="2.5">  an item's fields are attributes, its structured fields nest
    //       <Wave.enemies> … </Wave.enemies>
    //   <Spawner.rewards>                  [SerializeReference]: the element names the concrete class
    //     <CoinReward coins="25" />
    //   <Spawner.difficulty>               AnimationCurve: <Key time value [inTangent outTangent …] />
    //   <Spawner.warningColor>             Gradient: <Color time value /> and <Alpha time value />
    //   <Spawner.onWaveStarted>            UnityEvent: <Call target method [argument] /> (UnityEventValue)
    //   <Path.points> <Vector2 value="0 1" />   a list of single values: value="…" on each item
    //
    // A property's value (a C# struct, a list, a curve) is written the same way, through the C#
    // type's own public members: <HingeJoint.motor> <JointMotor targetVelocity="90" force="10" />.
    // A field's value is written by the serialized names inside it, exactly as stored.
    // ------------------------------------------------------------------------------------------

    public static class StructuredValues
    {
        // --- writing ----------------------------------------------------------------------------

        public static void Write(SerializedProperty p, XElement prop, ImportContext ctx)
        {
            var type = FieldTypes.Of(p);
            if (type != null && typeof(UnityEventBase).IsAssignableFrom(type)) { UnityEventValue.Write(p, prop, ctx); return; }
            switch (p.propertyType)
            {
                case SerializedPropertyType.AnimationCurve: p.animationCurveValue = Curve(prop); return;
                case SerializedPropertyType.Gradient: p.gradientValue = Gradient(prop); return;
                case SerializedPropertyType.Generic when p.isArray:
                    var items = prop.Elements().ToList();
                    p.arraySize = items.Count;
                    for (var i = 0; i < items.Count; i++) WriteItem(p.GetArrayElementAtIndex(i), items[i], ctx);
                    return;
                case SerializedPropertyType.Generic:
                case SerializedPropertyType.ManagedReference:
                    if (prop.Elements().Count() != 1) throw new ScenocoException(prop, $"{ValueText.Label(p)} holds one value: write one element inside <{prop.Name}>");
                    WriteItem(p, prop.Elements().First(), ctx);
                    return;
            }
            throw new ScenocoException(prop, $"{ValueText.Label(p)} is a single value: write it as an attribute, not as <{prop.Name}>");
        }

        static void WriteItem(SerializedProperty item, XElement e, ImportContext ctx)
        {
            if (item.propertyType == SerializedPropertyType.ManagedReference)
            {
                var declared = FieldTypes.Of(item);
                var concrete = FieldTypes.Named(e.Tag(), declared)
                               ?? throw new ScenocoException(e, $"no class named {e.Name} fits {ValueText.Label(item)} ({declared?.Name})");
                item.managedReferenceValue = Activator.CreateInstance(concrete);
            }
            if (ValueText.IsText(item))
            {
                var text = e.Attr("value") ?? throw new ScenocoException(e, $"a list of single values: write each as <{e.Name} value=\"…\" />");
                ValueText.Write(item, text, ctx, e, e.Attribute("value"));
                return;
            }
            foreach (var a in e.Attributes())
            {
                var field = Child(item, a.Name.LocalName) ?? throw new ScenocoException((XObject)a ?? e, $"{ItemName(item)} has no field {a.Name} — it has: {string.Join(", ", Children(item).Select(c => ApiName(c, item)))}");
                if (ValueText.IsText(field)) ValueText.Write(field, a.Value, ctx, e, a);
                else throw new ScenocoException((XObject)a ?? e, $"{a.Name} is structured: write it as <{e.Name}.{a.Name}>");
            }
            foreach (var child in e.Elements())
            {
                var name = child.FieldIn(e) ?? throw new ScenocoException(child, $"<{child.Name}> inside <{e.Name}>: a structured field is written <{e.Name}.field>");
                var field = Child(item, name) ?? throw new ScenocoException(child, $"{ItemName(item)} has no field {name}");
                Write(field, child, ctx);
            }
        }

        static AnimationCurve Curve(XElement prop)
        {
            float A(XElement k, string name, float d = 0) => k.Attr(name) is { } s ? ValueText.Float(s) : d;
            var keys = prop.Elements().Select(k => new Keyframe(A(k, "time"), A(k, "value"), A(k, "inTangent"), A(k, "outTangent"), A(k, "inWeight", 1 / 3f), A(k, "outWeight", 1 / 3f))
            {
                weightedMode = k.Attr("weightedMode") is { } w ? (WeightedMode)Enum.Parse(typeof(WeightedMode), w) : WeightedMode.None,
            }).ToArray();
            var curve = new AnimationCurve(keys);
            if (prop.Attr("preWrapMode") is { } pre) curve.preWrapMode = (WrapMode)Enum.Parse(typeof(WrapMode), pre);
            if (prop.Attr("postWrapMode") is { } post) curve.postWrapMode = (WrapMode)Enum.Parse(typeof(WrapMode), post);
            return curve;
        }

        static Gradient Gradient(XElement prop)
        {
            var g = new Gradient();
            if (prop.Attr("mode") is { } mode) g.mode = (GradientMode)Enum.Parse(typeof(GradientMode), mode);
            g.SetKeys(
                prop.Elements().Where(k => k.Name == "Color").Select(k => new GradientColorKey(ValueText.Colour(k.Attr("value")), ValueText.Float(k.Attr("time") ?? "0"))).ToArray(),
                prop.Elements().Where(k => k.Name == "Alpha").Select(k => new GradientAlphaKey(ValueText.Float(k.Attr("value")), ValueText.Float(k.Attr("time") ?? "0"))).ToArray());
            return g;
        }

        // --- reading ----------------------------------------------------------------------------

        /// <param name="include">Only these paths, for a struct that differs in some members (null: all of it).</param>
        public static XElement Read(SerializedProperty p, string elementName, ExportContext ctx, Func<string, bool> include = null)
        {
            var prop = new XElement(elementName);
            var type = FieldTypes.Of(p);
            if (type != null && typeof(UnityEventBase).IsAssignableFrom(type)) return UnityEventValue.Read(p, prop, ctx);
            switch (p.propertyType)
            {
                case SerializedPropertyType.AnimationCurve: return CurveElement(p.animationCurveValue, prop);
                case SerializedPropertyType.Gradient: return GradientElement(p.gradientValue, prop);
                case SerializedPropertyType.Generic when p.isArray:
                    for (var i = 0; i < p.arraySize; i++) prop.Add(ReadItem(p.GetArrayElementAtIndex(i), ctx));
                    return prop;
                default:
                    prop.Add(ReadItem(p, ctx, include));
                    return prop;
            }
        }

        static XElement CurveElement(AnimationCurve curve, XElement prop)
        {
            if (curve.preWrapMode != WrapMode.ClampForever) prop.Set("preWrapMode", curve.preWrapMode.ToString());
            if (curve.postWrapMode != WrapMode.ClampForever) prop.Set("postWrapMode", curve.postWrapMode.ToString());
            foreach (var k in curve.keys)
            {
                var key = new XElement("Key").Set("time", ValueText.F(k.time)).Set("value", ValueText.F(k.value));
                if (k.inTangent != 0 || k.outTangent != 0) key.Set("inTangent", ValueText.F(k.inTangent)).Set("outTangent", ValueText.F(k.outTangent));
                if (k.weightedMode != WeightedMode.None) key.Set("inWeight", ValueText.F(k.inWeight)).Set("outWeight", ValueText.F(k.outWeight)).Set("weightedMode", k.weightedMode.ToString());
                prop.Add(key);
            }
            return prop;
        }

        static XElement GradientElement(Gradient g, XElement prop)
        {
            if (g.mode != GradientMode.Blend) prop.Set("mode", g.mode.ToString());
            foreach (var k in g.colorKeys) prop.Add(new XElement("Color").Set("time", ValueText.F(k.time)).Set("value", ColourText(k.color)));
            foreach (var k in g.alphaKeys) prop.Add(new XElement("Alpha").Set("time", ValueText.F(k.time)).Set("value", ValueText.F(k.alpha)));
            return prop;
        }

        static XElement ReadItem(SerializedProperty item, ExportContext ctx, Func<string, bool> include = null)
        {
            var name = ItemName(item);
            var e = new XElement(name);
            if (ValueText.IsText(item)) return e.Set("value", ValueText.Read(item, ctx));
            foreach (var field in Children(item))
            {
                if (include != null && !include(field.propertyPath)) continue;
                var fieldName = ApiName(field, item);
                if (ValueText.IsText(field)) e.Set(fieldName, ValueText.Read(field, ctx));
                else e.Add(Read(field, $"{name}.{fieldName}", ctx, include));
            }
            return e;
        }

        static string ColourText(Color c) => ValueText.ColourText(c);

        // --- pieces -----------------------------------------------------------------------------

        static string ItemName(SerializedProperty item)
        {
            if (item.propertyType == SerializedPropertyType.ManagedReference)
                return item.managedReferenceValue?.GetType().Name ?? "null";
            return FieldTypes.Of(item)?.Name ?? item.type;
        }

        static System.Collections.Generic.IEnumerable<SerializedProperty> Children(SerializedProperty owner)
        {
            var it = owner.Copy();
            var end = owner.GetEndProperty();
            if (!it.Next(true)) yield break;
            while (!SerializedProperty.EqualContents(it, end))
            {
                yield return it.Copy();
                if (!it.Next(false)) yield break;
            }
        }

        static SerializedProperty Child(SerializedProperty owner, string name) => owner.FindPropertyRelative(name);

        static string ApiName(SerializedProperty field, SerializedProperty owner) => field.name;

        // --- C# values: a property's struct, list, curve or gradient --------------------------------

        static readonly System.Collections.Generic.Dictionary<Type, bool> writable = new();

        /// <summary>Whether a C# type is written as elements: a curve, a gradient, a struct, or a list of what can be written.</summary>
        public static bool Writable(Type t)
        {
            if (writable.TryGetValue(t, out var known)) return known;
            writable[t] = false;                                                      // while deciding: no cycles
            var element = FieldTypes.ElementOf(t);
            var ok = t == typeof(AnimationCurve) || t == typeof(Gradient)
                     || element != null && element != t && ValueText.Writable(element)
                     || t.IsValueType && !t.IsPrimitive && !t.IsEnum && Members(t).Any();
            return writable[t] = ok;
        }

        /// <summary>A struct's public fields and settable properties that can be written.</summary>
        static System.Collections.Generic.IEnumerable<(string name, Type type, Func<object, object> get, Action<object, object> set)> Members(Type t)
        {
            const System.Reflection.BindingFlags Public = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;
            foreach (var f in t.GetFields(Public).OrderBy(f => f.MetadataToken))
                if (!f.IsInitOnly && ValueText.Writable(f.FieldType)) yield return (f.Name, f.FieldType, f.GetValue, f.SetValue);
            foreach (var p in t.GetProperties(Public).OrderBy(p => p.MetadataToken))
                if (p.GetGetMethod() != null && p.GetSetMethod() != null && p.GetIndexParameters().Length == 0 && !p.IsDefined(typeof(ObsoleteAttribute), true) && ValueText.Writable(p.PropertyType))
                    yield return (p.Name, p.PropertyType, p.GetValue, p.SetValue);
        }

        /// <summary>A property element → the value it describes, starting from <paramref name="current"/> (what the property holds now).</summary>
        public static object Write(Type t, object current, XElement prop, ImportContext ctx)
        {
            if (t == typeof(AnimationCurve)) return Curve(prop);
            if (t == typeof(Gradient)) return Gradient(prop);
            var element = FieldTypes.ElementOf(t);
            if (element != null && element != t)
            {
                var items = prop.Elements().Select(e => Item(element, null, e, ctx)).ToList();
                if (t.IsArray)
                {
                    var array = Array.CreateInstance(element, items.Count);
                    for (var i = 0; i < items.Count; i++) array.SetValue(items[i], i);
                    return array;
                }
                var list = (System.Collections.IList)Activator.CreateInstance(t);
                foreach (var item in items) list.Add(item);
                return list;
            }
            if (prop.Elements().Count() != 1) throw new ScenocoException(prop, $"{t.Name} is one value: write one <{t.Name} … /> inside <{prop.Name}>");
            return Item(t, current, prop.Elements().First(), ctx);
        }

        static object Item(Type t, object current, XElement e, ImportContext ctx)
        {
            if (ValueText.IsText(t))
            {
                var text = e.Attr("value") ?? throw new ScenocoException(e, $"a list of single values: write each as <{e.Name} value=\"…\" />");
                return Parse(text, t, ctx, e, e.Attribute("value"));
            }
            if (e.Tag() != t.Name) throw new ScenocoException(e, $"<{e.Name}> where a {t.Name} goes: write <{t.Name} … />");
            if (!t.IsValueType) return Write(t, null, new XElement("item", e), ctx);
            var boxed = current ?? Activator.CreateInstance(t);
            var members = Members(t).ToList();
            foreach (var a in e.Attributes())
            {
                var m = members.FirstOrDefault(x => x.name == a.Name.LocalName);
                if (m.name == null) throw new ScenocoException((XObject)a, $"{t.Name} has no member {a.Name} — it has: {string.Join(", ", members.Select(x => x.name))}");
                if (!ValueText.IsText(m.type)) throw new ScenocoException((XObject)a, $"{a.Name} is structured: write it as <{t.Name}.{a.Name}>");
                m.set(boxed, Parse(a.Value, m.type, ctx, e, a));
            }
            foreach (var child in e.Elements())
            {
                var name = child.FieldIn(e) ?? throw new ScenocoException(child, $"<{child.Name}> inside <{e.Name}>: a structured member is written <{e.Name}.member>");
                var m = members.FirstOrDefault(x => x.name == name);
                if (m.name == null) throw new ScenocoException(child, $"{t.Name} has no member {name}");
                m.set(boxed, Write(m.type, m.get(boxed), child, ctx));
            }
            return boxed;
        }

        static object Parse(string text, Type t, ImportContext ctx, XElement at, XAttribute attr)
        {
            try { return ValueText.Parse(text, t, ctx, at); }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                throw new ScenocoException((XObject)attr ?? at, $"{attr?.Name}: {ex.Message}");
            }
        }

        /// <summary>A property's value → its property element; a struct writes only the members that differ from <paramref name="baseline"/>'s.</summary>
        public static XElement Read(object v, object baseline, Type t, string elementName, ExportContext ctx)
        {
            var prop = new XElement(elementName);
            if (v is AnimationCurve curve) return CurveElement(curve, prop);
            if (v is Gradient g) return GradientElement(g, prop);
            var element = FieldTypes.ElementOf(t);
            if (element != null && element != t)
            {
                foreach (var item in (System.Collections.IEnumerable)v ?? Array.Empty<object>()) prop.Add(ReadItem(item, null, element, ctx));
                return prop;
            }
            prop.Add(ReadItem(v, baseline, t, ctx));
            return prop;
        }

        static XElement ReadItem(object v, object baseline, Type t, ExportContext ctx)
        {
            var e = new XElement(t.Name);
            if (ValueText.IsText(t)) return e.Set("value", ValueText.Format(v, t, ctx));
            if (!t.IsValueType) return Read(v, null, t, t.Name, ctx);
            baseline ??= Activator.CreateInstance(t);
            foreach (var m in Members(t))
            {
                var mine = m.get(v);
                if (global::Scenoco.Members.Same(mine, m.get(baseline))) continue;
                if (ValueText.IsText(m.type)) e.Set(m.name, ValueText.Format(mine, m.type, ctx));
                else e.Add(Read(mine, m.get(baseline), m.type, $"{t.Name}.{m.name}", ctx));
            }
            return e;
        }
    }
}
