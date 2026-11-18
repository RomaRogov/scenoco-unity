using System.Xml.Linq;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using Object = UnityEngine.Object;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Objects layer: one element → one Unity object (a GameObject, a component, a ScriptableObject).
    //
    // Each name is what Members says it is: a property, set through its own setter — so what the
    // setter does besides (turn useMotor on, clamp a weight, move the sizeDelta for an offsetMin)
    // happens, as in a script — or a serialized field, set through a SerializedObject. Values
    // apply in the order they're written, like assignments.
    //
    // Around each property, a snapshot of the object's serialized data (Members.Changed) says what
    // it stored. Nothing stored while the property now reads differently: runtime state, refused —
    // it wouldn't survive saving. A value that reads back differently than written (clamped,
    // wrapped, ignored) is reported at its line (ImportContext.Changed and the console).
    // With undo on, each change is recorded first (Undo.RecordObject), as an editor script does.
    // ------------------------------------------------------------------------------------------

    public static class ObjectWriter
    {
        /// <summary>Apply an element's attributes and property elements, skipping the names in <paramref name="skip"/>.</summary>
        public static void Apply(Object target, XElement e, ImportContext ctx, ICollection<string> skip = null)
        {
            var type = target.GetType();
            SerializedObject fields = null;
            var written = new List<(SerializedProperty p, XAttribute a, string text)>();
            void Flush()
            {
                if (fields == null) return;
                if (ctx.RecordUndo) fields.ApplyModifiedProperties();
                else fields.ApplyModifiedPropertiesWithoutUndo();
                fields.Update();
                foreach (var (p, a, text) in written)
                {
                    var now = ValueText.Read(fields.FindProperty(p.propertyPath), null);
                    if (now != text) Report(ctx, a, $"{type.Name}.{a.Name}=\"{a.Value}\" became \"{now}\": Unity changed it when it was applied (OnValidate, or a range)");
                }
                written.Clear();
                fields = null;
            }

            var names = new HashSet<string>();
            foreach (var a in e.Attributes())
            {
                var name = a.Name.LocalName;
                if (skip != null && skip.Contains(name)) continue;
                names.Add(name);
                if (Members.Property(type, name) is { } p)
                {
                    Flush();
                    Set(target, p, Parse(a.Value, p, ctx, e, a), a, ctx, a.Value);
                    continue;
                }
                fields ??= new SerializedObject(target);
                var f = Members.Field(fields, name) ?? throw Unknown(target, name, a);
                ValueText.Write(f, a.Value, ctx, e, a);
                if (f.propertyType != SerializedPropertyType.ObjectReference && !f.isArray) written.Add((f.Copy(), a, ValueText.Read(f, null)));
            }
            foreach (var child in e.Elements())
            {
                var name = child.FieldIn(e) ?? throw new ScenocoException(child, $"<{child.Name}> inside <{e.Name}>: a structured value is written <{e.Name}.name>");
                if (!names.Add(name)) throw new ScenocoException(child, $"{name} is set twice on <{e.Name}>: as an attribute and as <{child.Name}>");
                if (Members.Property(type, name) is { } p)
                {
                    Flush();
                    if (ValueText.IsText(p.PropertyType)) throw new ScenocoException(child, $"{type.Name}.{name} is a single value: write it as an attribute");
                    Set(target, p, StructuredValues.Write(p.PropertyType, Members.Get(p, target, out _), child, ctx), child, ctx, null);
                    continue;
                }
                fields ??= new SerializedObject(target);
                var f = Members.Field(fields, name) ?? throw Unknown(target, name, child);
                StructuredValues.Write(f, child, ctx);
            }
            Flush();
        }

        /// <summary>Set one property through its setter, and check what it stored.</summary>
        public static void Set(Object target, PropertyInfo p, object value, XObject at, ImportContext ctx, string text)
        {
            var type = target.GetType();
            var before = new SerializedObject(target);
            var was = Members.Get(p, target, out _);
            if (ctx.RecordUndo) Undo.RecordObject(target, "Scenoco");
            // An error Unity logs while its setter runs ("Tag: Hero is not defined") is this value's error.
            string error = null;
            void Logged(string message, string stack, UnityEngine.LogType kind)
            {
                if (kind is UnityEngine.LogType.Error or UnityEngine.LogType.Exception or UnityEngine.LogType.Assert) error ??= message;
            }
            UnityEngine.Application.logMessageReceived += Logged;
            try { Members.SetValue(p, target, value); }
            catch (TargetInvocationException ex)
            {
                throw new ScenocoException(at, $"{type.Name}.{p.Name}: {ex.InnerException?.Message}");
            }
            finally { UnityEngine.Application.logMessageReceived -= Logged; }
            if (error != null)
            {
                Members.TrySet(p, target, was);
                throw new ScenocoException(at, $"{type.Name}.{p.Name}=\"{text ?? ValueText.Format(value, p.PropertyType, null)}\": Unity says: {error}");
            }
            var stored = Members.Changed(before, new SerializedObject(target));
            var now = Members.Get(p, target, out _);
            if (stored.Any(Members.Structure.Contains))
            {
                Members.SetValue(p, target, was);
                throw new ScenocoException(at, $"{type.Name}.{p.Name} changes the hierarchy: an object's parent and children are where its element is written");
            }
            if (stored.Count == 0 && !Members.Same(now, was))
            {
                Members.SetValue(p, target, was);
                throw new ScenocoException(at, $"{type.Name}.{p.Name} is runtime state: setting it changes nothing the scene or asset stores, so it wouldn't last. Set it from a script when the game runs");
            }
            if (stored.Count > 0)
            {
                if (PrefabUtility.IsPartOfPrefabInstance(target)) PrefabUtility.RecordPrefabInstancePropertyModifications(target);
                EditorUtility.SetDirty(target);
            }
            if (text != null && value is not Object && ValueText.IsText(p.PropertyType) && !Members.Same(now, value))
                Report(ctx, at, $"{type.Name}.{p.Name}=\"{text}\" became \"{ValueText.Format(now, p.PropertyType, null)}\": Unity's setter changed it");
        }

        /// <summary>
        /// A name the text stopped writing goes back to its baseline: a fresh object's value, or for a
        /// placed prefab the prefab's — its override reverted, for each field the name stores.
        /// </summary>
        public static void Reset(Object target, string name, XObject at, ImportContext ctx)
        {
            var type = target.GetType();
            var source = PrefabInstances.Source(target);
            var from = source != null ? source : Defaults.Fresh(type);
            if (from == null) return;
            var mode = ctx.RecordUndo ? InteractionMode.UserAction : InteractionMode.AutomatedAction;
            var so = new SerializedObject(target);
            if (Members.Property(type, name) is { } p)
            {
                if (source == null)
                {
                    var value = Members.Get(p, from, out var failed);
                    if (!failed) Set(target, p, value, at, ctx, null);
                    return;
                }
                if (Members.Probe(type, source, p, Members.Get(p, target, out _)) is { } probe)
                    foreach (var path in probe.changed)
                        if (so.FindProperty(path) is { prefabOverride: true } overridden) PrefabUtility.RevertPropertyOverride(overridden, mode);
                return;
            }
            var f = Members.Field(so, name) ?? throw Unknown(target, name, at);
            if (source != null) PrefabUtility.RevertPropertyOverride(f, mode);
            else if (new SerializedObject(from).FindProperty(f.propertyPath) is { } d)
            {
                so.CopyFromSerializedProperty(d);
                if (ctx.RecordUndo) so.ApplyModifiedProperties();
                else so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static object Parse(string text, PropertyInfo p, ImportContext ctx, XElement e, XAttribute a)
        {
            try { return ValueText.Parse(text, p.PropertyType, ctx, e); }
            catch (System.Exception ex) when (ex is System.FormatException or System.OverflowException or System.ArgumentException)
            {
                throw new ScenocoException(a, $"{p.DeclaringType?.Name}.{p.Name}: {ex.Message}");
            }
        }

        static void Report(ImportContext ctx, XObject at, string what)
        {
            var message = $"{Markup.Where(at)}: {what}";
            ctx.Changed.Add(message);
            UnityEngine.Debug.LogWarning("[Scenoco] " + message);
        }

        /// <summary>A name the object has no property or field for: why, and what it takes.</summary>
        public static ScenocoException Unknown(Object target, string name, XObject at)
        {
            var type = target.GetType();
            var takes = string.Join(", ", Members.Names(target));
            var p = Members.IsUnitys(type) ? type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public) : null;
            var why = p == null ? $"{type.Name} has no {name}"
                : p.GetSetMethod() == null ? $"{type.Name}.{name} is read-only"
                : p.IsDefined(typeof(System.ObsoleteAttribute), true) ? $"{type.Name}.{name} is obsolete"
                : $"{type.Name}.{name} isn't written (its value is a copy, or of a kind text can't hold)";
            return new ScenocoException(at, $"{why} — it takes: {takes}");
        }
    }
}
