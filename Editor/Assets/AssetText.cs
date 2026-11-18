using System.Linq;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Assets layer, read-only: assets whose serialized data says little to a reader, read through
    // Unity's own API and written under the names Unity uses for them. Changing one is done in C#
    // with the same API (Material.SetColor, AnimationUtility.SetEditorCurve, …).
    //
    //   <Asset type="Material" name="Glow" shader="Universal Render Pipeline/Unlit" _BaseColor="1 0 0 1" />
    //       the shader's properties that differ from a fresh material of that shader; a texture's
    //       tiling and offset as Unity's own {name}_ST; keywords and render queue when set
    //   <Asset type="AnimationClip" name="Bob" length="1" loopTime="true">
    //     <Curve path="" type="Transform" property="m_LocalPosition.y">  AnimationUtility's bindings
    //       <Key time="0" value="0" />                                    tangents when not flat
    //     </Curve>
    //     <Event time="0.5" function="Step" />
    //   </Asset>
    // ------------------------------------------------------------------------------------------

    public static class AssetText
    {
        public static XElement Describe(Object o) => o switch
        {
            Material m => Material(m),
            AnimationClip c => Clip(c),
            _ => null,
        };

        static XElement Root(Object o) => new XElement("Asset").Set("type", o.GetType().Name).Set("name", o.name);

        // --- Material ---------------------------------------------------------------------------

        public static XElement Material(Material m)
        {
            var e = Root(m).Set("shader", m.shader != null ? m.shader.name : "none");
            if (m.shader == null) return e;
            var fresh = new Material(m.shader) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var shader = m.shader;
                for (var i = 0; i < shader.GetPropertyCount(); i++)
                {
                    var name = shader.GetPropertyName(i);
                    switch (shader.GetPropertyType(i))
                    {
                        case ShaderPropertyType.Color: Differ(e, name, C(m.GetColor(name)), C(fresh.GetColor(name))); break;
                        case ShaderPropertyType.Vector: Differ(e, name, V(m.GetVector(name)), V(fresh.GetVector(name))); break;
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range: Differ(e, name, ValueText.F(m.GetFloat(name)), ValueText.F(fresh.GetFloat(name))); break;
                        case ShaderPropertyType.Int: Differ(e, name, m.GetInteger(name).ToString(), fresh.GetInteger(name).ToString()); break;
                        case ShaderPropertyType.Texture:
                            Differ(e, name, Path(m.GetTexture(name)), Path(fresh.GetTexture(name)));
                            Differ(e, name + "_ST", V(ST(m, name)), V(ST(fresh, name)));
                            break;
                    }
                }
                Differ(e, "renderQueue", m.renderQueue.ToString(), fresh.renderQueue.ToString());
                Differ(e, "keywords", string.Join(" ", m.shaderKeywords.OrderBy(k => k)), string.Join(" ", fresh.shaderKeywords.OrderBy(k => k)));
                Differ(e, "enableInstancing", m.enableInstancing ? "true" : "false", fresh.enableInstancing ? "true" : "false");
            }
            finally { Object.DestroyImmediate(fresh); }
            return e;
        }

        static Vector4 ST(Material m, string name)
        {
            var s = m.GetTextureScale(name);
            var o = m.GetTextureOffset(name);
            return new Vector4(s.x, s.y, o.x, o.y);
        }

        // --- AnimationClip ------------------------------------------------------------------------

        public static XElement Clip(AnimationClip clip)
        {
            var e = Root(clip).Set("length", ValueText.F(clip.length));
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            if (settings.loopTime) e.Set("loopTime", "true");
            if (clip.frameRate != 60) e.Set("frameRate", ValueText.F(clip.frameRate));
            if (clip.legacy) e.Set("legacy", "true");
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, b);
                e.Add(Binding(b, curve.keys.Select(k =>
                {
                    var key = new XElement("Key").Set("time", ValueText.F(k.time)).Set("value", ValueText.F(k.value));
                    if (k.inTangent != 0 || k.outTangent != 0) key.Set("inTangent", ValueText.F(k.inTangent)).Set("outTangent", ValueText.F(k.outTangent));
                    return key;
                })));
            }
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                e.Add(Binding(b, AnimationUtility.GetObjectReferenceCurve(clip, b)
                    .Select(k => new XElement("Key").Set("time", ValueText.F(k.time)).Set("value", Path(k.value)))));
            foreach (var ev in AnimationUtility.GetAnimationEvents(clip))
            {
                var x = new XElement("Event").Set("time", ValueText.F(ev.time)).Set("function", ev.functionName);
                if (ev.floatParameter != 0) x.Set("float", ValueText.F(ev.floatParameter));
                if (ev.intParameter != 0) x.Set("int", ev.intParameter.ToString());
                if (!string.IsNullOrEmpty(ev.stringParameter)) x.Set("string", ev.stringParameter);
                if (ev.objectReferenceParameter != null) x.Set("object", Path(ev.objectReferenceParameter));
                e.Add(x);
            }
            return e;
        }

        static XElement Binding(EditorCurveBinding b, System.Collections.Generic.IEnumerable<XElement> keys) =>
            new XElement("Curve", keys).Set("path", b.path).Set("type", b.type.Name).Set("property", b.propertyName);

        // --- values -------------------------------------------------------------------------------

        static void Differ(XElement e, string name, string value, string fresh)
        {
            if (value != fresh) e.Set(name, value);
        }

        static string Path(Object o)
        {
            if (o == null) return "none";
            var path = AssetDatabase.GetAssetPath(o);
            return string.IsNullOrEmpty(path) ? o.name : AssetDatabase.IsMainAsset(o) ? path : $"{path}#{o.name}";
        }

        static string C(Color c) => $"{ValueText.F(c.r)} {ValueText.F(c.g)} {ValueText.F(c.b)} {ValueText.F(c.a)}";

        static string V(Vector4 v) => $"{ValueText.F(v.x)} {ValueText.F(v.y)} {ValueText.F(v.z)} {ValueText.F(v.w)}";
    }
}
