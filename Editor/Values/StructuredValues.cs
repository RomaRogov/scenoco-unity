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
                return XmlName(item.managedReferenceValue?.GetType().Name ?? "null");
            return XmlName(FieldTypes.Of(item)?.Name ?? item.type);
        }

        /// <summary>
        /// A type's name as an element's: a generic one without its arity (`SplineDataKeyValuePair`1` is
        /// `SplineDataKeyValuePair`), and what XML can't hold in a name (`PPtr&lt;$Object&gt;`) as `_`. An item's
        /// element is read back by its place in the list, so the name only has to be readable.
        /// </summary>
        internal static string XmlName(string name)
        {
            var tick = name.IndexOf('`');
            if (tick > 0) name = name.Substring(0, tick);
            name = System.Text.RegularExpressions.Regex.Replace(name, @"[^\w.-]", "_");
            return name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') ? "_" + name : name;
        }

        static System.Collections.Generic.IEnumerable<SerializedProperty> Children(SerializedProperty owner)
        {
            var it = owner.Copy();
            // Hidden properties count: Next(true) visits them, and an end that skips one ([HideInInspector]
            // Spline.m_MetaData after the last knot) would read it as the owner's own field.
            var end = owner.GetEndProperty(true);
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

        /// <summary>
        /// A member of a struct as the text names it: a public field, a settable property, or a list the type
        /// gives through Get{Names}(T[]), Set{Names}(T[]) and a {name}Count — a ParticleSystem module's bursts,
        /// which a script sets with SetBursts.
        /// </summary>
        public readonly struct Member
        {
            public readonly string Name;
            public readonly Type Type;
            public readonly Func<object, object> Get;
            public readonly Action<object, object> Set;

            public Member(string name, Type type, Func<object, object> get, Action<object, object> set)
            {
                Name = name;
                Type = type;
                Get = get;
                Set = set;
            }
        }

        static readonly System.Collections.Generic.Dictionary<Type, Member[]> members = new();

        /// <summary>A struct's members that can be written, in declaration order: fields, properties, then lists given by methods.</summary>
        public static Member[] Members(Type t)
        {
            if (members.TryGetValue(t, out var known)) return known;
            members[t] = Array.Empty<Member>();                                       // while deciding: no cycles
            return members[t] = Find(t).ToArray();
        }

        static System.Collections.Generic.IEnumerable<Member> Find(Type t)
        {
            const System.Reflection.BindingFlags Public = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;
            foreach (var f in t.GetFields(Public).OrderBy(f => f.MetadataToken))
                if (!f.IsInitOnly && ValueText.Writable(f.FieldType)) yield return new Member(f.Name, f.FieldType, f.GetValue, f.SetValue);
            foreach (var p in t.GetProperties(Public).OrderBy(p => p.MetadataToken))
                if (p.GetGetMethod() != null && p.GetSetMethod() != null && p.GetIndexParameters().Length == 0 && !p.IsDefined(typeof(ObsoleteAttribute), true) && ValueText.Writable(p.PropertyType))
                    yield return new Member(p.Name, p.PropertyType, p.GetValue, p.SetValue);
            foreach (var set in t.GetMethods(Public).OrderBy(m => m.MetadataToken))
            {
                var parameters = set.GetParameters();
                if (!set.Name.StartsWith("Set") || set.Name.Length < 5 || set.ReturnType != typeof(void) || parameters.Length != 1 || !parameters[0].ParameterType.IsArray) continue;
                var list = parameters[0].ParameterType;
                var plural = set.Name.Substring(3);                                   // Bursts
                if (!plural.EndsWith("s") || set.IsDefined(typeof(ObsoleteAttribute), true)) continue;
                var get = t.GetMethod("Get" + plural, Public, null, new[] { list }, null);
                var count = t.GetProperty(Lower(plural.Substring(0, plural.Length - 1)) + "Count", Public);
                if (get == null || count?.GetGetMethod() == null || !ValueText.Writable(list)) continue;
                var element = list.GetElementType();
                yield return new Member(Lower(plural), list,
                    o =>
                    {
                        var items = Array.CreateInstance(element, Convert.ToInt32(count.GetValue(o)));
                        get.Invoke(o, new object[] { items });
                        return items;
                    },
                    (o, v) => set.Invoke(o, new[] { v }));
            }
        }

        static string Lower(string s) => char.ToLowerInvariant(s[0]) + s.Substring(1);

        /// <summary>A struct's member by name: its Member, or null.</summary>
        public static Member? MemberOf(Type t, string name)
        {
            foreach (var m in Members(t)) if (m.Name == name) return m;
            return null;
        }

        // --- short forms: a struct written as one value it converts from (MinMaxCurve from a float) ----

        static readonly System.Collections.Generic.Dictionary<Type, (Type from, System.Reflection.MethodInfo make, System.Reflection.PropertyInfo[] read)[]> shortForms = new();

        /// <summary>
        /// The text values a struct converts from implicitly — a MinMaxCurve from a float, a MinMaxGradient from a
        /// Color, as a script assigns `main.startSize = 2` — each with the struct's properties of that type, which
        /// read one back. Learned from the type's own conversion operators.
        /// </summary>
        static (Type from, System.Reflection.MethodInfo make, System.Reflection.PropertyInfo[] read)[] ShortForms(Type t)
        {
            if (shortForms.TryGetValue(t, out var known)) return known;
            var forms = new System.Collections.Generic.List<(Type, System.Reflection.MethodInfo, System.Reflection.PropertyInfo[])>();
            if (t.IsValueType && !ValueText.IsText(t))
                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                {
                    if (m.Name != "op_Implicit" || m.ReturnType != t || m.GetParameters().Length != 1) continue;
                    var from = m.GetParameters()[0].ParameterType;
                    if (!ValueText.IsText(from) || from == t) continue;
                    var read = t.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                        .Where(p => p.PropertyType == from && p.GetGetMethod() != null && p.GetIndexParameters().Length == 0)
                        .OrderBy(p => p.MetadataToken).ToArray();
                    if (read.Length > 0) forms.Add((from, m, read));
                }
            return shortForms[t] = forms.ToArray();
        }

        /// <summary>Whether a value is exactly what its short form makes, and that short value.</summary>
        public static bool IsShort(object v, Type t, out object value, out Type type)
        {
            foreach (var (from, make, read) in ShortForms(t))
                foreach (var p in read)
                {
                    var s = p.GetValue(v);
                    if (!Equals(make.Invoke(null, new[] { s }), v)) continue;
                    value = s;
                    type = from;
                    return true;
                }
            value = null;
            type = null;
            return false;
        }

        /// <summary>
        /// The value with each part that has a short form replaced by it wherever <paramref name="same"/> says the
        /// replacement stores the same — the item's MinMaxCurve that is a constant becomes that constant. What a
        /// short form can't say (two constants, a curve) is kept whole.
        /// </summary>
        /// <param name="start">What the value is written onto — the baseline the text leaves out, or a new one for a list's
        /// item: a member that stores the same at start's value (one Unity clamps anyway, one a mode leaves unused) is
        /// left at it, and so out of the text.</param>
        public static object Shorten(object v, Type t, Func<object, bool> same, object start = null)
        {
            if (v == null || ValueText.IsText(t) || IsShort(v, t, out _, out _)) return v;
            foreach (var (_, make, read) in ShortForms(t))
                foreach (var p in read)
                {
                    var candidate = make.Invoke(null, new[] { p.GetValue(v) });
                    if (same(candidate)) return candidate;
                }
            var element = FieldTypes.ElementOf(t);
            if (element != null && element != t && v is Array array)
            {
                var copy = (Array)array.Clone();
                for (var i = 0; i < copy.Length; i++)
                {
                    var at = i;
                    copy.SetValue(Shorten(copy.GetValue(i), element, x =>
                    {
                        var tried = (Array)copy.Clone();
                        tried.SetValue(x, at);
                        return same(tried);
                    }, element.IsValueType ? Activator.CreateInstance(element) : null), i);
                }
                return copy;
            }
            if (!t.IsValueType || t.IsPrimitive || t.IsEnum) return v;
            var shortened = Copy(v);
            // A member set to a value its setter refuses (a Burst's repeatInterval of 0) is no shorter.
            bool Same(Member m, object x)
            {
                var tried = Copy(shortened);
                try { m.Set(tried, x); }
                catch (Exception ex) when (ex is ArgumentException or System.Reflection.TargetInvocationException) { return false; }
                return same(tried);
            }
            if (start != null)
                foreach (var m in Members(t))
                    if (TryGet(m, shortened, out var mine) && TryGet(m, start, out var initial) && !global::Scenoco.Members.Same(mine, initial) && Same(m, initial))
                        m.Set(shortened, initial);
            foreach (var m in Members(t))
            {
                if (ValueText.IsText(m.Type) || !TryGet(m, shortened, out var mine)) continue;
                var member = m;
                var shorter = Shorten(mine, m.Type, x => Same(member, x), start != null && TryGet(m, start, out var under) ? under : null);
                if (!ReferenceEquals(shorter, mine)) m.Set(shortened, shorter);
            }
            return shortened;
        }

        /// <summary>
        /// A value read through a handle, detached from the object: a module's getter hands out the system's own
        /// Gradient and AnimationCurve, which change when the object does. Structs and lists copied, those copied anew.
        /// </summary>
        public static object Detach(object v, Type t)
        {
            switch (v)
            {
                case null: return null;
                case Gradient g:
                    var gradient = new Gradient { mode = g.mode, colorSpace = g.colorSpace };
                    gradient.SetKeys(g.colorKeys, g.alphaKeys);
                    return gradient;
                case AnimationCurve c:
                    return new AnimationCurve(c.keys) { preWrapMode = c.preWrapMode, postWrapMode = c.postWrapMode };
                case Array a:
                    var items = (Array)a.Clone();
                    var element = t.GetElementType() ?? typeof(object);
                    for (var i = 0; i < items.Length; i++) items.SetValue(Detach(items.GetValue(i), element), i);
                    return items;
            }
            if (!t.IsValueType || t.IsPrimitive || t.IsEnum || ValueText.IsText(t)) return v;
            var copy = Copy(v);
            foreach (var m in Members(t))
                if (!ValueText.IsText(m.Type) && TryGet(m, copy, out var member))
                    try { m.Set(copy, Detach(member, m.Type)); }
                    catch { /* left as read */ }
            return copy;
        }

        /// <summary>A boxed copy of a struct.</summary>
        static object Copy(object v) => System.Runtime.CompilerServices.RuntimeHelpers.GetObjectValue(v);

        /// <summary>A member's value, or false when its getter throws (a module's member that doesn't apply).</summary>
        public static bool TryGet(Member m, object of, out object value)
        {
            var logging = Debug.unityLogger.logEnabled;
            Debug.unityLogger.logEnabled = false;
            try
            {
                value = m.Get(of);
                return true;
            }
            catch
            {
                value = null;
                return false;
            }
            finally { Debug.unityLogger.logEnabled = logging; }
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
            // Members are set on the boxed value in the order written: on a handle (a ParticleSystem's module) each one sets the object.
            var boxed = current ?? Activator.CreateInstance(t);
            var all = Members(t);
            foreach (var a in e.Attributes())
            {
                var m = all.FirstOrDefault(x => x.Name == a.Name.LocalName);
                if (m.Name == null) throw new ScenocoException((XObject)a, $"{t.Name} has no member {a.Name} — it has: {string.Join(", ", all.Select(x => x.Name))}");
                m.Set(boxed, Short(a.Value, m.Type, ctx, e, a));
            }
            foreach (var child in e.Elements())
            {
                var name = child.FieldIn(e) ?? throw new ScenocoException(child, $"<{child.Name}> inside <{e.Name}>: a structured member is written <{e.Name}.member>");
                var m = all.FirstOrDefault(x => x.Name == name);
                if (m.Name == null) throw new ScenocoException(child, $"{t.Name} has no member {name}");
                // A struct inside one is the whole value: what it doesn't write is a new one's, never what a getter
                // happened to hold (a MinMaxGradient read in Gradient mode carries colours nothing set).
                m.Set(boxed, Write(m.Type, null, child, ctx));
            }
            return boxed;
        }

        /// <summary>An attribute's value for a member: text, or a struct's short form (startSize="2" for a MinMaxCurve).</summary>
        static object Short(string text, Type t, ImportContext ctx, XElement at, XAttribute attr)
        {
            if (ValueText.IsText(t)) return Parse(text, t, ctx, at, attr);
            var forms = ShortForms(t);
            if (forms.Length == 0) throw new ScenocoException((XObject)attr ?? at, $"{attr?.Name} is structured: write it as <{at.Name}.{attr?.Name}>");
            Exception failed = null;
            foreach (var (from, make, _) in forms)
            {
                try { return make.Invoke(null, new[] { ValueText.Parse(text, from, ctx, at) }); }
                catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException) { failed = ex; }
            }
            throw new ScenocoException((XObject)attr ?? at, $"{attr?.Name}: {failed?.Message} — as a {string.Join(" or ", forms.Select(f => f.from.Name))}, or written as <{at.Name}.{attr?.Name}>");
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
            // As the writer builds it: from the baseline (a new one for a list's item), member by member in order,
            // so a member an earlier one already set (a Burst's maxCount, after its count) isn't written again.
            var built = Copy(baseline ?? Activator.CreateInstance(t));
            foreach (var m in Members(t))
            {
                if (!TryGet(m, v, out var mine) || TryGet(m, built, out var theirs) && global::Scenoco.Members.Same(mine, theirs)) continue;
                AddMember(e, m, mine, theirs, ctx);
                try { m.Set(built, mine); }
                catch { /* the item is still written; only what follows may repeat */ }
            }
            return e;
        }

        /// <summary>One member into an item's element: an attribute when it's text or its short form, else a nested element.</summary>
        public static void AddMember(XElement item, Member m, object value, object baseline, ExportContext ctx)
        {
            if (ValueText.IsText(m.Type)) item.Set(m.Name, ValueText.Format(value, m.Type, ctx));
            else if (IsShort(value, m.Type, out var s, out var st)) item.Set(m.Name, ValueText.Format(s, st, ctx));
            else item.Add(Read(value, baseline, m.Type, $"{item.Name}.{m.Name}", ctx));
        }
    }
}
