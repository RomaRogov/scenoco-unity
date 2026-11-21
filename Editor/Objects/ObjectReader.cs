using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using UnityEditor;
using Object = UnityEngine.Object;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Objects layer: one Unity object → attributes and property elements.
    //
    // What's written is what the object stores differently from its baseline — a fresh object
    // (Defaults), or for a placed prefab the prefab's own (its overrides) — so a file never lists
    // values nobody set. Each stored value (Members.Units) is written once:
    //   - under a property that stores it (Unity's types). Properties are tried on a scratch copy
    //     of the baseline, one after another as the writer will set them, each with this object's
    //     value: a property is written when it gives this object's stored data and the ones before
    //     it stay right. Tried first: the properties named like their field (localPosition for
    //     m_LocalPosition), and a Transform's localEulerAngles — the Inspector's rotation (Members),
    //     declared ahead of localRotation. Then the rest. Each group in declaration order, a type's
    //     own before its base's (a RectTransform's anchors before the localPosition derived from them).
    //   - else as the field itself, by its serialized name: a script's fields, and Unity's that no
    //     property reaches and the Inspector shows (Members.FieldNames) — a struct's differing
    //     members only. What's left is Unity's own state, never written — as is what Unity
    //     computes (Members.Derived): a RectTransform's driven values, and its localPosition.
    // ------------------------------------------------------------------------------------------

    public static class ObjectReader
    {
        /// <param name="baseline">A placed prefab's own object (only overrides are written), or null for a fresh object's values.</param>
        public static void Read(Object target, XElement into, ExportContext ctx, Object baseline = null)
        {
            var type = target.GetType();
            var placed = baseline != null;
            var from = baseline != null ? baseline : Defaults.Fresh(type);
            var so = new SerializedObject(target);
            if (from == null)
            {
                All(target, so, into, ctx);
                return;
            }
            var their = new SerializedObject(from);
            var uncovered = new List<string>();
            var derived = Members.Derived(target, from);
            foreach (var u in Members.Units(so))
            {
                if (derived.Contains(u.propertyPath)) continue;
                var d = their.FindProperty(u.propertyPath);
                // Placed: what Unity marks overridden and isn't the prefab's value (a root's Transform is always marked).
                if ((!placed || u.prefabOverride) && (d == null || !Members.Equal(u, d))) uncovered.Add(u.propertyPath);
            }
            if (uncovered.Count == 0) return;

            if (Members.IsUnitys(type)) Properties(target, so, from, uncovered, into, ctx);

            var names = Members.FieldNames(so);
            foreach (var field in uncovered.GroupBy(path => path.Split('.')[0]).Where(f => names.Contains(f.Key)))
            {
                var p = so.FindProperty(field.Key);
                var parts = new HashSet<string>(field);
                if (parts.Contains(field.Key) || !ValueText.IsText(p) && p.propertyType != SerializedPropertyType.Generic) Field(p, into, ctx, null);
                else Field(p, into, ctx, path => parts.Contains(path) || parts.Any(x => x.StartsWith(path + ".")));
            }
        }

        static void Properties(Object target, SerializedObject so, Object from, List<string> uncovered, XElement into, ExportContext ctx)
        {
            var type = target.GetType();
            var candidates = Members.Properties(type)
                .Select(p => (p, mine: Members.Get(p, target, out var f1), f1, theirs: Members.Get(p, from, out var f2), f2))
                .Where(c => !c.f1 && !c.f2 && !Members.Same(c.mine, c.theirs))
                .OrderBy(c => c.p == Members.InspectorRotation || uncovered.Contains(StoredName(c.p.Name)) ? 0 : 1)
                .ToList();
            var written = new List<(PropertyInfo p, object value)>();
            Object scratch = null;
            foreach (var c in candidates)
            {
                if (uncovered.Count == 0) break;
                if (scratch == null)
                {
                    // Fresh, with what's been written so far: as the writer will have it.
                    scratch = Defaults.Scratch(type, from);
                    foreach (var (p, value) in written) Members.TrySet(p, scratch, value);
                }
                var before = new SerializedObject(scratch);
                if (!Members.TrySet(c.p, scratch, c.mine)) { scratch = null; continue; }
                var after = new SerializedObject(scratch);
                var changed = Members.Changed(before, after);
                var right = changed.Any(uncovered.Contains) && !changed.Any(Members.Structure.Contains)
                            && changed.All(path => so.FindProperty(path) is { } mine && Members.Equal(mine, after.FindProperty(path)));
                if (!right)
                {
                    scratch = null;                                       // undone by starting again
                    continue;
                }
                written.Add((c.p, c.mine));
                uncovered.RemoveAll(changed.Contains);
                Property(c.p, c.mine, c.theirs, into, ctx);
            }
            if (uncovered.Count > 0) Handles(target, so, from, written, uncovered, into, ctx);
        }

        /// <summary>
        /// What's left, under a handle's members (a ParticleSystem's main.duration): each member that differs is set
        /// on the scratch copy's handle, as the writer will set it, and written when it stores this object's values.
        /// A struct member is then written short where its short form stores the same (startSize="2").
        /// </summary>
        static void Handles(Object target, SerializedObject so, Object from, List<(PropertyInfo p, object value)> written, List<string> uncovered, XElement into, ExportContext ctx)
        {
            var type = target.GetType();
            var kept = new List<(PropertyInfo h, StructuredValues.Member m, object value)>();
            Object scratch = null;
            Object Scratch()
            {
                if (scratch != null) return scratch;
                scratch = Defaults.Scratch(type, from);
                foreach (var (p, value) in written) Members.TrySet(p, scratch, value);
                foreach (var (h, m, value) in kept) SetMember(h, m, scratch, value);
                return scratch;
            }
            foreach (var h in Members.Handles(type))
            {
                if (uncovered.Count == 0) break;
                var mine = Members.Get(h, target, out var failed);
                if (failed) continue;
                var item = new XElement(h.PropertyType.Name);
                foreach (var m in StructuredValues.Members(h.PropertyType))
                {
                    if (uncovered.Count == 0) break;
                    if (!StructuredValues.TryGet(m, mine, out var value)) continue;
                    var s = Scratch();
                    var theirs = Members.Get(h, s, out failed);
                    if (failed || !StructuredValues.TryGet(m, theirs, out var baseline)) continue;
                    // What a handle gives can be the object's own (a module's Gradient): kept apart from what's tried next.
                    value = StructuredValues.Detach(value, m.Type);
                    baseline = StructuredValues.Detach(baseline, m.Type);
                    if (Members.Same(value, baseline)) continue;
                    var before = new SerializedObject(s);
                    List<string> changed = null;
                    // A value that didn't store this object's leaves the copy as it was before, for the next one to try:
                    // a mode ignores what it doesn't use (a gradient, the colour an earlier try set).
                    var spoilt = false;
                    bool Stores(object v)
                    {
                        if (spoilt)
                        {
                            scratch = null;
                            Scratch();
                        }
                        spoilt = true;
                        if (!SetMember(h, m, s, v)) return false;
                        var after = new SerializedObject(s);
                        changed = Members.Changed(before, after);
                        spoilt = !(changed.Any(uncovered.Contains) && !changed.Any(Members.Structure.Contains)
                                   && changed.All(path => so.FindProperty(path) is { } p && Members.Equal(p, after.FindProperty(path))));
                        return !spoilt;
                    }
                    if (!Stores(value))
                    {
                        scratch = null;                                       // undone by starting again
                        continue;
                    }
                    // A shorter value stores the same when everything the whole value reached is still this object's.
                    var reached = changed;
                    bool StoresAll(object v) => Stores(v) && new SerializedObject(s) is var now
                                                && reached.All(path => so.FindProperty(path) is { } p && Members.Equal(p, now.FindProperty(path)));
                    // A struct member is written whole, onto a new one, as the writer builds it.
                    var fresh = m.Type.IsValueType && !ValueText.IsText(m.Type) ? System.Activator.CreateInstance(m.Type) : null;
                    var shortened = StructuredValues.Shorten(value, m.Type, StoresAll, fresh);
                    if (!StoresAll(shortened)) Stores(shortened = value);
                    kept.Add((h, m, shortened));
                    uncovered.RemoveAll(reached.Contains);
                    StructuredValues.AddMember(item, m, shortened, fresh, ctx);
                }
                if (item.HasAttributes || item.HasElements) into.Add(new XElement($"{into.Name}.{h.Name}", item));
            }
        }

        /// <summary>Set one member through an object's handle, with Unity's log quiet; false when it throws.</summary>
        static bool SetMember(PropertyInfo h, StructuredValues.Member m, Object o, object value)
        {
            var logging = UnityEngine.Debug.unityLogger.logEnabled;
            UnityEngine.Debug.unityLogger.logEnabled = false;
            try
            {
                m.Set(Members.GetValue(h, o), value);
                return true;
            }
            catch { return false; }
            finally { UnityEngine.Debug.unityLogger.logEnabled = logging; }
        }

        /// <summary>m_ and the name capitalized: how Unity names the field a property stores, when it's stored as itself.</summary>
        static string StoredName(string property) => "m_" + char.ToUpperInvariant(property[0]) + property.Substring(1);

        static void Property(PropertyInfo p, object value, object baseValue, XElement into, ExportContext ctx)
        {
            if (ValueText.IsText(p.PropertyType)) into.Set(p.Name, ValueText.Format(value, p.PropertyType, ctx));
            else into.Add(StructuredValues.Read(value, baseValue, p.PropertyType, $"{into.Name}.{p.Name}", ctx));
        }

        static void Field(SerializedProperty p, XElement into, ExportContext ctx, System.Func<string, bool> include)
        {
            if (!ValueText.IsText(p)) into.Add(StructuredValues.Read(p, $"{into.Name}.{p.name}", ctx, include));
            else if (ValueText.Read(p, ctx) is { } text) into.Set(p.name, text);
        }

        /// <summary>An object with no fresh state to compare with (an importer): everything it has.</summary>
        static void All(Object target, SerializedObject so, XElement into, ExportContext ctx)
        {
            if (Members.IsUnitys(target.GetType()))
            {
                foreach (var p in Members.Properties(target.GetType()))
                    if (Members.Get(p, target, out var failed) is var value && !failed) Property(p, value, null, into, ctx);
                return;
            }
            foreach (var p in Members.Fields(so)) Field(p, into, ctx, null);
        }
    }
}
