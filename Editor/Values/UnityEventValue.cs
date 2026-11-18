using System.Xml.Linq;
using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Values layer: a UnityEvent's persistent listeners — what the Inspector's event drawer edits.
    //
    //   <Button.onClick>
    //     <Call target="#Game" method="BreakoutGame.Restart" />              no argument: Void, or the
    //     <Call target="#Door" method="Door.Open" argument="2" />           event's own (dynamic) one
    //   </Button.onClick>
    //
    // Written straight into m_PersistentCalls.m_Calls through SerializedProperty — the drawer's data —
    // not with UnityEventTools, which changes the object behind the SerializedObject's back.
    // ------------------------------------------------------------------------------------------

    public static class UnityEventValue
    {
        // PersistentListenerMode and UnityEventCallState, as Unity stores them.
        const int EventDefined = 0, Void = 1, ObjectArg = 2, IntArg = 3, FloatArg = 4, StringArg = 5, BoolArg = 6;
        const int RuntimeOnly = 2;
        static readonly string[] States = { "Off", "EditorAndRuntime", "RuntimeOnly" };
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static void Write(SerializedProperty p, XElement prop, ImportContext ctx)
        {
            var calls = p.FindPropertyRelative("m_PersistentCalls.m_Calls");
            var eventArgs = EventArguments(FieldTypes.Of(p));
            var listeners = prop.Elements().ToList();
            calls.arraySize = listeners.Count;
            for (var i = 0; i < listeners.Count; i++)
            {
                var e = listeners[i];
                if (e.Name != "Call") throw new ScenocoException(e, $"an event's listeners are <Call target=\"#Name\" method=\"Component.Method\" />, not <{e.Name}>");
                var methodText = e.Attr("method") ?? throw new ScenocoException(e, "a Call needs method=\"Component.Method\"");
                var dot = methodText.LastIndexOf('.');
                if (dot <= 0) throw new ScenocoException(e, $"method=\"{methodText}\": write it Component.Method");
                var typeName = methodText.Substring(0, dot);
                var methodName = methodText.Substring(dot + 1);
                var targetType = typeName == "GameObject" ? typeof(GameObject) : FieldTypes.Unity(typeName, typeof(Component))
                                 ?? throw new ScenocoException(e, $"no component is named {typeName}");
                var target = ctx.References.Resolve(e.Attr("target") ?? throw new ScenocoException(e, "a Call needs target=\"#Name\""), targetType, e);
                var argument = e.Attr("argument");

                var candidates = targetType.GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(m => m.Name == methodName && m.GetParameters().Length <= 1).ToList();
                if (candidates.Count == 0) throw new ScenocoException(e, $"{typeName} has no public method {methodName} with at most one parameter");
                MethodInfo method;
                int mode;
                if (argument == null)
                {
                    method = candidates.FirstOrDefault(m => m.GetParameters().Select(x => x.ParameterType).SequenceEqual(eventArgs));
                    mode = EventDefined;
                    if (method == null) { method = candidates.FirstOrDefault(m => m.GetParameters().Length == 0); mode = Void; }
                    if (method == null) throw new ScenocoException(e, $"{methodText} takes a parameter: give it argument=\"…\"");
                }
                else
                {
                    method = candidates.FirstOrDefault(m => m.GetParameters().Length == 1 && ModeFor(m.GetParameters()[0].ParameterType) > 0)
                             ?? throw new ScenocoException(e, $"{methodText} has no overload taking one int, float, string, bool or object");
                    mode = ModeFor(method.GetParameters()[0].ParameterType);
                }

                var call = calls.GetArrayElementAtIndex(i);
                call.FindPropertyRelative("m_Target").objectReferenceValue = target;
                call.FindPropertyRelative("m_TargetAssemblyTypeName").stringValue = $"{targetType.FullName}, {targetType.Assembly.GetName().Name}";
                call.FindPropertyRelative("m_MethodName").stringValue = method.Name;
                call.FindPropertyRelative("m_Mode").enumValueIndex = mode;
                call.FindPropertyRelative("m_CallState").enumValueIndex = e.Attr("state") is { } s ? Array.IndexOf(States, s) is var k && k >= 0 ? k : throw new ScenocoException(e, $"state is {string.Join(", ", States)}") : RuntimeOnly;
                var args = call.FindPropertyRelative("m_Arguments");
                Clear(args);
                if (argument == null) continue;
                var paramType = method.GetParameters()[0].ParameterType;
                switch (mode)
                {
                    case IntArg: args.FindPropertyRelative("m_IntArgument").intValue = int.Parse(argument, Inv); break;
                    case FloatArg: args.FindPropertyRelative("m_FloatArgument").floatValue = ValueText.Float(argument); break;
                    case StringArg: args.FindPropertyRelative("m_StringArgument").stringValue = argument; break;
                    case BoolArg: args.FindPropertyRelative("m_BoolArgument").boolValue = bool.Parse(argument); break;
                    case ObjectArg:
                        args.FindPropertyRelative("m_ObjectArgument").objectReferenceValue = ctx.References.Resolve(argument, paramType, e);
                        args.FindPropertyRelative("m_ObjectArgumentAssemblyTypeName").stringValue = $"{paramType.FullName}, {paramType.Assembly.GetName().Name}";
                        break;
                }
            }
        }

        public static XElement Read(SerializedProperty p, XElement prop, ExportContext ctx)
        {
            var calls = p.FindPropertyRelative("m_PersistentCalls.m_Calls");
            for (var i = 0; i < calls.arraySize; i++)
            {
                var call = calls.GetArrayElementAtIndex(i);
                var typeName = call.FindPropertyRelative("m_TargetAssemblyTypeName").stringValue;
                var type = Type.GetType(typeName, false);
                var e = new XElement("Call")
                    .Set("target", ctx.Names.Name(call.FindPropertyRelative("m_Target").objectReferenceValue) ?? "null")
                    .Set("method", $"{type?.Name ?? typeName.Split(',')[0].Split('.').Last()}.{call.FindPropertyRelative("m_MethodName").stringValue}");
                var args = call.FindPropertyRelative("m_Arguments");
                switch (call.FindPropertyRelative("m_Mode").enumValueIndex)
                {
                    case IntArg: e.Set("argument", args.FindPropertyRelative("m_IntArgument").intValue.ToString(Inv)); break;
                    case FloatArg: e.Set("argument", ValueText.F(args.FindPropertyRelative("m_FloatArgument").floatValue)); break;
                    case StringArg: e.Set("argument", args.FindPropertyRelative("m_StringArgument").stringValue); break;
                    case BoolArg: e.Set("argument", args.FindPropertyRelative("m_BoolArgument").boolValue ? "true" : "false"); break;
                    case ObjectArg: e.Set("argument", ctx.Names.Name(args.FindPropertyRelative("m_ObjectArgument").objectReferenceValue) ?? "null"); break;
                }
                var state = call.FindPropertyRelative("m_CallState").enumValueIndex;
                if (state != RuntimeOnly && state >= 0 && state < States.Length) e.Set("state", States[state]);
                prop.Add(e);
            }
            return prop;
        }

        static int ModeFor(Type t) =>
            t == typeof(int) ? IntArg : t == typeof(float) ? FloatArg : t == typeof(string) ? StringArg : t == typeof(bool) ? BoolArg
            : typeof(Object).IsAssignableFrom(t) ? ObjectArg : -1;

        /// <summary>The argument types an event passes to its dynamic listeners: UnityEvent&lt;int&gt; → int.</summary>
        static Type[] EventArguments(Type eventType)
        {
            for (var t = eventType; t != null && t != typeof(UnityEventBase); t = t.BaseType)
                if (t.IsGenericType && t.Namespace == "UnityEngine.Events" && t.Name.StartsWith("UnityEvent`")) return t.GetGenericArguments();
            return Type.EmptyTypes;
        }

        static void Clear(SerializedProperty args)
        {
            args.FindPropertyRelative("m_ObjectArgument").objectReferenceValue = null;
            args.FindPropertyRelative("m_ObjectArgumentAssemblyTypeName").stringValue = "UnityEngine.Object, UnityEngine";
            args.FindPropertyRelative("m_IntArgument").intValue = 0;
            args.FindPropertyRelative("m_FloatArgument").floatValue = 0;
            args.FindPropertyRelative("m_StringArgument").stringValue = "";
            args.FindPropertyRelative("m_BoolArgument").boolValue = false;
        }
    }
}
