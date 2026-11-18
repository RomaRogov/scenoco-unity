using System;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Scenoco
{
    // ------------------------------------------------------------------------------------------
    // Document layer: the text itself, with no Unity knowledge — and no parser or writer of our
    // own. A file is a System.Xml.Linq tree: XElement, XAttribute, XComment.
    //
    //   <Prefab> <Scene> <Asset> …   a root, handled by an asset adapter (Assets/)
    //   <GameObject …>               an object in a GameObject tree
    //   <BoxCollider2D …>            a component (or, inside a property element, a list item)
    //   <Wave.enemies>               a property element: a structured field of its parent
    //
    // Parse keeps what a person wrote — comments, blank lines, indentation, the order of elements
    // and attributes — as nodes of the tree, and every node's line, so an error can point at it.
    // Editing a file in place is editing that tree (Insert, Remove) and printing it back (Print):
    // nodes nobody touched come out as they went in. What the library normalizes on the way:
    // attribute values are double-quoted with one space between attributes, and the lines around
    // the root element (a declaration, leading comments) are one per line.
    // ------------------------------------------------------------------------------------------

    /// <summary>An error in a file, with where it is and, when known, what to write instead.</summary>
    public sealed class ScenocoException : Exception
    {
        public ScenocoException(XObject at, string message) : base($"{Markup.Where(at)}: {message}") { }

        public ScenocoException(string source, int line, string message) : base($"{source ?? "text"}:{line}: {message}") { }
    }

    public static class Markup
    {
        sealed class SourceName { public string Name; }

        sealed class WrittenAt { public XObject Node; }

        // --- text ↔ tree ------------------------------------------------------------------------------

        /// <summary>Parse a file, keeping its comments, whitespace and line numbers.</summary>
        public static XElement Parse(string text, string source = null)
        {
            XDocument doc;
            try
            {
                doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            }
            catch (XmlException e)
            {
                throw new ScenocoException(source, e.LineNumber, e.Message);
            }
            doc.Root.AddAnnotation(new SourceName { Name = source });
            var stray = doc.Root.DescendantNodes().OfType<XText>().FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Value));
            if (stray != null) throw new ScenocoException(stray.Parent, $"<{stray.Parent.Name}> holds text; values are written as attributes or elements");
            return doc.Root;
        }

        /// <summary>A parsed (and possibly edited) tree back as text: untouched nodes as they were written.</summary>
        public static string Print(XElement root)
        {
            var doc = root.Document;
            if (doc == null) return Write(root);
            var sb = new StringBuilder();
            if (doc.Declaration != null) sb.Append(doc.Declaration).Append('\n');
            foreach (var node in doc.Nodes())
                if (node is not XText) sb.Append(node.ToString(SaveOptions.DisableFormatting)).Append('\n');
            return sb.ToString();
        }

        /// <summary>A tree as new text, in the house style: one element per line, two-space indent.</summary>
        public static string Write(XElement root)
        {
            var sb = new StringBuilder();
            var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", NewLineChars = "\n", OmitXmlDeclaration = true };
            using (var writer = XmlWriter.Create(sb, settings)) Plain(root).WriteTo(writer);
            return sb.Append('\n').ToString();
        }

        /// <summary>A copy without layout (whitespace nodes), as a tree built in code is.</summary>
        public static XElement Plain(XElement e)
        {
            var copy = new XElement(e);
            copy.DescendantNodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)).ToList().Remove();
            return copy;
        }

        /// <summary>One line that is equal for two elements exactly when they say the same thing, however they're laid out.</summary>
        public static string Canonical(XElement e)
        {
            var copy = Plain(e);
            copy.DescendantNodes().OfType<XComment>().ToList().Remove();
            return copy.ToString(SaveOptions.DisableFormatting);
        }

        // --- editing a parsed tree in place -----------------------------------------------------------

        /// <summary>
        /// Add <paramref name="child"/> to <paramref name="parent"/>: after one element, or before one, or
        /// (both null) last. In a parsed tree it goes on its own line, indented like its siblings.
        /// </summary>
        public static void Insert(XElement parent, XElement child, XElement after = null, XElement before = null)
        {
            if (parent.Document == null && !parent.Nodes().OfType<XText>().Any())
            {
                if (after != null) after.AddAfterSelf(child);
                else if (before != null) before.AddBeforeSelf(child);
                else parent.Add(child);
                return;
            }
            var own = IndentOf(parent) ?? "";
            var indent = parent.Elements().Select(IndentOf).FirstOrDefault(i => i != null) ?? own + "  ";
            Indent(child, indent);
            var lead = new XText("\n" + indent);
            if (after != null) after.AddAfterSelf(lead, child);
            else if (before != null)
            {
                // Above the comments written over that element: they stay its comments.
                XNode first = before;
                while (first.PreviousNode is XComment || first.PreviousNode is XText t && string.IsNullOrWhiteSpace(t.Value)) first = first.PreviousNode;
                if (first == before) before.AddBeforeSelf(child, lead);
                else first.AddBeforeSelf(lead, child);
            }
            else if (parent.LastNode is XText closing && string.IsNullOrWhiteSpace(closing.Value)) closing.AddBeforeSelf(lead, child);
            else parent.Add(lead, child, new XText("\n" + own));
        }

        /// <summary>Take an element out of its parent, with the whitespace that put it on its own line. Comments stay.</summary>
        public static void Remove(XElement e)
        {
            var parent = e.Parent;
            if (e.PreviousNode is XText lead && string.IsNullOrWhiteSpace(lead.Value)) lead.Remove();
            e.Remove();
            if (parent != null && parent.Nodes().All(n => n is XText)) parent.RemoveNodes();     // nothing left: <A />
        }

        /// <summary>The whitespace an element is indented with, when it starts its own line.</summary>
        static string IndentOf(XElement e)
        {
            if (e.PreviousNode is not XText t || !string.IsNullOrWhiteSpace(t.Value)) return null;
            var newline = t.Value.LastIndexOf('\n');
            return newline < 0 ? null : t.Value.Substring(newline + 1);
        }

        /// <summary>Give an element built in code the layout of the file it goes into.</summary>
        static void Indent(XElement e, string indent)
        {
            if (!e.HasElements || e.Nodes().OfType<XText>().Any()) return;
            foreach (var child in e.Elements().ToList())
            {
                child.AddBeforeSelf(new XText("\n" + indent + "  "));
                Indent(child, indent + "  ");
            }
            e.Add(new XText("\n" + indent));
        }

        // --- what the layers above ask of an element ------------------------------------------------

        /// <summary>The element's name as text.</summary>
        public static string Tag(this XElement e) => e.Name.LocalName;

        /// <summary>An attribute's value, or null when it isn't written.</summary>
        public static string Attr(this XElement e, string name) => (string)e.Attribute(name);

        /// <summary>Set an attribute; returns the element, for building trees in one expression.</summary>
        public static XElement Set(this XElement e, string name, string value)
        {
            e.SetAttributeValue(name, value);
            return e;
        }

        /// <summary>
        /// The field an element sets when it's a property element of <paramref name="parent"/>:
        /// <c>&lt;Wave.enemies&gt;</c> inside <c>&lt;Wave&gt;</c> → <c>enemies</c>. The owner part is the parent's
        /// name, full or short (<c>&lt;Button.onClick&gt;</c> inside <c>&lt;UnityEngine.UI.Button&gt;</c>), so a
        /// component written by its full type name is never mistaken for a property element.
        /// </summary>
        public static string FieldIn(this XElement e, XElement parent)
        {
            string name = e.Tag(), full = parent.Tag();
            var shortName = full.Substring(full.LastIndexOf('.') + 1);
            if (name.StartsWith(full + ".", StringComparison.Ordinal)) return name.Substring(full.Length + 1);
            if (name.StartsWith(shortName + ".", StringComparison.Ordinal)) return name.Substring(shortName.Length + 1);
            return null;
        }

        /// <summary>An element made in code that stands for something written: errors about it point there.</summary>
        public static XElement StandingFor(this XElement made, XObject written)
        {
            if (written != null) made.AddAnnotation(new WrittenAt { Node = written });
            return made;
        }

        /// <summary>The line a node was written on (0 for one made in code).</summary>
        public static int Line(this XObject o) => Written(o) is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;

        /// <summary>"file:line" for messages.</summary>
        public static string Where(XObject o)
        {
            o = Written(o);
            var root = (o as XElement ?? o?.Parent)?.AncestorsAndSelf().LastOrDefault();
            return $"{root?.Annotation<SourceName>()?.Name ?? "text"}:{o.Line()}";
        }

        /// <summary>The written node behind one made in code: what it stands for, or the element it was put on.</summary>
        static XObject Written(XObject o)
        {
            while (o != null)
            {
                if (o.Annotation<WrittenAt>() is { } w) o = w.Node;
                else if (o is XAttribute && !((IXmlLineInfo)o).HasLineInfo() && o.Parent != null) o = o.Parent;
                else break;
            }
            return o;
        }

        /// <summary>Put an attribute at a position among an element's attributes.</summary>
        public static void InsertAttribute(XElement e, int index, string name, string value)
        {
            var all = e.Attributes().ToList();
            all.Insert(Math.Min(index, all.Count), new XAttribute(name, value));
            e.ReplaceAttributes(all);
        }
    }
}
