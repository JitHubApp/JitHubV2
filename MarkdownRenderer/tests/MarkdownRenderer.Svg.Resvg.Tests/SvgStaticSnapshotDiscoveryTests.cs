using System.Collections.Generic;
using System.Threading;
using System.Xml;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgStaticSnapshotDiscoveryTests
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";
    private const string HtmlNamespace = "http://www.w3.org/1999/xhtml";
    private const string ForeignNamespace = "urn:discovery-test";

    [Fact]
    public void SingleTraversalMatchesIndependentLegacyFindersAcrossNamespacesAndSuppressedSubtrees()
    {
        XmlDocument document = Load(
            "<svg xmlns='" + SvgNamespace + "' xmlns:x='" + ForeignNamespace + "' xmlns:h='" + HtmlNamespace + "' id='root' style='root-style'>" +
            "<animate id='outer-animation' style='outer-style'>" +
            "<animate id='nested-animation'/><foreignObject id='outer-foreign'>" +
            "<foreignObject id='nested-foreign'><script id='script-under-nested-foreign'/></foreignObject>" +
            "<script id='script-under-foreign'/><h:script id='xhtml-script'/><style id='style-under-foreign'/>" +
            "<x:foreignObject id='wrong-namespace-foreign'><script xmlns='" + SvgNamespace + "' id='script-under-wrong-foreign'/></x:foreignObject>" +
            "</foreignObject><style id='style-under-animation'/><script id='script-under-animation'/>" +
            "<g xmlns='' id='empty-namespace-group' style='empty-style'>" +
            "<animate id='empty-namespace-animation'/><foreignObject id='empty-namespace-foreign'>" +
            "<script id='script-under-empty-foreign'/></foreignObject><script id='empty-namespace-script'/>" +
            "<style id='empty-namespace-style'/></g></animate>" +
            "<x:animate id='wrong-namespace-animation'><animate xmlns='" + SvgNamespace + "' id='animation-under-wrong-animation'/></x:animate>" +
            "<x:foreignObject id='wrong-namespace-foreign-root'><script xmlns='" + SvgNamespace + "' id='script-under-wrong-foreign-root'/>" +
            "<foreignObject xmlns='" + SvgNamespace + "' id='svg-foreign-under-wrong-foreign'/></x:foreignObject>" +
            "<script id='top-level-script'/><style id='top-level-style'/><x:style id='foreign-style' style='foreign-inline-style'/>" +
            "</svg>");

        AssertMatchesLegacyFinders(document);
        SvgStaticSnapshot.DiscoverElements(
            document,
            CancellationToken.None,
            out List<XmlElement> animations,
            out List<XmlElement> scripts,
            out List<XmlElement> foreignObjects,
            out List<XmlElement> styles,
            out List<XmlElement> inlineStyles);

        Assert.Equal(new[] { "outer-animation", "animation-under-wrong-animation" }, Ids(animations));
        Assert.Equal(
            new[] { "script-under-animation", "empty-namespace-script", "script-under-wrong-foreign-root", "top-level-script" },
            Ids(scripts));
        Assert.Equal(
            new[] { "outer-foreign", "empty-namespace-foreign", "svg-foreign-under-wrong-foreign" },
            Ids(foreignObjects));
        Assert.Equal(
            new[] { "style-under-foreign", "style-under-animation", "empty-namespace-style", "top-level-style" },
            Ids(styles));
        Assert.Equal(
            new[] { "root", "outer-animation", "empty-namespace-group", "foreign-style" },
            Ids(inlineStyles));
    }

    [Fact]
    public void SingleTraversalMatchesLegacyOnWideMixedNamespaceDocument()
    {
        XmlDocument document = CreateDocument();
        XmlElement root = document.DocumentElement!;
        for (int index = 0; index < 8192; index++)
        {
            string name = (index % 7) switch
            {
                0 => "animate",
                1 => "foreignObject",
                2 => "script",
                3 => "style",
                _ => "g",
            };
            string namespaceUri = index % 13 == 0 ? ForeignNamespace : index % 17 == 0 ? string.Empty : SvgNamespace;
            string prefix = string.IsNullOrEmpty(namespaceUri) ? string.Empty : "n";
            XmlElement element = document.CreateElement(prefix, name, namespaceUri);
            element.SetAttribute("id", $"wide-{index}");
            if (index % 3 == 0)
                element.SetAttribute("style", "fill:red");
            root.AppendChild(element);

            // The legacy animation/foreignObject finders prune these nested
            // subtrees for their own category, but the other finders still see
            // their descendants.
            if (index % 43 == 0 && element.LocalName.Equals("animate", StringComparison.OrdinalIgnoreCase))
            {
                XmlElement nested = document.CreateElement(string.Empty, "animate", SvgNamespace);
                nested.SetAttribute("id", $"wide-nested-animation-{index}");
                element.AppendChild(nested);
            }
            if (index % 47 == 0 && element.LocalName.Equals("foreignObject", StringComparison.OrdinalIgnoreCase))
            {
                XmlElement nested = document.CreateElement(string.Empty, "foreignObject", SvgNamespace);
                nested.SetAttribute("id", $"wide-nested-foreign-{index}");
                XmlElement childScript = document.CreateElement(string.Empty, "script", SvgNamespace);
                childScript.SetAttribute("id", $"wide-foreign-script-{index}");
                nested.AppendChild(childScript);
                element.AppendChild(nested);
            }
        }

        AssertMatchesLegacyFinders(document);
    }

    [Fact]
    public void SingleTraversalMatchesLegacyOnDeepDocumentWithoutUsingTraversalStack()
    {
        XmlDocument document = CreateDocument();
        XmlElement current = document.DocumentElement!;
        for (int depth = 0; depth < 4096; depth++)
        {
            string localName = depth is 300 or 700 ? "animate" :
                depth is 1700 or 2600 ? "foreignObject" : "g";
            XmlElement child = document.CreateElement(string.Empty, localName, SvgNamespace);
            child.SetAttribute("id", $"deep-{depth}");
            child.SetAttribute("style", "fill:blue");
            current.AppendChild(child);
            current = child;
        }
        XmlElement nestedScript = document.CreateElement(string.Empty, "script", SvgNamespace);
        nestedScript.SetAttribute("id", "deep-script");
        current.AppendChild(nestedScript);

        AssertMatchesLegacyFinders(document);
    }

    [Fact]
    public void DiscoveryObservesCancellationBeforeAllocatingOrTraversingLists()
    {
        XmlDocument document = Load(
            "<svg xmlns='" + SvgNamespace + "'><animate id='animation'><foreignObject id='foreign'/></animate></svg>");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => SvgStaticSnapshot.DiscoverElements(
            document,
            cancellation.Token,
            out _,
            out _,
            out _,
            out _,
            out _));
    }

    private static void AssertMatchesLegacyFinders(XmlDocument document)
    {
        LegacyDiscovery expected = LegacyFind(document);
        SvgStaticSnapshot.DiscoverElements(
            document,
            CancellationToken.None,
            out List<XmlElement> animations,
            out List<XmlElement> scripts,
            out List<XmlElement> foreignObjects,
            out List<XmlElement> styles,
            out List<XmlElement> inlineStyles);

        Assert.Equal(Ids(expected.Animations), Ids(animations));
        Assert.Equal(Ids(expected.Scripts), Ids(scripts));
        Assert.Equal(Ids(expected.ForeignObjects), Ids(foreignObjects));
        Assert.Equal(Ids(expected.Styles), Ids(styles));
        Assert.Equal(Ids(expected.InlineStyles), Ids(inlineStyles));
    }

    private static LegacyDiscovery LegacyFind(XmlDocument document) => new(
        FindAnimations(document),
        FindScripts(document),
        FindForeignObjects(document),
        FindElements(document, "style"),
        FindInlineStyles(document));

    private static List<XmlElement> FindAnimations(XmlDocument document)
    {
        var result = new List<XmlElement>();
        if (document.DocumentElement is not XmlElement root)
            return result;
        var pending = new Stack<XmlNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            XmlNode node = pending.Pop();
            if (node is XmlElement element && IsAnimation(element))
            {
                result.Add(element);
                continue;
            }
            PushChildrenInPreorderOrder(node, pending);
        }
        return result;
    }

    private static List<XmlElement> FindForeignObjects(XmlDocument document)
    {
        var result = new List<XmlElement>();
        if (document.DocumentElement is not XmlElement root)
            return result;
        var pending = new Stack<XmlNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            XmlNode node = pending.Pop();
            if (node is XmlElement element && IsSvgOrUnqualified(element, "foreignObject"))
            {
                result.Add(element);
                continue;
            }
            PushChildrenInPreorderOrder(node, pending);
        }
        return result;
    }

    private static List<XmlElement> FindScripts(XmlDocument document)
    {
        List<XmlElement> scripts = FindElements(document, "script");
        scripts.RemoveAll(script =>
        {
            for (XmlNode? ancestor = script.ParentNode; ancestor is XmlElement element; ancestor = ancestor.ParentNode)
            {
                if (IsSvgOrUnqualified(element, "foreignObject"))
                    return true;
            }
            return false;
        });
        return scripts;
    }

    private static List<XmlElement> FindElements(XmlDocument document, string localName)
    {
        var result = new List<XmlElement>();
        if (document.DocumentElement is not XmlElement root)
            return result;
        var pending = new Stack<XmlNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            XmlNode node = pending.Pop();
            if (node is XmlElement element && IsSvgOrUnqualified(element, localName))
                result.Add(element);
            PushChildrenInPreorderOrder(node, pending);
        }
        return result;
    }

    private static List<XmlElement> FindInlineStyles(XmlDocument document)
    {
        var result = new List<XmlElement>();
        if (document.DocumentElement is not XmlElement root)
            return result;
        var pending = new Stack<XmlNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            XmlNode node = pending.Pop();
            if (node is XmlElement element && element.HasAttribute("style"))
                result.Add(element);
            PushChildrenInPreorderOrder(node, pending);
        }
        return result;
    }

    private static void PushChildrenInPreorderOrder(XmlNode node, Stack<XmlNode> pending)
    {
        for (XmlNode? child = node.LastChild; child is not null; child = child.PreviousSibling)
            pending.Push(child);
    }

    private static bool IsAnimation(XmlElement element) =>
        IsSvgOrUnqualified(element, "animate") ||
        IsSvgOrUnqualified(element, "animateColor") ||
        IsSvgOrUnqualified(element, "animateMotion") ||
        IsSvgOrUnqualified(element, "animateTransform") ||
        IsSvgOrUnqualified(element, "set") ||
        IsSvgOrUnqualified(element, "discard");

    private static bool IsSvgOrUnqualified(XmlElement element, string localName) =>
        (string.IsNullOrEmpty(element.NamespaceURI) || element.NamespaceURI.Equals(SvgNamespace, StringComparison.Ordinal)) &&
        element.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase);

    private static string[] Ids(List<XmlElement> elements)
    {
        var result = new string[elements.Count];
        for (int index = 0; index < elements.Count; index++)
            result[index] = elements[index].GetAttribute("id");
        return result;
    }

    private static XmlDocument Load(string xml)
    {
        var document = new XmlDocument
        {
            PreserveWhitespace = true,
            XmlResolver = null,
        };
        document.LoadXml(xml);
        return document;
    }

    private static XmlDocument CreateDocument()
    {
        var document = new XmlDocument
        {
            PreserveWhitespace = true,
            XmlResolver = null,
        };
        XmlElement root = document.CreateElement(string.Empty, "svg", SvgNamespace);
        root.SetAttribute("id", "root");
        document.AppendChild(root);
        return document;
    }

    private sealed record LegacyDiscovery(
        List<XmlElement> Animations,
        List<XmlElement> Scripts,
        List<XmlElement> ForeignObjects,
        List<XmlElement> Styles,
        List<XmlElement> InlineStyles);
}
