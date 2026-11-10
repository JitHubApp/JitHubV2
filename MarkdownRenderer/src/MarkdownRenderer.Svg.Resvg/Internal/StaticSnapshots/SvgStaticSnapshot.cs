using System.Text;
using System.Xml;
using MarkdownRenderer.Svg.Resvg.Internal.Preparation;

namespace MarkdownRenderer.Svg.Resvg.Internal.StaticSnapshots;

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
            !SvgStaticSnapshotPrescan.MayRequireStaticSnapshot(bytes, cancellationToken))
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
        bool changed = RemoveScripts(scriptElements, cancellationToken);
        changed |= ReplaceAnimations(animationElements, cancellationToken);
        changed |= ReplaceForeignObjects(document, foreignObjects, cancellationToken);
        changed |= SanitizeStyleElements(styleElements, cancellationToken);
        changed |= SanitizeInlineStyles(inlineStyleElements, cancellationToken);
        if (!changed)
            return originalBytes;

        return Serialize(document, cancellationToken, maxOutputBytes);
    }

    private static bool RemoveScripts(List<XmlElement> scriptElements, CancellationToken cancellationToken)
    {
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

        return changed;
    }

    private static bool ReplaceAnimations(List<XmlElement> animationElements, CancellationToken cancellationToken)
    {
        bool changed = false;
        foreach (XmlElement animation in animationElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SvgAnimationInitialValues.ApplyInitialValue(animation, cancellationToken);
            animation.ParentNode?.RemoveChild(animation);
            changed = true;
        }

        return changed;
    }

    private static bool ReplaceForeignObjects(
        XmlDocument document,
        List<XmlElement> foreignObjects,
        CancellationToken cancellationToken)
    {
        bool changed = false;
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

        return changed;
    }

    private static bool SanitizeStyleElements(List<XmlElement> styleElements, CancellationToken cancellationToken)
    {
        bool changed = false;
        foreach (XmlElement style in styleElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string styleText = SvgDomText.GetInnerTextIterative(style, cancellationToken);
            if (SvgStaticStyleSheet.TryCreateStaticStyleSheet(styleText, cancellationToken, out string sanitizedCss))
            {
                style.InnerText = sanitizedCss;
                changed = true;
            }
        }

        return changed;
    }

    private static bool SanitizeInlineStyles(List<XmlElement> inlineStyleElements, CancellationToken cancellationToken)
    {
        bool changed = false;
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

        return changed;
    }

    private static byte[] Serialize(XmlDocument document, CancellationToken cancellationToken, int maxOutputBytes)
    {
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

            if (!TryAdvanceToNextSibling(
                ref current,
                root,
                ref animationAncestorDepth,
                ref foreignObjectAncestorDepth,
                cancellationToken))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Leaves the leaf and each completed ancestor until reaching a sibling,
    /// unwinding the ancestor counters as each element is exited. Checks
    /// cancellation on every exit; deep trees can have little sibling activity
    /// while still requiring prompt teardown.
    /// </summary>
    /// <param name="current">The leaf being left; on success, the next sibling to visit.</param>
    /// <param name="root">The walk's root element.</param>
    /// <param name="animationAncestorDepth">The open animation-ancestor count.</param>
    /// <param name="foreignObjectAncestorDepth">The open foreignObject-ancestor count.</param>
    /// <param name="cancellationToken">Cancels discovery.</param>
    /// <returns><see langword="false"/> once the root itself has been exited.</returns>
    private static bool TryAdvanceToNextSibling(
        ref XmlNode current,
        XmlNode root,
        ref int animationAncestorDepth,
        ref int foreignObjectAncestorDepth,
        CancellationToken cancellationToken)
    {
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
                return false;

            if (current.NextSibling is { } nextSibling)
            {
                current = nextSibling;
                return true;
            }

            current = current.ParentNode!;
        }
    }

    private static bool IsForeignObjectElement(XmlElement element) =>
        IsSvgOrUnqualified(element, "foreignObject");

    private static bool IsSvgOrUnqualified(XmlElement element, string localName) =>
        (string.IsNullOrEmpty(element.NamespaceURI) ||
            element.NamespaceURI.Equals(SvgContentRules.SvgNamespace, StringComparison.Ordinal)) &&
        element.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase);

}
