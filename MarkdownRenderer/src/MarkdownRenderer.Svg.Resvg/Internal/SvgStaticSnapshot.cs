using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Converts browser-image SVG features into a deterministic, inert first frame.
/// The renderer remains a static-SVG renderer: scripts and animation nodes never
/// cross the host/worker boundary, and the normal security preflight validates
/// the resulting document before it is opened by the provider.
/// </summary>
internal static class SvgStaticSnapshot
{
    public static byte[] Create(
        byte[] bytes,
        CancellationToken cancellationToken,
        int maxOutputBytes = SvgResourceBudget.MaxInputBytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputBytes);
        if (maxOutputBytes > SvgResourceBudget.MaxInputBytes)
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));

        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length == 0 ||
            bytes.Length > SvgResourceBudget.MaxInputBytes ||
            !MayRequireStaticSnapshot(bytes, cancellationToken))
        {
            return bytes;
        }

        try
        {
            XmlDocument document;
            using (var input = new CancellationAwareReadStream(bytes, cancellationToken))
                document = LoadDocument(input, cancellationToken);
            return TransformLoadedDocument(bytes, document, cancellationToken, maxOutputBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SvgStaticSnapshotResourceException)
        {
            throw;
        }
        catch (XmlException)
        {
            // Preserve the original bytes so the authoritative preflight can
            // report its normal typed invalid-XML failure.
            cancellationToken.ThrowIfCancellationRequested();
            return bytes;
        }
    }

    internal static XmlDocument LoadDocument(Stream input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        var document = new XmlDocument
        {
            PreserveWhitespace = true,
            XmlResolver = null,
        };

        try
        {
            using XmlReader parser = XmlReader.Create(input, new XmlReaderSettings
            {
                Async = false,
                DtdProcessing = DtdProcessing.Parse,
                XmlResolver = null,
                IgnoreComments = false,
                IgnoreProcessingInstructions = false,
                MaxCharactersFromEntities = 1,
                MaxCharactersInDocument = SvgResourceBudget.MaxInputBytes,
            });
            using var reader = new BudgetedXmlReader(parser, cancellationToken);
            document.Load(reader);
            cancellationToken.ThrowIfCancellationRequested();
            return document;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            cancellationToken.IsCancellationRequested &&
            exception is XmlException or IOException)
        {
            throw new OperationCanceledException(
                "Static SVG source preparation was canceled while loading XML.",
                exception,
                cancellationToken);
        }
    }

    /// <summary>
    /// Applies static-content transformations to an already-loaded DOM. Normal
    /// callers must use <see cref="Create(byte[], CancellationToken, int)"/>,
    /// which loads through the structural-budget reader first. Keeping this
    /// algorithm boundary separate also lets tests exercise iterative DOM
    /// transforms using synthetic deep trees without admitting those raw trees.
    /// </summary>
    internal static byte[] TransformLoadedDocument(
        byte[] originalBytes,
        XmlDocument document,
        CancellationToken cancellationToken,
        int maxOutputBytes = SvgResourceBudget.MaxInputBytes)
    {
        ArgumentNullException.ThrowIfNull(originalBytes);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputBytes);
        if (maxOutputBytes > SvgResourceBudget.MaxInputBytes)
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        cancellationToken.ThrowIfCancellationRequested();

        DiscoverElements(
            document,
            cancellationToken,
            out List<XmlElement> animationElements,
            out List<XmlElement> scriptElements,
            out List<XmlElement> foreignObjects,
            out List<XmlElement> styleElements,
            out List<XmlElement> inlineStyleElements);
        bool changed = false;

        foreach (XmlElement script in scriptElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Script is never executed for an SVG loaded through a browser
            // image element. Removing SVG-namespace scripts reproduces that
            // inert static-image surface while ensuring executable bytes do
            // not reach either host preflight or the isolated worker. XHTML
            // scripts inside foreignObject are deliberately not matched and
            // keep the entire unsupported branch rejected.
            script.ParentNode?.RemoveChild(script);
            changed = true;
        }

        foreach (XmlElement animation in animationElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SvgAnimationInitialValues.ApplyInitialValue(animation, cancellationToken);
            animation.ParentNode?.RemoveChild(animation);
            changed = true;
        }

        Dictionary<XmlElement, bool>? switchFallbackByParent = null;
        foreach (XmlElement foreignObject in foreignObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasSwitchFallback(foreignObject, ref switchFallbackByParent, cancellationToken))
            {
                // Draw.io and similar exporters put an XHTML label first in
                // <switch> and a native SVG <text> fallback second. The
                // XHTML branch is executable web content and never crosses
                // the renderer boundary; retaining the authored SVG branch
                // gives resvg the same safe fallback a non-HTML user agent
                // selects.
                foreignObject.ParentNode?.RemoveChild(foreignObject);
                changed = true;
            }
            else if (SvgForeignObjectText.TryCreatePlainTextReplacement(
                document,
                foreignObject,
                cancellationToken,
                out XmlElement replacement))
            {
                XmlNode? parent = foreignObject.ParentNode;
                parent?.ReplaceChild(replacement, foreignObject);
                // The foreignObject itself is excluded from fallback
                // discovery, but this replacement is an SVG <text> child
                // and therefore becomes a fallback for later siblings.
                // Keep the per-parent memo coherent as the tree changes.
                if (parent is XmlElement switchParent &&
                    switchFallbackByParent is not null &&
                    switchFallbackByParent.ContainsKey(switchParent))
                {
                    switchFallbackByParent[switchParent] = true;
                }
                changed = true;
            }
        }

        foreach (XmlElement style in styleElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string styleText = SvgForeignObjectText.GetInnerTextIterative(style, cancellationToken);
            if (SvgStaticStyleSheet.TryCreateStaticStyleSheet(styleText, cancellationToken, out string sanitizedCss))
            {
                style.InnerText = sanitizedCss;
                changed = true;
            }
        }

        foreach (XmlElement element in inlineStyleElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string inlineStyle = element.GetAttribute("style");
            if (!SvgStaticStyleSheet.TryCreateStaticStyleSheet(inlineStyle, cancellationToken, out string sanitizedCss))
                continue;

            if (string.IsNullOrWhiteSpace(sanitizedCss))
                element.RemoveAttribute("style");
            else
                element.SetAttribute("style", sanitizedCss);
            changed = true;
        }

        if (!changed)
            return originalBytes;

        using var output = new BoundedSegmentedWriteStream(maxOutputBytes, cancellationToken);
        try
        {
            using (XmlWriter writer = XmlWriter.Create(output, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                Indent = false,
                NewLineHandling = NewLineHandling.None,
                OmitXmlDeclaration = document.FirstChild?.NodeType != XmlNodeType.XmlDeclaration,
            }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                document.Save(writer);
            }
        }
        catch (XmlException) when (output.LimitExceeded)
        {
            throw new SvgStaticSnapshotResourceException();
        }

        return output.ToArray();
    }

    internal static bool HasSwitchFallback(
        XmlElement foreignObject,
        ref Dictionary<XmlElement, bool>? fallbackByParent,
        CancellationToken cancellationToken)
    {
        if (foreignObject.ParentNode is not XmlElement parent ||
            (!string.IsNullOrEmpty(parent.NamespaceURI) &&
                !parent.NamespaceURI.Equals(SvgContentRules.SvgNamespace, StringComparison.Ordinal)) ||
            !parent.LocalName.Equals("switch", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (fallbackByParent is not null && fallbackByParent.TryGetValue(parent, out bool cachedResult))
            return cachedResult;

        bool hasFallback = false;
        for (XmlNode? sibling = parent.FirstChild; sibling is not null; sibling = sibling.NextSibling)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(sibling, foreignObject) || sibling is not XmlElement element)
                continue;
            if ((string.IsNullOrEmpty(element.NamespaceURI) ||
                    element.NamespaceURI.Equals(SvgContentRules.SvgNamespace, StringComparison.Ordinal)) &&
                !element.LocalName.Equals("foreignObject", StringComparison.OrdinalIgnoreCase))
            {
                hasFallback = true;
                break;
            }
        }

        fallbackByParent ??= new Dictionary<XmlElement, bool>(ReferenceEqualityComparer.Instance);
        fallbackByParent.Add(parent, hasFallback);
        return hasFallback;
    }

    internal static void DiscoverElements(
        XmlDocument document,
        CancellationToken cancellationToken,
        out List<XmlElement> animationElements,
        out List<XmlElement> scriptElements,
        out List<XmlElement> foreignObjects,
        out List<XmlElement> styleElements,
        out List<XmlElement> inlineStyleElements)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();

        animationElements = [];
        scriptElements = [];
        foreignObjects = [];
        styleElements = [];
        inlineStyleElements = [];
        if (document.DocumentElement is not XmlElement root)
            return;

        // A stackless preorder walk visits every DOM node once. Ancestor
        // counters reproduce the old independent finders' subtree rules while
        // still allowing other categories to inspect those descendants.
        int animationAncestorDepth = 0;
        int foreignObjectAncestorDepth = 0;
        XmlNode current = root;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            XmlElement? element = current as XmlElement;
            bool isAnimation = element is not null && SvgAnimationInitialValues.IsAnimationElement(element);
            bool isForeignObject = element is not null && IsForeignObjectElement(element);
            if (element is not null)
            {
                if (isAnimation && animationAncestorDepth == 0)
                    animationElements.Add(element);
                if (isForeignObject && foreignObjectAncestorDepth == 0)
                    foreignObjects.Add(element);
                if (foreignObjectAncestorDepth == 0 && IsSvgOrUnqualified(element, "script"))
                    scriptElements.Add(element);
                if (IsSvgOrUnqualified(element, "style"))
                    styleElements.Add(element);
                if (element.HasAttribute("style"))
                    inlineStyleElements.Add(element);
            }

            if (isAnimation)
                animationAncestorDepth++;
            if (isForeignObject)
                foreignObjectAncestorDepth++;

            if (current.FirstChild is { } firstChild)
            {
                current = firstChild;
                continue;
            }

            // Leave the leaf and each completed ancestor until reaching a
            // sibling. Check cancellation on every exit; deep trees can have
            // little sibling activity while still requiring prompt teardown.
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (current is XmlElement exitedElement)
                {
                    if (SvgAnimationInitialValues.IsAnimationElement(exitedElement))
                        animationAncestorDepth--;
                    if (IsForeignObjectElement(exitedElement))
                        foreignObjectAncestorDepth--;
                }

                if (ReferenceEquals(current, root))
                    return;

                if (current.NextSibling is { } nextSibling)
                {
                    current = nextSibling;
                    break;
                }

                current = current.ParentNode!;
            }
        }
    }

    private static bool IsForeignObjectElement(XmlElement element) =>
        IsSvgOrUnqualified(element, "foreignObject");

    private static bool IsSvgOrUnqualified(XmlElement element, string localName) =>
        (string.IsNullOrEmpty(element.NamespaceURI) ||
            element.NamespaceURI.Equals(SvgContentRules.SvgNamespace, StringComparison.Ordinal)) &&
        element.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase);

    internal static bool MayRequireStaticSnapshot(
        ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlySpan<byte> marker = "<animate"u8;
        ReadOnlySpan<byte> scriptMarker = "<script"u8;
        ReadOnlySpan<byte> setMarker = "<set"u8;
        ReadOnlySpan<byte> discardMarker = "<discard"u8;
        ReadOnlySpan<byte> foreignObjectMarker = "<foreignObject"u8;
        ReadOnlySpan<byte> fontFaceMarker = "@font-face"u8;
        ReadOnlySpan<byte> importMarker = "@import"u8;
        ReadOnlySpan<byte> keyframesMarker = "@keyframes"u8;
        ReadOnlySpan<byte> webkitKeyframesMarker = "@-webkit-keyframes"u8;
        ReadOnlySpan<byte> animationMarker = "animation"u8;
        ReadOnlySpan<byte> transitionMarker = "transition"u8;
        for (int index = 0; index < bytes.Length; index++)
        {
            if ((index & 0xFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            byte first = ToAsciiLower(bytes[index]);
            if ((first == (byte)'<' &&
                    (AsciiStartsWithIgnoreCase(bytes[index..], marker) ||
                     AsciiStartsWithIgnoreCase(bytes[index..], scriptMarker) ||
                     AsciiStartsWithIgnoreCase(bytes[index..], setMarker) ||
                     AsciiStartsWithIgnoreCase(bytes[index..], discardMarker) ||
                     AsciiStartsWithIgnoreCase(bytes[index..], foreignObjectMarker))) ||
                (first == (byte)'@' &&
                    (AsciiStartsWithIgnoreCase(bytes[index..], fontFaceMarker) ||
                     AsciiStartsWithIgnoreCase(bytes[index..], importMarker) ||
                     AsciiStartsWithIgnoreCase(bytes[index..], keyframesMarker) ||
                     AsciiStartsWithIgnoreCase(bytes[index..], webkitKeyframesMarker))) ||
                (first == (byte)'a' && AsciiStartsWithIgnoreCase(bytes[index..], animationMarker)) ||
                (first == (byte)'t' && AsciiStartsWithIgnoreCase(bytes[index..], transitionMarker)))
            {
                return true;
            }
        }

        return false;
    }

    private static byte ToAsciiLower(byte value) => value is >= (byte)'A' and <= (byte)'Z'
        ? (byte)(value + ((byte)'a' - (byte)'A'))
        : value;

    private static bool AsciiStartsWithIgnoreCase(
        ReadOnlySpan<byte> value,
        ReadOnlySpan<byte> prefix)
    {
        if (value.Length < prefix.Length)
            return false;

        for (int index = 0; index < prefix.Length; index++)
        {
            byte actual = value[index];
            byte expected = prefix[index];
            if (actual is >= (byte)'A' and <= (byte)'Z')
                actual = (byte)(actual + ((byte)'a' - (byte)'A'));
            if (expected is >= (byte)'A' and <= (byte)'Z')
                expected = (byte)(expected + ((byte)'a' - (byte)'A'));
            if (actual != expected)
                return false;
        }

        return true;
    }
}
