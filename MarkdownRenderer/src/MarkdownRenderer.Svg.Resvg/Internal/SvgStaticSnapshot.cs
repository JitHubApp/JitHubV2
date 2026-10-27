using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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
    private const string SvgNamespace = "http://www.w3.org/2000/svg";
    private const int CssCancellationCheckInterval = 4096;
    private static readonly string[] CssBlockDirectives = ["@font-face", "@keyframes", "@-webkit-keyframes"];

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
            ApplyInitialValue(animation, cancellationToken);
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
            else if (TryCreatePlainTextReplacement(
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
            string styleText = GetInnerTextIterative(style, cancellationToken);
            if (TryCreateStaticStyleSheet(styleText, cancellationToken, out string sanitizedCss))
            {
                style.InnerText = sanitizedCss;
                changed = true;
            }
        }

        foreach (XmlElement element in inlineStyleElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string inlineStyle = element.GetAttribute("style");
            if (!TryCreateStaticStyleSheet(inlineStyle, cancellationToken, out string sanitizedCss))
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
                !parent.NamespaceURI.Equals(SvgNamespace, StringComparison.Ordinal)) ||
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
                    element.NamespaceURI.Equals(SvgNamespace, StringComparison.Ordinal)) &&
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
            bool isAnimation = element is not null && IsAnimationElement(element);
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
                    if (IsAnimationElement(exitedElement))
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

    /// <summary>
    /// Keeps the static-snapshot DOM load within the same structural ceilings as
    /// the streaming security preflight. Counts are applied as the parser yields
    /// each node, before XmlDocument can materialize that node and its attributes.
    /// This bounds DOM amplification, not the parser's own input buffers or total
    /// process memory.
    /// </summary>
    internal sealed class XmlStructuralBudget
    {
        private int _elementsSeen;
        private int _attributesSeen;

        internal XmlStructuralBudget(int elementsSeen = 0, int attributesSeen = 0)
        {
            if ((uint)elementsSeen > SvgResourceBudget.MaxElements)
                throw new ArgumentOutOfRangeException(nameof(elementsSeen));
            if ((uint)attributesSeen > SvgResourceBudget.MaxAttributes)
                throw new ArgumentOutOfRangeException(nameof(attributesSeen));

            _elementsSeen = elementsSeen;
            _attributesSeen = attributesSeen;
        }

        internal void Observe(XmlReader reader)
        {
            if (reader.Depth > SvgResourceBudget.MaxDepth)
                throw new SvgStaticSnapshotResourceException(
                    SvgStaticSnapshotResourceException.ElementDepthReason);

            if (reader.NodeType != XmlNodeType.Element)
                return;

            if (_elementsSeen >= SvgResourceBudget.MaxElements)
                throw new SvgStaticSnapshotResourceException(
                    SvgStaticSnapshotResourceException.ElementCountReason);
            _elementsSeen++;

            int currentElementAttributes = reader.AttributeCount;
            if (currentElementAttributes > SvgResourceBudget.MaxAttributes - _attributesSeen)
                throw new SvgStaticSnapshotResourceException(
                    SvgStaticSnapshotResourceException.AttributeCountReason);
            _attributesSeen += currentElementAttributes;
        }
    }

    /// <summary>
    /// Transparent synchronous reader facade that checks cancellation and
    /// structural budgets before exposing each parsed node to XmlDocument.
    /// Parser settings and node/attribute navigation continue to come from the
    /// underlying XmlReader.
    /// </summary>
    internal sealed class BudgetedXmlReader : XmlReader
    {
        private readonly XmlReader _reader;
        private readonly CancellationToken _cancellationToken;
        private readonly XmlStructuralBudget _budget;

        internal BudgetedXmlReader(
            XmlReader reader,
            CancellationToken cancellationToken,
            XmlStructuralBudget? budget = null)
        {
            ArgumentNullException.ThrowIfNull(reader);
            _reader = reader;
            _cancellationToken = cancellationToken;
            _budget = budget ?? new XmlStructuralBudget();
        }

        public override XmlReaderSettings? Settings => _reader.Settings;
        public override XmlNodeType NodeType => _reader.NodeType;
        public override string LocalName => _reader.LocalName;
        public override string NamespaceURI => _reader.NamespaceURI;
        public override string Prefix => _reader.Prefix;
        public override string Value => _reader.Value;
        public override int Depth => _reader.Depth;
        public override string BaseURI => _reader.BaseURI;
        public override bool IsEmptyElement => _reader.IsEmptyElement;
        public override int AttributeCount => _reader.AttributeCount;
        public override bool EOF => _reader.EOF;
        public override ReadState ReadState => _reader.ReadState;
        public override XmlNameTable NameTable => _reader.NameTable;
        public override string Name => _reader.Name;
        public override bool HasValue => _reader.HasValue;
        public override bool IsDefault => _reader.IsDefault;
        public override char QuoteChar => _reader.QuoteChar;
        public override XmlSpace XmlSpace => _reader.XmlSpace;
        public override string XmlLang => _reader.XmlLang;
        public override System.Xml.Schema.IXmlSchemaInfo? SchemaInfo => _reader.SchemaInfo;
        public override Type ValueType => _reader.ValueType;
        public override bool CanResolveEntity => _reader.CanResolveEntity;
        public override bool HasAttributes => _reader.HasAttributes;

        public override string this[int i] => _reader[i];
        public override string? this[string name] => _reader[name];
        public override string? this[string name, string? namespaceURI] => _reader[name, namespaceURI];

        public override string? GetAttribute(string name) => _reader.GetAttribute(name);
        public override string? GetAttribute(string name, string? namespaceURI) =>
            _reader.GetAttribute(name, namespaceURI);
        public override string GetAttribute(int i) => _reader.GetAttribute(i);

        public override bool MoveToAttribute(string name) => _reader.MoveToAttribute(name);
        public override bool MoveToAttribute(string name, string? namespaceURI) =>
            _reader.MoveToAttribute(name, namespaceURI);
        public override void MoveToAttribute(int i) => _reader.MoveToAttribute(i);
        public override bool MoveToFirstAttribute() => _reader.MoveToFirstAttribute();
        public override bool MoveToNextAttribute() => _reader.MoveToNextAttribute();
        public override bool MoveToElement() => _reader.MoveToElement();

        public override bool ReadAttributeValue()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            bool result = _reader.ReadAttributeValue();
            _cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        public override bool Read()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            bool result = _reader.Read();
            _cancellationToken.ThrowIfCancellationRequested();
            if (result)
                _budget.Observe(_reader);
            return result;
        }

        public override string? LookupNamespace(string prefix) => _reader.LookupNamespace(prefix);

        public override void ResolveEntity()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _reader.ResolveEntity();
            _cancellationToken.ThrowIfCancellationRequested();
        }

        public override void Close() => _reader.Close();
    }

    private static bool IsSvgOrUnqualified(XmlElement element, string localName) =>
        (string.IsNullOrEmpty(element.NamespaceURI) ||
            element.NamespaceURI.Equals(SvgNamespace, StringComparison.Ordinal)) &&
        element.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase);

    internal static bool TryCreateStaticStyleSheet(
        string css,
        CancellationToken cancellationToken,
        out string sanitized)
    {
        ArgumentNullException.ThrowIfNull(css);
        cancellationToken.ThrowIfCancellationRequested();

        List<(int Start, int Length)>? removals = null;
        int charactersUntilCancellationCheck = CssCancellationCheckInterval;
        int importCursor = 0;
        while (TryFindCssToken(
            css,
            "@import",
            importCursor,
            ref charactersUntilCancellationCheck,
            cancellationToken,
            out int importStart))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryFindCssStatementEnd(
                css,
                importStart + "@import".Length,
                ref charactersUntilCancellationCheck,
                cancellationToken,
                out int importEnd))
            {
                cancellationToken.ThrowIfCancellationRequested();
                sanitized = css;
                return false;
            }

            AddCssRemoval(ref removals, importStart, importEnd - importStart);
            importCursor = importEnd;
        }

        foreach (string directive in CssBlockDirectives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int cursor = 0;
            while (TryFindCssToken(
                css,
                directive,
                cursor,
                ref charactersUntilCancellationCheck,
                cancellationToken,
                out int start))
            {
                cancellationToken.ThrowIfCancellationRequested();
                int openingBrace = start + directive.Length;
                while (openingBrace < css.Length && css[openingBrace] != '{')
                {
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    openingBrace++;
                }
                if (openingBrace >= css.Length ||
                    !TryFindCssBlockEnd(
                        css,
                        openingBrace,
                        ref charactersUntilCancellationCheck,
                        cancellationToken,
                        out int end))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sanitized = css;
                    return false;
                }

                AddCssRemoval(ref removals, start, end + 1 - start);
                cursor = end + 1;
            }
        }

        if (!TryFindMotionDeclarations(css, ref removals, ref charactersUntilCancellationCheck, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            sanitized = css;
            return false;
        }

        if (removals is null || removals.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sanitized = css;
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        sanitized = string.Create(
            css.Length,
            (Css: css, Removals: removals, CancellationToken: cancellationToken),
            static (destination, state) =>
            {
                int charactersUntilCheck = CssCancellationCheckInterval;
                for (int index = 0; index < state.Css.Length; index++)
                {
                    CheckCssCancellation(ref charactersUntilCheck, state.CancellationToken);
                    destination[index] = state.Css[index];
                }

                foreach ((int start, int length) in state.Removals)
                {
                    state.CancellationToken.ThrowIfCancellationRequested();
                    int end = checked(start + length);
                    for (int index = start; index < end; index++)
                    {
                        CheckCssCancellation(ref charactersUntilCheck, state.CancellationToken);
                        destination[index] = ' ';
                    }
                }

                state.CancellationToken.ThrowIfCancellationRequested();
            });
        return true;
    }

    private static void AddCssRemoval(
        ref List<(int Start, int Length)>? removals,
        int start,
        int length)
    {
        (removals ??= new List<(int Start, int Length)>()).Add((start, length));
    }

    private static bool TryFindCssStatementEnd(
        string css,
        int start,
        ref int charactersUntilCancellationCheck,
        CancellationToken cancellationToken,
        out int end)
    {
        char quote = '\0';
        bool comment = false;
        int parentheses = 0;
        for (int cursor = start; cursor < css.Length; cursor++)
        {
            CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            char current = css[cursor];
            if (comment)
            {
                if (current == '*' && cursor + 1 < css.Length && css[cursor + 1] == '/')
                {
                    comment = false;
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    cursor++;
                }
                continue;
            }
            if (quote != '\0')
            {
                if (current == '\\' && cursor + 1 < css.Length)
                {
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    cursor++;
                }
                else if (current == quote)
                    quote = '\0';
                continue;
            }
            if (current == '/' && cursor + 1 < css.Length && css[cursor + 1] == '*')
            {
                comment = true;
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                cursor++;
                continue;
            }
            if (current is '\'' or '"')
                quote = current;
            else if (current == '(')
                parentheses++;
            else if (current == ')' && parentheses > 0)
                parentheses--;
            else if (current == ';' && parentheses == 0)
            {
                end = cursor + 1;
                return true;
            }
            else if (current == '{' && parentheses == 0)
            {
                end = -1;
                return false;
            }
        }

        end = -1;
        return false;
    }

    private static bool TryFindMotionDeclarations(
        string css,
        ref List<(int Start, int Length)>? removals,
        ref int charactersUntilCancellationCheck,
        CancellationToken cancellationToken)
    {
        int cursor = 0;
        while (cursor < css.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TrySkipCssTrivia(css, ref cursor, ref charactersUntilCancellationCheck, cancellationToken))
                continue;

            int nameStart = cursor;
            while (cursor < css.Length &&
                (char.IsAsciiLetter(css[cursor]) || css[cursor] is '-' or '_'))
            {
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                cursor++;
            }
            if (cursor == nameStart)
            {
                cursor++;
                continue;
            }

            ReadOnlySpan<char> property = css.AsSpan(nameStart, cursor - nameStart);
            int separator = cursor;
            while (separator < css.Length && char.IsWhiteSpace(css[separator]))
            {
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                separator++;
            }
            if (separator >= css.Length || css[separator] != ':')
                continue;

            bool isMotion = IsMotionProperty(property);
            if (!isMotion)
            {
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                cursor = separator + 1;
                continue;
            }

            int end = separator + 1;
            char quote = '\0';
            int parentheses = 0;
            bool encounteredNestedRule = false;
            while (end < css.Length)
            {
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                char current = css[end];
                if (quote != '\0')
                {
                    if (current == '\\' && end + 1 < css.Length)
                    {
                        CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                        end += 2;
                    }
                    else
                    {
                        if (current == quote)
                            quote = '\0';
                        end++;
                    }
                    continue;
                }
                if (current is '\'' or '"')
                    quote = current;
                else if (current == '(')
                    parentheses++;
                else if (current == ')' && parentheses > 0)
                    parentheses--;
                else if (parentheses == 0 && current == '{')
                {
                    // A block opener before a declaration terminator means the
                    // token was part of a selector or an at-rule prelude (for
                    // example `.animation:hover { ... }`), not a property.
                    encounteredNestedRule = true;
                    break;
                }
                else if (parentheses == 0 && current is (';' or '}'))
                    break;
                end++;
            }
            if (quote != '\0' || parentheses != 0)
                return false;
            if (encounteredNestedRule)
            {
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                cursor = end + 1;
                continue;
            }

            if (end < css.Length && css[end] == ';')
                end++;
            AddCssRemoval(ref removals, nameStart, end - nameStart);
            cursor = Math.Max(end, separator + 1);
        }

        return true;
    }

    private static bool IsMotionProperty(ReadOnlySpan<char> property) =>
        property.Equals("animation", StringComparison.OrdinalIgnoreCase) ||
        property.StartsWith("animation-", StringComparison.OrdinalIgnoreCase) ||
        property.Equals("transition", StringComparison.OrdinalIgnoreCase) ||
        property.StartsWith("transition-", StringComparison.OrdinalIgnoreCase);

    private static void CheckCssCancellation(
        ref int charactersUntilCancellationCheck,
        CancellationToken cancellationToken)
    {
        if (--charactersUntilCancellationCheck <= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            charactersUntilCancellationCheck = CssCancellationCheckInterval;
        }
    }

    private static bool TrySkipCssTrivia(
        string css,
        ref int cursor,
        ref int charactersUntilCancellationCheck,
        CancellationToken cancellationToken)
    {
        if (char.IsWhiteSpace(css[cursor]))
        {
            CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            cursor++;
            return true;
        }
        if (cursor + 1 < css.Length && css[cursor] == '/' && css[cursor + 1] == '*')
        {
            CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            cursor += 2;
            while (cursor < css.Length)
            {
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                if (css[cursor] == '*' && cursor + 1 < css.Length && css[cursor + 1] == '/')
                {
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    cursor += 2;
                    break;
                }
                cursor++;
            }
            return true;
        }
        if (css[cursor] is '\'' or '"')
        {
            CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            char quote = css[cursor++];
            while (cursor < css.Length && css[cursor] != quote)
            {
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                if (css[cursor] == '\\' && cursor + 1 < css.Length)
                {
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    cursor += 2;
                }
                else
                {
                    cursor++;
                }
            }
            if (cursor < css.Length)
            {
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                cursor++;
            }
            return true;
        }
        return false;
    }

    private static bool TryFindCssToken(
        string css,
        string token,
        int start,
        ref int charactersUntilCancellationCheck,
        CancellationToken cancellationToken,
        out int index)
    {
        char quote = '\0';
        bool comment = false;
        for (int cursor = Math.Max(0, start); cursor < css.Length; cursor++)
        {
            CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            char current = css[cursor];
            if (comment)
            {
                if (current == '*' && cursor + 1 < css.Length && css[cursor + 1] == '/')
                {
                    comment = false;
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    cursor++;
                }
                continue;
            }
            if (quote != '\0')
            {
                if (current == '\\' && cursor + 1 < css.Length)
                {
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    cursor++;
                }
                else if (current == quote)
                    quote = '\0';
                continue;
            }
            if (current == '/' && cursor + 1 < css.Length && css[cursor + 1] == '*')
            {
                comment = true;
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                cursor++;
                continue;
            }
            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }
            if (css.AsSpan(cursor).StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                index = cursor;
                return true;
            }
        }

        index = -1;
        return false;
    }

    private static bool TryFindCssBlockEnd(
        string css,
        int openingBrace,
        ref int charactersUntilCancellationCheck,
        CancellationToken cancellationToken,
        out int end)
    {
        int depth = 0;
        char quote = '\0';
        bool comment = false;
        for (int cursor = openingBrace; cursor < css.Length; cursor++)
        {
            CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            char current = css[cursor];
            if (comment)
            {
                if (current == '*' && cursor + 1 < css.Length && css[cursor + 1] == '/')
                {
                    comment = false;
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    cursor++;
                }
                continue;
            }
            if (quote != '\0')
            {
                if (current == '\\' && cursor + 1 < css.Length)
                {
                    CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                    cursor++;
                }
                else if (current == quote)
                    quote = '\0';
                continue;
            }
            if (current == '/' && cursor + 1 < css.Length && css[cursor + 1] == '*')
            {
                comment = true;
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                cursor++;
                continue;
            }
            if (current is '\'' or '"')
            {
                quote = current;
                continue;
            }
            if (current == '{')
                depth++;
            else if (current == '}' && --depth == 0)
            {
                end = cursor;
                return true;
            }
        }

        end = -1;
        return false;
    }

    private static bool TryCreatePlainTextReplacement(
        XmlDocument document,
        XmlElement foreignObject,
        CancellationToken cancellationToken,
        out XmlElement replacement)
    {
        replacement = null!;
        if (!HasOnlySafeForeignObjectAttributes(foreignObject, cancellationToken) ||
            !TryReadLength(foreignObject.GetAttribute("x"), defaultValue: 0, out double x) ||
            !TryReadLength(foreignObject.GetAttribute("y"), defaultValue: 0, out double y) ||
            !TryReadForeignObjectExtent(
                document,
                foreignObject.GetAttribute("width"),
                true,
                cancellationToken,
                out double width) ||
            !TryReadForeignObjectExtent(
                document,
                foreignObject.GetAttribute("height"),
                false,
                cancellationToken,
                out double height) ||
            width <= 0 ||
            height <= 0 ||
            !TryExtractPlainHtmlText(foreignObject, cancellationToken, out string text))
        {
            return false;
        }

        Dictionary<string, string> style = ParseStyle(foreignObject.GetAttribute("style"), cancellationToken);
        if (style.Keys.Any(static name => !IsSupportedTextStyle(name)))
            return false;

        double fontSize = 16;
        if (style.TryGetValue("font-size", out string? fontSizeText) &&
            (!TryReadLength(fontSizeText, defaultValue: double.NaN, out fontSize) ||
                fontSize <= 0 ||
                fontSize > 4096))
        {
            return false;
        }

        XmlElement svgText = document.CreateElement("text", SvgNamespace);
        string alignment = style.GetValueOrDefault("text-align", "start").Trim();
        if (alignment.Equals("center", StringComparison.OrdinalIgnoreCase))
        {
            x += width / 2;
            svgText.SetAttribute("text-anchor", "middle");
        }
        else if (alignment.Equals("right", StringComparison.OrdinalIgnoreCase) ||
            alignment.Equals("end", StringComparison.OrdinalIgnoreCase))
        {
            x += width;
            svgText.SetAttribute("text-anchor", "end");
        }
        else if (!alignment.Equals("left", StringComparison.OrdinalIgnoreCase) &&
            !alignment.Equals("start", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        svgText.SetAttribute("x", FormatNumber(x));
        svgText.SetAttribute("y", FormatNumber(y + Math.Min(fontSize, height)));
        svgText.SetAttribute("font-size", FormatNumber(fontSize));

        if (style.TryGetValue("color", out string? color))
        {
            if (!IsSafeCssToken(color, 128))
                return false;
            svgText.SetAttribute("fill", color.Trim());
        }

        if (style.TryGetValue("font-family", out string? fontFamily))
        {
            if (!IsSafeCssToken(fontFamily, 256))
                return false;
            svgText.SetAttribute("font-family", fontFamily.Trim());
        }

        if (style.TryGetValue("font-weight", out string? fontWeight))
        {
            fontWeight = fontWeight.Trim();
            if (!fontWeight.Equals("normal", StringComparison.OrdinalIgnoreCase) &&
                !fontWeight.Equals("bold", StringComparison.OrdinalIgnoreCase) &&
                (!int.TryParse(fontWeight, NumberStyles.None, CultureInfo.InvariantCulture, out int numericWeight) ||
                    numericWeight is < 1 or > 1000))
            {
                return false;
            }
            svgText.SetAttribute("font-weight", fontWeight);
        }

        if (style.TryGetValue("font-style", out string? fontStyle))
        {
            fontStyle = fontStyle.Trim();
            if (!fontStyle.Equals("normal", StringComparison.OrdinalIgnoreCase) &&
                !fontStyle.Equals("italic", StringComparison.OrdinalIgnoreCase) &&
                !fontStyle.Equals("oblique", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            svgText.SetAttribute("font-style", fontStyle);
        }

        if (style.TryGetValue("letter-spacing", out string? letterSpacing))
        {
            if (!TryReadLength(letterSpacing, defaultValue: double.NaN, out double spacing) ||
                Math.Abs(spacing) > 4096)
            {
                return false;
            }
            svgText.SetAttribute("letter-spacing", FormatNumber(spacing));
        }

        if (style.TryGetValue("line-height", out string? lineHeight) &&
            (!TryReadLength(lineHeight, defaultValue: double.NaN, out double parsedLineHeight) ||
                parsedLineHeight <= 0 ||
                parsedLineHeight > 4096))
        {
            return false;
        }

        if (foreignObject.HasAttribute("transform"))
            svgText.SetAttribute("transform", foreignObject.GetAttribute("transform"));
        if (foreignObject.HasAttribute("opacity"))
            svgText.SetAttribute("opacity", foreignObject.GetAttribute("opacity"));

        svgText.InnerText = text;
        replacement = svgText;
        return true;
    }

    private static bool TryReadForeignObjectExtent(
        XmlDocument document,
        string value,
        bool horizontal,
        CancellationToken cancellationToken,
        out double extent)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryReadLength(value, defaultValue: double.NaN, out extent))
            return true;

        value = value.Trim();
        if (!value.EndsWith('%') ||
            !double.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double percentage) ||
            percentage is <= 0 or > 100 ||
            !TryReadSvgViewportExtent(document.DocumentElement, horizontal, cancellationToken, out double viewport))
        {
            extent = double.NaN;
            return false;
        }

        extent = viewport * percentage / 100;
        return double.IsFinite(extent) && extent > 0;
    }

    internal static bool TryReadSvgViewportExtent(
        XmlElement? root,
        bool horizontal,
        CancellationToken cancellationToken,
        out double extent)
    {
        cancellationToken.ThrowIfCancellationRequested();
        extent = double.NaN;
        if (root is null)
            return false;

        string dimension = root.GetAttribute(horizontal ? "width" : "height");
        if (TryReadLength(dimension, defaultValue: double.NaN, out extent) && extent > 0)
            return true;

        return TryParseViewBoxExtent(root.GetAttribute("viewBox"), horizontal, cancellationToken, out extent);
    }

    internal static bool TryParseViewBoxExtent(
        string viewBox,
        bool horizontal,
        CancellationToken cancellationToken,
        out double extent)
    {
        cancellationToken.ThrowIfCancellationRequested();
        extent = double.NaN;
        Range requestedToken = default;
        int tokenCount = 0;
        int charactersUntilCancellationCheck = CssCancellationCheckInterval;
        int tokenStart = 0;
        for (int cursor = 0; cursor <= viewBox.Length; cursor++)
        {
            if (cursor < viewBox.Length)
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);

            if (cursor < viewBox.Length && viewBox[cursor] is not (' ' or ','))
                continue;

            int trimmedStart = tokenStart;
            int trimmedEnd = cursor;
            while (trimmedStart < trimmedEnd && char.IsWhiteSpace(viewBox[trimmedStart]))
            {
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                trimmedStart++;
            }
            while (trimmedEnd > trimmedStart && char.IsWhiteSpace(viewBox[trimmedEnd - 1]))
            {
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                trimmedEnd--;
            }

            if (trimmedStart < trimmedEnd)
            {
                if (tokenCount == (horizontal ? 2 : 3))
                    requestedToken = new Range(trimmedStart, trimmedEnd);
                tokenCount++;
            }

            tokenStart = cursor + 1;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return tokenCount == 4 &&
            double.TryParse(viewBox.AsSpan(requestedToken), NumberStyles.Float,
                CultureInfo.InvariantCulture, out extent) &&
            double.IsFinite(extent) &&
            extent > 0;
    }

    private static bool HasOnlySafeForeignObjectAttributes(
        XmlElement foreignObject,
        CancellationToken cancellationToken)
    {
        foreach (XmlAttribute attribute in foreignObject.Attributes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.NamespaceURI.Equals("http://www.w3.org/2000/xmlns/", StringComparison.Ordinal))
                continue;
            if (attribute.NamespaceURI.Length != 0 ||
                !IsSupportedForeignObjectAttribute(attribute.LocalName))
            {
                return false;
            }

            if ((attribute.LocalName.Equals("class", StringComparison.OrdinalIgnoreCase) ||
                    attribute.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase)) &&
                !IsSafeCssToken(attribute.Value.Trim(), 512))
            {
                return false;
            }

            if (attribute.LocalName.Equals("selection", StringComparison.OrdinalIgnoreCase) &&
                !bool.TryParse(attribute.Value, out _))
            {
                return false;
            }

            if (attribute.LocalName.Equals("requiredFeatures", StringComparison.OrdinalIgnoreCase) &&
                !attribute.Value.Equals(
                    "http://www.w3.org/TR/SVG11/feature#Extensibility",
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSupportedForeignObjectAttribute(string name) =>
        name.Equals("x", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("y", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("width", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("height", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("style", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("transform", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("opacity", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("class", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("requiredFeatures", StringComparison.OrdinalIgnoreCase) ||
        // Generated Trendshift cards use this inert authoring hint.
        name.Equals("selection", StringComparison.OrdinalIgnoreCase);

    private static bool IsSupportedTextStyle(string name) =>
        name.Equals("font-size", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("color", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("font-family", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("font-weight", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("font-style", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("text-align", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("letter-spacing", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("line-height", StringComparison.OrdinalIgnoreCase);

    private static bool TryExtractPlainHtmlText(
        XmlElement foreignObject,
        CancellationToken cancellationToken,
        out string text)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accumulator = new CollapsedTextAccumulator(cancellationToken);
        XmlNode? current = foreignObject.FirstChild;
        while (current is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or
                XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                if (!accumulator.Append(current.Value))
                {
                    text = string.Empty;
                    return false;
                }
                current = AdvanceAfterPlainTextSubtree(current, foreignObject, accumulator);
                if (accumulator.IsTooLong)
                {
                    text = string.Empty;
                    return false;
                }
                continue;
            }

            if (current is not XmlElement element)
            {
                text = string.Empty;
                return false;
            }

            if (element.NamespaceURI.Equals(SvgNamespace, StringComparison.Ordinal))
            {
                // CSS inside an SVG image's foreignObject is inert once the
                // entire HTML branch is replaced by native SVG text. Accept
                // only a stylesheet whose XML children are text, discard it
                // rather than forwarding CSS or executable web content.
                bool safe = element.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase)
                    ? HasOnlyTextChildren(element, cancellationToken)
                    : IsSafeDiscardedSvgGraphic(element, cancellationToken);
                if (!safe)
                {
                    text = string.Empty;
                    return false;
                }

                current = AdvanceAfterPlainTextSubtree(current, foreignObject, accumulator);
                if (accumulator.IsTooLong)
                {
                    text = string.Empty;
                    return false;
                }
                continue;
            }

            if (!element.NamespaceURI.Equals("http://www.w3.org/1999/xhtml", StringComparison.Ordinal) ||
                !IsSafeTextContainer(element.LocalName) ||
                !HasOnlySafeHtmlTextAttributes(element, cancellationToken))
            {
                text = string.Empty;
                return false;
            }

            if (AddsPlainTextBoundary(element.LocalName) && !accumulator.AppendBoundary())
            {
                text = string.Empty;
                return false;
            }

            if (element.FirstChild is { } firstChild)
            {
                current = firstChild;
                continue;
            }

            current = AdvanceAfterPlainTextSubtree(element, foreignObject, accumulator);
            if (accumulator.IsTooLong)
            {
                text = string.Empty;
                return false;
            }
        }

        text = accumulator.ToString();
        return text.Length > 0;
    }

    private static bool AddsPlainTextBoundary(string localName) =>
        localName.Equals("div", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("p", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("br", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("tr", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("li", StringComparison.OrdinalIgnoreCase) ||
        (localName.Length == 2 &&
            (localName[0] is 'h' or 'H') &&
            localName[1] is >= '1' and <= '6');

    private static XmlNode? AdvanceAfterPlainTextSubtree(
        XmlNode completedNode,
        XmlElement foreignObject,
        CollapsedTextAccumulator accumulator)
    {
        XmlNode? current = completedNode;
        while (current is not null && !ReferenceEquals(current, foreignObject))
        {
            accumulator.ThrowIfCancellationRequested();
            if (current is XmlElement element &&
                element.NamespaceURI.Equals("http://www.w3.org/1999/xhtml", StringComparison.Ordinal) &&
                AddsPlainTextBoundary(element.LocalName) &&
                !accumulator.AppendBoundary())
            {
                return null;
            }

            if (current.NextSibling is { } nextSibling)
                return nextSibling;
            current = current.ParentNode;
        }

        return null;
    }

    private static bool HasOnlyTextChildren(XmlElement element, CancellationToken cancellationToken)
    {
        foreach (XmlNode child in element.ChildNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child.NodeType is not (
                    XmlNodeType.Text or
                    XmlNodeType.CDATA or
                    XmlNodeType.Whitespace or
                    XmlNodeType.SignificantWhitespace))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSafeDiscardedSvgGraphic(
        XmlElement root,
        CancellationToken cancellationToken)
    {
        XmlNode current = root;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current is XmlElement element)
            {
                if ((!ReferenceEquals(element, root) &&
                        !element.NamespaceURI.Equals(SvgNamespace, StringComparison.Ordinal)) ||
                    !IsSafeDiscardedSvgGraphicName(element.LocalName))
                {
                    return false;
                }

                foreach (XmlAttribute attribute in element.Attributes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (attribute.NamespaceURI.Equals("http://www.w3.org/2000/xmlns/", StringComparison.Ordinal))
                        continue;
                    if (attribute.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                        attribute.LocalName is "href" or "src" ||
                        attribute.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                if (current.FirstChild is { } firstChild)
                {
                    current = firstChild;
                    continue;
                }
            }
            else if (current.NodeType is not (
                         XmlNodeType.Text or XmlNodeType.CDATA or
                         XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace))
            {
                return false;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ReferenceEquals(current, root))
                    return true;
                if (current.NextSibling is { } nextSibling)
                {
                    current = nextSibling;
                    break;
                }

                current = current.ParentNode!;
            }
        }
    }

    private static bool IsSafeDiscardedSvgGraphicName(string localName) => localName is
        "svg" or "g" or "defs" or "mask" or "path" or "rect" or
        "circle" or "ellipse" or "line" or "polyline" or "polygon" or
        "title" or "desc";

    private static string GetInnerTextIterative(XmlNode root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (root.FirstChild is { NextSibling: null } onlyChild &&
            onlyChild.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or
                XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return onlyChild.Value ?? string.Empty;
        }

        var builder = new StringBuilder();
        XmlNode? current = root.FirstChild;
        while (current is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or
                XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                AppendStringWithCancellation(builder, current.Value, cancellationToken);
            }

            if (current.FirstChild is { } firstChild)
            {
                current = firstChild;
                continue;
            }

            current = AdvanceAfterSubtree(current, root, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return builder.ToString();
    }

    private static XmlNode? AdvanceAfterSubtree(
        XmlNode completedNode,
        XmlNode root,
        CancellationToken cancellationToken)
    {
        XmlNode? current = completedNode;
        while (current is not null && !ReferenceEquals(current, root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.NextSibling is { } nextSibling)
                return nextSibling;
            current = current.ParentNode;
        }

        return null;
    }

    private static void AppendStringWithCancellation(
        StringBuilder builder,
        string? value,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(value))
            return;

        for (int offset = 0; offset < value.Length; offset += CssCancellationCheckInterval)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(CssCancellationCheckInterval, value.Length - offset);
            builder.Append(value, offset, count);
        }
    }

    private static bool IsSafeTextContainer(string localName) =>
        localName.Equals("div", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("section", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("span", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("p", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("br", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("strong", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("b", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("em", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("i", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("small", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("code", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("table", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("thead", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("tbody", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("tfoot", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("tr", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("td", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("th", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("ul", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("ol", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("li", StringComparison.OrdinalIgnoreCase) ||
        localName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6";

    internal static bool HasOnlySafeHtmlTextAttributes(
        XmlElement element,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int charactersUntilCancellationCheck = CssCancellationCheckInterval;
        foreach (XmlAttribute attribute in element.Attributes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attribute.NamespaceURI.Equals("http://www.w3.org/2000/xmlns/", StringComparison.Ordinal))
                continue;
            if (attribute.NamespaceURI.Length != 0)
                return false;

            ReadOnlySpan<char> attributeValue = attribute.Value.AsSpan();
            int valueStart = 0;
            int valueEnd = attributeValue.Length;
            TrimStyleRange(
                attributeValue,
                ref valueStart,
                ref valueEnd,
                ref charactersUntilCancellationCheck,
                cancellationToken);
            ReadOnlySpan<char> value = attributeValue[valueStart..valueEnd];
            if (attribute.LocalName.Equals("class", StringComparison.OrdinalIgnoreCase) ||
                attribute.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsSafeCssToken(value, 512))
                    return false;
                continue;
            }
            if (attribute.LocalName.Equals("align", StringComparison.OrdinalIgnoreCase))
            {
                if (!value.Equals("left", StringComparison.OrdinalIgnoreCase) &&
                    !value.Equals("right", StringComparison.OrdinalIgnoreCase) &&
                    !value.Equals("center", StringComparison.OrdinalIgnoreCase))
                    return false;
                continue;
            }
            if (attribute.LocalName.Equals("colspan", StringComparison.OrdinalIgnoreCase) ||
                attribute.LocalName.Equals("rowspan", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int span) ||
                    span is < 1 or > 64)
                {
                    return false;
                }
                continue;
            }
            if (attribute.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase) &&
                IsSafeDiscardedHtmlTextStyle(value, cancellationToken, ref charactersUntilCancellationCheck))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    internal static bool IsSafeDiscardedHtmlTextStyle(
        ReadOnlySpan<char> value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int charactersUntilCancellationCheck = CssCancellationCheckInterval;
        return IsSafeDiscardedHtmlTextStyle(value, cancellationToken, ref charactersUntilCancellationCheck);
    }

    private static bool IsSafeDiscardedHtmlTextStyle(
        ReadOnlySpan<char> value,
        CancellationToken cancellationToken,
        ref int charactersUntilCancellationCheck)
    {
        if (!IsSafeCssToken(value, 1024) ||
            ContainsHtmlStyleForbiddenCharacter(value, cancellationToken, ref charactersUntilCancellationCheck))
        {
            return false;
        }

        int declarationCount = 0;
        uint seenProperties = 0;
        int declarationStart = 0;
        while (declarationStart <= value.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int declarationEnd = declarationStart;
            int separator = -1;
            while (declarationEnd < value.Length && value[declarationEnd] != ';')
            {
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                if (separator < 0 && value[declarationEnd] == ':')
                    separator = declarationEnd;
                declarationEnd++;
            }

            if (declarationEnd > declarationStart)
            {
                if (separator <= declarationStart)
                    return false;

                int nameStart = declarationStart;
                int nameEnd = separator;
                TrimStyleRange(value, ref nameStart, ref nameEnd,
                    ref charactersUntilCancellationCheck, cancellationToken);
                int valueStart = separator + 1;
                int valueEnd = declarationEnd;
                TrimStyleRange(value, ref valueStart, ref valueEnd,
                    ref charactersUntilCancellationCheck, cancellationToken);
                if (nameStart == nameEnd || valueStart == valueEnd)
                    return false;

                int propertyIndex = GetSafeHtmlStyleIndex(value[nameStart..nameEnd]);
                if (propertyIndex < 0)
                    return false;

                uint propertyMask = 1u << propertyIndex;
                if ((seenProperties & propertyMask) != 0 ||
                    !IsSafeCssToken(value[valueStart..valueEnd], 256))
                {
                    return false;
                }

                seenProperties |= propertyMask;
                declarationCount++;
            }

            if (declarationEnd == value.Length)
                break;
            declarationStart = declarationEnd + 1;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return declarationCount > 0;
    }

    internal static Dictionary<string, string> ParseStyle(string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Span<int> valueStarts = stackalloc int[8];
        Span<int> valueLengths = stackalloc int[8];
        uint foundProperties = 0;
        bool hasUnsupportedProperty = false;
        int charactersUntilCancellationCheck = CssCancellationCheckInterval;
        ReadOnlySpan<char> source = value.AsSpan();
        int declarationStart = 0;
        while (declarationStart <= source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int declarationEnd = declarationStart;
            int separator = -1;
            while (declarationEnd < source.Length && source[declarationEnd] != ';')
            {
                CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                if (separator < 0 && source[declarationEnd] == ':')
                    separator = declarationEnd;
                declarationEnd++;
            }

            if (declarationEnd > declarationStart && separator > declarationStart)
            {
                int nameStart = declarationStart;
                int nameEnd = separator;
                TrimStyleRange(source, ref nameStart, ref nameEnd,
                    ref charactersUntilCancellationCheck, cancellationToken);
                int propertyValueStart = separator + 1;
                int propertyValueEnd = declarationEnd;
                TrimStyleRange(source, ref propertyValueStart, ref propertyValueEnd,
                    ref charactersUntilCancellationCheck, cancellationToken);

                if (nameStart < nameEnd && propertyValueStart < propertyValueEnd)
                {
                    int propertyIndex = GetSupportedTextStyleIndex(source[nameStart..nameEnd]);
                    if (propertyIndex < 0)
                    {
                        // Every unsupported text property rejects conversion.
                        // One static marker preserves that signal without
                        // allocating names or values for attacker-sized lists.
                        hasUnsupportedProperty = true;
                    }
                    else
                    {
                        valueStarts[propertyIndex] = propertyValueStart;
                        valueLengths[propertyIndex] = propertyValueEnd - propertyValueStart;
                        foundProperties |= 1u << propertyIndex;
                    }
                }
            }

            if (declarationEnd == source.Length)
                break;
            declarationStart = declarationEnd + 1;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (hasUnsupportedProperty)
        {
            // The only caller rejects as soon as it observes an unsupported
            // key, so do not materialize even the final supported values for
            // an input that cannot be admitted for replacement.
            properties.Add("\u0001unsupported-text-style", string.Empty);
            return properties;
        }

        for (int propertyIndex = 0; propertyIndex < 8; propertyIndex++)
        {
            if ((foundProperties & (1u << propertyIndex)) == 0)
                continue;
            cancellationToken.ThrowIfCancellationRequested();
            properties.Add(
                GetSupportedTextStyleName(propertyIndex),
                source.Slice(valueStarts[propertyIndex], valueLengths[propertyIndex]).ToString());
        }
        return properties;
    }

    private static void TrimStyleRange(
        ReadOnlySpan<char> value,
        ref int start,
        ref int end,
        ref int charactersUntilCancellationCheck,
        CancellationToken cancellationToken)
    {
        while (start < end && char.IsWhiteSpace(value[start]))
        {
            CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            start++;
        }
        while (end > start && char.IsWhiteSpace(value[end - 1]))
        {
            CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            end--;
        }
    }

    private static bool ContainsHtmlStyleForbiddenCharacter(
        ReadOnlySpan<char> value,
        CancellationToken cancellationToken,
        ref int charactersUntilCancellationCheck)
    {
        for (int index = 0; index < value.Length; index++)
        {
            CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
            if (value[index] is '{' or '}' or '@')
                return true;
        }
        return false;
    }

    private static int GetSupportedTextStyleIndex(ReadOnlySpan<char> name)
    {
        if (name.Equals("font-size", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Equals("color", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Equals("font-family", StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.Equals("font-weight", StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.Equals("font-style", StringComparison.OrdinalIgnoreCase)) return 4;
        if (name.Equals("text-align", StringComparison.OrdinalIgnoreCase)) return 5;
        if (name.Equals("letter-spacing", StringComparison.OrdinalIgnoreCase)) return 6;
        if (name.Equals("line-height", StringComparison.OrdinalIgnoreCase)) return 7;
        return -1;
    }

    private static string GetSupportedTextStyleName(int propertyIndex) => propertyIndex switch
    {
        0 => "font-size",
        1 => "color",
        2 => "font-family",
        3 => "font-weight",
        4 => "font-style",
        5 => "text-align",
        6 => "letter-spacing",
        7 => "line-height",
        _ => throw new ArgumentOutOfRangeException(nameof(propertyIndex)),
    };

    private static int GetSafeHtmlStyleIndex(ReadOnlySpan<char> name)
    {
        if (name.Equals("width", StringComparison.Ordinal)) return 0;
        if (name.Equals("white-space", StringComparison.Ordinal)) return 1;
        if (name.Equals("margin", StringComparison.Ordinal)) return 2;
        if (name.Equals("font-size", StringComparison.Ordinal)) return 3;
        if (name.Equals("color", StringComparison.Ordinal)) return 4;
        if (name.Equals("font-family", StringComparison.Ordinal)) return 5;
        if (name.Equals("font-weight", StringComparison.Ordinal)) return 6;
        if (name.Equals("font-style", StringComparison.Ordinal)) return 7;
        if (name.Equals("text-align", StringComparison.Ordinal)) return 8;
        if (name.Equals("letter-spacing", StringComparison.Ordinal)) return 9;
        if (name.Equals("line-height", StringComparison.Ordinal)) return 10;
        return -1;
    }

    private static bool TryReadLength(string value, double defaultValue, out double result)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            result = defaultValue;
            return double.IsFinite(result);
        }
        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase) ||
            value.EndsWith("em", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^2].Trim();
        }
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
            double.IsFinite(result);
    }

    private static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool IsSafeCssToken(string value, int maximumLength) =>
        value.Length <= maximumLength &&
        value.IndexOf("url", StringComparison.OrdinalIgnoreCase) < 0 &&
        value.IndexOfAny(['<', '>', '\r', '\n', '\0']) < 0;

    private static bool IsSafeCssToken(ReadOnlySpan<char> value, int maximumLength)
    {
        if (value.Length > maximumLength ||
            value.IndexOf("url", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (character is '<' or '>' or '\r' or '\n' or '\0')
                return false;
        }
        return true;
    }

    private sealed class CollapsedTextAccumulator(CancellationToken cancellationToken)
    {
        private const int MaximumLength = 16_384;
        private readonly StringBuilder _builder = new(capacity: 256);
        private bool _pendingSpace;
        private int _charactersUntilCancellationCheck = CssCancellationCheckInterval;

        internal bool IsTooLong { get; private set; }

        internal bool Append(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return true;

            foreach (char character in value)
            {
                CheckCancellation();
                if (char.IsWhiteSpace(character))
                {
                    _pendingSpace = _builder.Length > 0;
                    continue;
                }

                int additionalLength = 1 + (_pendingSpace ? 1 : 0);
                if (_builder.Length > MaximumLength - additionalLength)
                {
                    IsTooLong = true;
                    return false;
                }

                if (_pendingSpace)
                {
                    _builder.Append(' ');
                    _pendingSpace = false;
                }
                _builder.Append(character);
            }

            return true;
        }

        internal bool AppendBoundary() => Append(" ");

        internal void ThrowIfCancellationRequested()
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        public override string ToString()
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _builder.ToString();
        }

        private void CheckCancellation()
        {
            if (--_charactersUntilCancellationCheck == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _charactersUntilCancellationCheck = CssCancellationCheckInterval;
            }
        }
    }

    private static bool IsAnimationElement(XmlElement element)
    {
        if (!string.IsNullOrEmpty(element.NamespaceURI) &&
            !element.NamespaceURI.Equals(SvgNamespace, StringComparison.Ordinal))
        {
            return false;
        }

        return element.LocalName.Equals("animate", StringComparison.OrdinalIgnoreCase) ||
            element.LocalName.Equals("animateColor", StringComparison.OrdinalIgnoreCase) ||
            element.LocalName.Equals("animateMotion", StringComparison.OrdinalIgnoreCase) ||
            element.LocalName.Equals("animateTransform", StringComparison.OrdinalIgnoreCase) ||
            element.LocalName.Equals("set", StringComparison.OrdinalIgnoreCase) ||
            element.LocalName.Equals("discard", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyInitialValue(XmlElement animation, CancellationToken cancellationToken)
    {
        if (animation.ParentNode is not XmlElement target || !BeginsImmediately(animation))
            return;

        string localName = animation.LocalName;
        if (localName.Equals("animateMotion", StringComparison.OrdinalIgnoreCase) ||
            localName.Equals("discard", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? initialValue = GetInitialValue(animation, cancellationToken);
        if (string.IsNullOrWhiteSpace(initialValue))
            return;

        if (localName.Equals("animateTransform", StringComparison.OrdinalIgnoreCase))
        {
            string type = animation.GetAttribute("type").Trim();
            if (!IsSupportedTransformType(type))
                return;

            string transform = $"{type}({initialValue.Trim()})";
            if (animation.GetAttribute("additive").Equals("sum", StringComparison.OrdinalIgnoreCase) &&
                target.HasAttribute("transform"))
            {
                transform = $"{target.GetAttribute("transform").Trim()} {transform}";
            }

            target.SetAttribute("transform", transform);
            return;
        }

        string attributeName = animation.GetAttribute("attributeName").Trim();
        if (attributeName.Length == 0 ||
            attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
            attributeName.IndexOf(':') >= 0)
        {
            return;
        }

        target.SetAttribute(attributeName, initialValue.Trim());
    }

    internal static string? GetInitialValue(XmlElement animation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string values = animation.GetAttribute("values");
        if (!string.IsNullOrWhiteSpace(values))
        {
            // Enumerate ranges rather than materializing every unused value.
            // The legacy Split contract includes leading/trailing empty tokens.
            var candidates = values.AsSpan().Split(';');
            candidates.MoveNext();
            Range selected = candidates.Current;

            string keyTimes = animation.GetAttribute("keyTimes");
            if (!string.IsNullOrWhiteSpace(keyTimes))
            {
                var times = keyTimes.AsSpan().Split(';');
                int index = 0;
                do
                {
                    if ((index++ & 0xFF) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    if (!times.MoveNext())
                        break;
                    if (!double.TryParse(
                            keyTimes.AsSpan()[times.Current].Trim(),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double keyTime) ||
                        keyTime > 0)
                    {
                        break;
                    }

                    // Multiple values at keyTime zero represent an immediate
                    // transition. The final zero-time value is the visible
                    // first frame (common in rotating README badge artwork).
                    selected = candidates.Current;
                }
                while (candidates.MoveNext());
            }

            cancellationToken.ThrowIfCancellationRequested();
            return values[selected];
        }

        string from = animation.GetAttribute("from");
        if (!string.IsNullOrWhiteSpace(from))
            return from;

        return animation.LocalName.Equals("set", StringComparison.OrdinalIgnoreCase)
            ? animation.GetAttribute("to")
            : null;
    }

    private static bool BeginsImmediately(XmlElement animation)
    {
        string begin = animation.GetAttribute("begin").Trim();
        if (begin.Length == 0)
            return true;

        int separator = begin.IndexOf(';');
        if (separator >= 0)
            begin = begin[..separator].Trim();
        if (begin.Equals("indefinite", StringComparison.OrdinalIgnoreCase) ||
            begin.IndexOfAny(['.', '+']) >= 0)
        {
            return false;
        }

        if (begin.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            begin = begin[..^2];
        else if (begin.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            begin = begin[..^1];

        return double.TryParse(
                begin,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double start) &&
            start <= 0;
    }

    private static bool IsSupportedTransformType(string value) =>
        value.Equals("translate", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("scale", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("rotate", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("skewX", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("skewY", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("matrix", StringComparison.OrdinalIgnoreCase);

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

    internal sealed class CancellationAwareReadStream : Stream
    {
        private byte[] _source;
        private readonly CancellationToken _cancellationToken;
        private int _position;
        private bool _disposed;

        internal CancellationAwareReadStream(byte[] source, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);
            _source = source;
            _cancellationToken = cancellationToken;
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                ThrowIfDisposed();
                return _source.Length;
            }
        }

        public override long Position
        {
            get
            {
                ThrowIfDisposed();
                return _position;
            }
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count)
                throw new ArgumentException("The buffer range is invalid.");
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            ThrowIfAvailable();
            if (_position >= _source.Length || buffer.IsEmpty)
                return 0;

            int count = Math.Min(buffer.Length, _source.Length - _position);
            _source.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int ReadByte()
        {
            ThrowIfAvailable();
            return _position < _source.Length ? _source[_position++] : -1;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override void Flush() => ThrowIfAvailable();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                _source = Array.Empty<byte>();
                _position = 0;
            }
            base.Dispose(disposing);
        }

        private void ThrowIfAvailable()
        {
            ThrowIfDisposed();
            _cancellationToken.ThrowIfCancellationRequested();
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    }

    internal sealed class BoundedSegmentedWriteStream : Stream
    {
        private const int SegmentSize = 16 * 1024;

        private readonly int _maximumLength;
        private readonly CancellationToken _cancellationToken;
        private readonly List<byte[]> _segments = [];
        private byte[]? _currentSegment;
        private int _currentOffset;
        private int _length;
        private int _allocatedLength;
        private bool _disposed;

        internal BoundedSegmentedWriteStream(int maximumLength, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
            if (maximumLength > SvgResourceBudget.MaxInputBytes)
                throw new ArgumentOutOfRangeException(nameof(maximumLength));
            _maximumLength = maximumLength;
            _cancellationToken = cancellationToken;
        }

        internal bool LimitExceeded { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed;

        public override long Length
        {
            get
            {
                ThrowIfDisposed();
                return _length;
            }
        }

        public override long Position
        {
            get => Length;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count)
                throw new ArgumentException("The buffer range is invalid.");
            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ThrowIfAvailable();
            if (buffer.Length > _maximumLength - _length)
            {
                LimitExceeded = true;
                throw new SvgStaticSnapshotResourceException();
            }

            while (!buffer.IsEmpty)
            {
                ThrowIfAvailable();
                if (_currentSegment is null || _currentOffset == _currentSegment.Length)
                    AllocateNextSegment();

                int count = Math.Min(buffer.Length, _currentSegment!.Length - _currentOffset);
                buffer[..count].CopyTo(_currentSegment.AsSpan(_currentOffset, count));
                buffer = buffer[count..];
                _currentOffset += count;
                _length += count;
            }
        }

        public override void WriteByte(byte value)
        {
            Span<byte> single = stackalloc byte[1];
            single[0] = value;
            Write(single);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override void Flush() => ThrowIfAvailable();

        internal byte[] ToArray()
        {
            ThrowIfAvailable();
            byte[] result = new byte[_length];
            try
            {
                int destinationOffset = 0;
                foreach (byte[] segment in _segments)
                {
                    ThrowIfAvailable();
                    int count = Math.Min(segment.Length, _length - destinationOffset);
                    if (count <= 0)
                        break;
                    segment.AsSpan(0, count).CopyTo(result.AsSpan(destinationOffset, count));
                    destinationOffset += count;
                }
                ThrowIfAvailable();
                return result;
            }
            catch
            {
                Array.Clear(result, 0, result.Length);
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                foreach (byte[] segment in _segments)
                    Array.Clear(segment);
                _segments.Clear();
                _currentSegment = null;
                _currentOffset = 0;
                _allocatedLength = 0;
                _length = 0;
            }
            base.Dispose(disposing);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void AllocateNextSegment()
        {
            int remaining = _maximumLength - _allocatedLength;
            if (remaining <= 0)
            {
                LimitExceeded = true;
                throw new SvgStaticSnapshotResourceException();
            }

            int size = Math.Min(SegmentSize, remaining);
            _currentSegment = new byte[size];
            _segments.Add(_currentSegment);
            _allocatedLength += size;
            _currentOffset = 0;
        }

        private void ThrowIfAvailable()
        {
            ThrowIfDisposed();
            _cancellationToken.ThrowIfCancellationRequested();
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal sealed class SvgStaticSnapshotResourceException : Exception
{
    internal const string OutputBytesReason = "static-snapshot-output-bytes";
    internal const string ElementDepthReason = "element-depth";
    internal const string ElementCountReason = "element-count";
    internal const string AttributeCountReason = "attribute-count";

    internal SvgStaticSnapshotResourceException()
        : base("The static SVG snapshot exceeds the configured source-byte ceiling.")
    {
        Reason = OutputBytesReason;
    }

    internal SvgStaticSnapshotResourceException(string reason)
        : base("The static SVG snapshot exceeds a configured structural resource ceiling.")
    {
        Reason = reason;
    }

    internal string Reason { get; }
}
