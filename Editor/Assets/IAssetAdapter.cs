using System.Xml.Linq;
using System.Collections.Generic;
using Object = UnityEngine.Object;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Assets layer: one adapter per kind of Unity file.
    //
    // Each kind of file has its own shape for an agent, and its own way into Unity:
    //   GameObjectTreeAdapter    <Prefab>, <Scene>        GameObjects, components, references
    //   ScriptableObjectAdapter  <Asset type="…">         one object's serialized fields
    // Other assets (materials, clips, import settings) have no adapter: they're read with
    // Files.Describe and changed in C# through Unity's API.
    // An adapter owns its root element and the objects it makes; it never reaches into another's.
    // ------------------------------------------------------------------------------------------

    public interface IAssetAdapter
    {
        /// <summary>The root element names this adapter reads.</summary>
        IReadOnlyList<string> Roots { get; }

        /// <summary>Whether this adapter writes text for this object.</summary>
        bool CanDescribe(Object o);

        /// <summary>Text → a new, detached object (not yet saved as an asset or placed in a scene).</summary>
        Object Build(XElement root, ImportContext ctx);

        /// <summary>An object → its text.</summary>
        XElement Describe(Object o);
    }
}
