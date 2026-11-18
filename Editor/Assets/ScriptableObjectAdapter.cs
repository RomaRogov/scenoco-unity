using System.Xml.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Assets layer: a ScriptableObject asset — one object's serialized fields.
    //
    //   <Asset type="LevelConfig" name="Level1" goal="40" oxygen="90">
    //     <LevelConfig.waves> … </LevelConfig.waves>
    //   </Asset>
    //
    // The same ObjectWriter and ObjectReader as a component: a data asset is a component without a
    // GameObject — a game's by its fields, Unity's by its properties. References inside it can only
    // point at assets (it has no scene objects).
    // ------------------------------------------------------------------------------------------

    public sealed class ScriptableObjectAdapter : IAssetAdapter
    {
        static readonly string[] Own = { "type", "name" };

        public IReadOnlyList<string> Roots { get; } = new[] { "Asset" };

        public bool CanDescribe(Object o) => o is ScriptableObject;

        public Object Build(XElement root, ImportContext ctx)
        {
            var typeName = root.Attr("type") ?? throw new ScenocoException(root, "<Asset> needs type=\"…\": the ScriptableObject class");
            var type = FieldTypes.Unity(typeName, typeof(ScriptableObject))
                       ?? throw new ScenocoException(root, $"no ScriptableObject class is named {typeName}");
            var asset = ScriptableObject.CreateInstance(type);
            asset.name = root.Attr("name") ?? type.Name;
            // Its structured fields are written <Type.field>, as a component's are: read the root as <Type>.
            root.Name = type.Name;
            ObjectWriter.Apply(asset, root, ctx, Own);
            return asset;
        }

        public XElement Describe(Object o) => Fields(o);

        /// <summary>
        /// Any one object's serialized values that differ from a fresh one's, as &lt;Asset type="…"&gt;: a data
        /// asset, and — to read, not to import — a physics material, an importer's settings (in full:
        /// an importer has no fresh state). Materials and clips read better through AssetText.
        /// </summary>
        public static XElement Fields(Object o)
        {
            var type = o.GetType();
            var asType = new XElement(type.Name);
            ObjectReader.Read(o, asType, new ExportContext((GameObject)null));
            var root = new XElement("Asset").Set("type", type.Name).Set("name", o.name);
            root.Add(asType.Attributes());
            root.Add(asType.Elements());
            return root;
        }
    }
}
