using System;
using System.IO;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Exercises the logical UIA document-generation seam. Source assertions below
/// verify wiring only; they are not a live WinUI/UIA control integration test.
/// </summary>
public sealed class MarkdownAutomationDocumentGenerationTests
{
    [Fact]
    public void EquivalentSemanticInputsKeepGenerationAcrossLayoutOnlyChanges()
    {
        object explicitDocument = new();
        object engine = new();
        object parserRegistry = new();
        object presentationRegistry = new();
        object extensionRegistry = new();
        object stringProvider = new();
        var generations = new MarkdownAutomationDocumentGeneration();

        long initial = generations.Publish(CreateIdentity(
            "# Same source", explicitDocument, engine, parserRegistry, 4,
            presentationRegistry, 9, extensionRegistry, 3, stringProvider, "en-US"));

        // Width/theme/snapshot revision are intentionally not inputs to this
        // key, so resizing and restyling reuse the same logical generation.
        long relayout = generations.Publish(CreateIdentity(
            "# Same source", explicitDocument, engine, parserRegistry, 4,
            presentationRegistry, 9, extensionRegistry, 3, stringProvider, "en-us"));

        Assert.Equal(1, initial);
        Assert.Equal(initial, relayout);
    }

    [Fact]
    public void ExactSourceWithANewExplicitDocumentIdentityGetsANewGeneration()
    {
        var generations = new MarkdownAutomationDocumentGeneration();
        object firstDocument = new();
        object secondDocument = new();
        MarkdownAutomationDocumentIdentity first = CreateIdentity(
            "same bytes", firstDocument, engine: null, parserRegistry: null, parserRevision: 0,
            presentationRegistry: null, presentationRevision: 0, extensionRegistry: null,
            extensionRevision: 0, stringProvider: null, culture: "en-US");
        MarkdownAutomationDocumentIdentity replacement = CreateIdentity(
            "same bytes", secondDocument, engine: null, parserRegistry: null, parserRevision: 0,
            presentationRegistry: null, presentationRevision: 0, extensionRegistry: null,
            extensionRevision: 0, stringProvider: null, culture: "en-US");

        Assert.Equal(1, generations.Publish(first));
        Assert.Equal(2, generations.Publish(replacement));
    }

    [Fact]
    public void DeferredSceneSemanticProjectionChangesGenerationWithoutChangingSourceInputs()
    {
        var generations = new MarkdownAutomationDocumentGeneration();
        object engine = new();
        object parserRegistry = new();
        object presentationRegistry = new();
        object extensionRegistry = new();
        object provider = new();
        const string source = "$$ x^2 $$";

        MarkdownAutomationDocumentIdentity fallback = CreateIdentity(
            source, explicitDocument: null, engine, parserRegistry, 1,
            presentationRegistry, 1, extensionRegistry, 1, provider, "en-US",
            semanticText: "Math x^2.");
        MarkdownAutomationDocumentIdentity materialized = CreateIdentity(
            source, explicitDocument: null, engine, parserRegistry, 1,
            presentationRegistry, 1, extensionRegistry, 1, provider, "en-US",
            semanticText: "Math y^2.");

        Assert.Equal(1, generations.Publish(fallback));
        Assert.Equal(2, generations.Publish(materialized));
        Assert.Equal(2, generations.Publish(CreateIdentity(
            source, explicitDocument: null, engine, parserRegistry, 1,
            presentationRegistry, 1, extensionRegistry, 1, provider, "en-US",
            semanticText: "Math y^2.")));
    }

    [Fact]
    public void SemanticNodeAndRangeProjectionChangesGenerationWhenTextIsUnchanged()
    {
        var generations = new MarkdownAutomationDocumentGeneration();
        object engine = new();
        object parserRegistry = new();
        object presentationRegistry = new();
        object extensionRegistry = new();
        object provider = new();
        const string source = "linked";

        MarkdownLayoutContext firstContext = CreateContext(source);
        var firstParagraph = new InlineContainerBox(firstContext, MarkdownElementKeys.Body)
        {
            BlockIndex = 10,
        };
        firstParagraph.Add(new LinkRun(source, "https://example.test")
        {
            SourceSpan = new SourceSpan(0, source.Length),
        });
        using var firstSnapshot = new LayoutSnapshot(
            new BlockBox[] { firstParagraph }, firstContext.SourceMap, 200, 40);

        MarkdownLayoutContext secondContext = CreateContext(source);
        var secondParagraph = new InlineContainerBox(secondContext, MarkdownElementKeys.Body)
        {
            BlockIndex = 11,
        };
        secondParagraph.Add(new LinkRun(source, "https://example.test")
        {
            SourceSpan = new SourceSpan(0, source.Length),
        });
        using var secondSnapshot = new LayoutSnapshot(
            new BlockBox[] { secondParagraph }, secondContext.SourceMap, 400, 80);

        MarkdownAutomationDocumentIdentity firstIdentity = CreateIdentity(
            firstSnapshot.SourceFingerprint,
            firstSnapshot.SemanticDocument.TextProjectionFingerprint,
            firstSnapshot.SemanticDocument.SemanticRangeFingerprint,
            explicitDocument: null,
            engine,
            parserRegistry,
            parserRevision: 1,
            presentationRegistry,
            presentationRevision: 1,
            extensionRegistry,
            extensionRevision: 1,
            provider,
            culture: "en-US");
        MarkdownAutomationDocumentIdentity secondIdentity = CreateIdentity(
            secondSnapshot.SourceFingerprint,
            secondSnapshot.SemanticDocument.TextProjectionFingerprint,
            secondSnapshot.SemanticDocument.SemanticRangeFingerprint,
            explicitDocument: null,
            engine,
            parserRegistry,
            parserRevision: 1,
            presentationRegistry,
            presentationRevision: 1,
            extensionRegistry,
            extensionRevision: 1,
            provider,
            culture: "en-US");

        Assert.Equal(
            firstSnapshot.SemanticDocument.TextProjectionFingerprint,
            secondSnapshot.SemanticDocument.TextProjectionFingerprint);
        Assert.NotEqual(
            firstSnapshot.SemanticDocument.SemanticRangeFingerprint,
            secondSnapshot.SemanticDocument.SemanticRangeFingerprint);
        Assert.Equal(1, generations.Publish(firstIdentity));
        Assert.Equal(2, generations.Publish(secondIdentity));
    }

    [Fact]
    public void TextFingerprintsCompareFixedSizeValuesAndDistinguishDifferentProjections()
    {
        MarkdownAutomationTextFingerprint same = MarkdownAutomationTextFingerprint.Compute("same text");
        MarkdownAutomationTextFingerprint sameAgain = MarkdownAutomationTextFingerprint.Compute("same text");
        MarkdownAutomationTextFingerprint different = MarkdownAutomationTextFingerprint.Compute("diff text");

        Assert.Equal(same, sameAgain);
        Assert.NotEqual(same, different);
        Assert.Equal("same text".Length, same.Length);
    }

    [Fact]
    public void ParserCultureProviderAndRegistryChangesInvalidateTheLogicalDocument()
    {
        var generations = new MarkdownAutomationDocumentGeneration();
        object engine = new();
        object parserRegistry = new();
        object presentationRegistry = new();
        object extensionRegistry = new();
        object provider = new();
        MarkdownAutomationDocumentIdentity baseline = CreateIdentity(
            "text", explicitDocument: null, engine, parserRegistry, 2,
            presentationRegistry, 7, extensionRegistry, 3, provider, "en-US");

        Assert.Equal(1, generations.Publish(baseline));
        Assert.Equal(2, generations.Publish(CreateIdentity(
            "text", explicitDocument: null, engine, parserRegistry, 3,
            presentationRegistry, 7, extensionRegistry, 3, provider, "en-US")));
        Assert.Equal(3, generations.Publish(CreateIdentity(
            "text", explicitDocument: null, engine, parserRegistry, 3,
            presentationRegistry, 7, extensionRegistry, 3, new object(), "en-US")));
        Assert.Equal(4, generations.Publish(CreateIdentity(
            "text", explicitDocument: null, engine: null, parserRegistry, 3,
            presentationRegistry, 7, extensionRegistry, 3, provider, "fr-FR")));
        Assert.Equal(5, generations.Publish(CreateIdentity(
            "text", explicitDocument: null, engine: new object(), parserRegistry, 3,
            presentationRegistry, 7, extensionRegistry, 3, provider, "fr-FR")));
    }

    [Fact]
    public void UnpublishStartsANewEpochAndStaleRangeGenerationIsNotAvailable()
    {
        var generations = new MarkdownAutomationDocumentGeneration();
        MarkdownAutomationDocumentIdentity identity = CreateIdentity(
            "text", explicitDocument: null, engine: null, parserRegistry: null, parserRevision: 0,
            presentationRegistry: null, presentationRevision: 0, extensionRegistry: null,
            extensionRevision: 0, stringProvider: null, culture: "en-US");

        long beforeUnload = generations.Publish(identity);
        MarkdownAutomationDocumentGeneration.EnsureCurrent(beforeUnload, beforeUnload);
        generations.Invalidate();
        long afterReload = generations.Publish(identity);

        Assert.True(afterReload > beforeUnload);
        Assert.Throws<ElementNotAvailableException>(
            () => MarkdownAutomationDocumentGeneration.EnsureCurrent(beforeUnload, afterReload));
        MarkdownAutomationDocumentGeneration.EnsureCurrent(afterReload, afterReload);
        Assert.Throws<ElementNotAvailableException>(
            () => MarkdownAutomationDocumentGeneration.EnsureCurrent(afterReload, 0));
    }

    [Fact]
    public void SnapshotAcceptsExactlyOnePositiveAutomationGeneration()
    {
        using var snapshot = new LayoutSnapshot(
            Array.Empty<BlockBox>(),
            new MarkdownSourceMap(string.Empty),
            width: 100,
            height: 0);

        Assert.Equal(0, snapshot.AutomationDocumentGeneration);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => snapshot.AssignAutomationDocumentGeneration(0));
        snapshot.AssignAutomationDocumentGeneration(8);
        Assert.Equal(8, snapshot.AutomationDocumentGeneration);
        Assert.Throws<InvalidOperationException>(
            () => snapshot.AssignAutomationDocumentGeneration(9));
        Assert.Equal(8, snapshot.AutomationDocumentGeneration);
    }

    [Fact]
    public void ExactBlockScopeResolvesToTheCurrentLayoutWithoutRetainingTheOldBox()
    {
        const string text = "linked";
        MarkdownLayoutContext firstContext = CreateContext(text);
        var firstParagraph = new InlineContainerBox(firstContext, MarkdownElementKeys.Body)
        {
            BlockIndex = firstContext.NextBlockIndex(),
        };
        firstParagraph.Add(new LinkRun(text, "https://example.test")
        {
            SourceSpan = new SourceSpan(0, text.Length),
        });
        using var firstSnapshot = new LayoutSnapshot(
            new BlockBox[] { firstParagraph }, firstContext.SourceMap, 200, 40);
        int start = 0;
        int end = firstSnapshot.SemanticDocument.Text.Length;
        int exactScopeBlockIndex = firstParagraph.BlockIndex;

        MarkdownLayoutContext secondContext = CreateContext(text);
        var secondParagraph = new InlineContainerBox(secondContext, MarkdownElementKeys.Body)
        {
            BlockIndex = secondContext.NextBlockIndex(),
        };
        secondParagraph.Add(new LinkRun(text, "https://example.test")
        {
            SourceSpan = new SourceSpan(0, text.Length),
        });
        using var secondSnapshot = new LayoutSnapshot(
            new BlockBox[] { secondParagraph }, secondContext.SourceMap, 400, 80);

        Assert.Equal(
            firstSnapshot.SemanticDocument.SemanticRangeFingerprint,
            secondSnapshot.SemanticDocument.SemanticRangeFingerprint);
        MarkdownSemanticNode paragraph = secondSnapshot.SemanticDocument
            .GetEnclosingNodeForTextRangeByBlockIndex(start, end, exactScopeBlockIndex);

        Assert.Equal(MarkdownSemanticRole.Paragraph, paragraph.Role);
        Assert.Same(secondParagraph, paragraph.InlineBox);
        Assert.NotSame(firstParagraph, paragraph.InlineBox);
    }

    [Fact]
    public void SemanticRangeFingerprintChangesWhenInlineScopeMappingChanges()
    {
        const string text = "same text";
        MarkdownLayoutContext firstContext = CreateContext(text);
        var firstParagraph = new InlineContainerBox(firstContext, MarkdownElementKeys.Body)
        {
            BlockIndex = 10,
        };
        firstParagraph.Add(new LinkRun(text, "https://example.test")
        {
            SourceSpan = new SourceSpan(0, text.Length),
        });
        using var firstSnapshot = new LayoutSnapshot(
            new BlockBox[] { firstParagraph }, firstContext.SourceMap, 200, 40);

        MarkdownLayoutContext secondContext = CreateContext(text);
        var secondParagraph = new InlineContainerBox(secondContext, MarkdownElementKeys.Body)
        {
            BlockIndex = 11,
        };
        secondParagraph.Add(new LinkRun(text, "https://example.test")
        {
            SourceSpan = new SourceSpan(0, text.Length),
        });
        using var secondSnapshot = new LayoutSnapshot(
            new BlockBox[] { secondParagraph }, secondContext.SourceMap, 400, 80);

        Assert.Equal(
            firstSnapshot.SemanticDocument.TextProjectionFingerprint,
            secondSnapshot.SemanticDocument.TextProjectionFingerprint);
        Assert.NotEqual(
            firstSnapshot.SemanticDocument.SemanticRangeFingerprint,
            secondSnapshot.SemanticDocument.SemanticRangeFingerprint);
    }

    [Fact]
    public void ControlStampsBeforeSnapshotPublicationAndUnpublishingInvalidatesIdentity()
    {
        string control = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs");
        int stamp = control.IndexOf("snapshot.AssignAutomationDocumentGeneration(", StringComparison.Ordinal);
        int publish = control.IndexOf("_snapshot = snapshot;", stamp, StringComparison.Ordinal);
        Assert.True(stamp >= 0 && publish > stamp);
        Assert.Equal(2, CountOccurrences(control, "_automationDocumentGenerations.Invalidate();"));

        string peer = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownAutomationPeer.cs");
        Assert.Contains("MarkdownAutomationDocumentGeneration.EnsureCurrent(", peer, StringComparison.Ordinal);
        Assert.Contains("snapshot?.AutomationDocumentGeneration ?? _owner.CurrentAutomationDocumentGeneration", peer, StringComparison.Ordinal);

        string provider = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Accessibility", "MarkdownTextRangeProvider.cs");
        Assert.Contains("private readonly long _documentGeneration;", provider, StringComparison.Ordinal);
        Assert.Contains("private readonly int? _exactRangeScopeBlockIndex;", provider, StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(MarkdownTextRangeProvider).GetFields(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic),
            field => field.FieldType == typeof(InlineContainerBox));
        Assert.Contains("_peer.GetTextRangeDocument(_documentGeneration)", provider, StringComparison.Ordinal);
        Assert.Contains("other.GetCurrentDocument();", provider, StringComparison.Ordinal);
        Assert.Contains("GetEnclosingNodeForTextRangeByBlockIndex(", provider, StringComparison.Ordinal);
        Assert.Contains("old.SourceFingerprint.Equals(snapshot.SourceFingerprint)", control, StringComparison.Ordinal);
        Assert.Contains("snapshot.SemanticDocument.SemanticRangeFingerprint", control, StringComparison.Ordinal);
        Assert.DoesNotContain("old.SourceMap.SourceText == snapshot.SourceMap.SourceText", control, StringComparison.Ordinal);
    }

    private static MarkdownLayoutContext CreateContext(string source)
    {
        var style = new ElementStyle();
        var styles = new System.Collections.Generic.Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.Link] = style,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new System.Collections.Generic.Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(source),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
    }

    private static MarkdownAutomationDocumentIdentity CreateIdentity(
        string source,
        object? explicitDocument,
        object? engine,
        object? parserRegistry,
        int parserRevision,
        object? presentationRegistry,
        int presentationRevision,
        object? extensionRegistry,
        int extensionRevision,
        object? stringProvider,
        string culture,
        string? semanticText = null,
        string? semanticRangeProjection = null) =>
        CreateIdentity(
            MarkdownAutomationTextFingerprint.Compute(source),
            MarkdownAutomationTextFingerprint.Compute(semanticText ?? source),
            MarkdownAutomationTextFingerprint.Compute(semanticRangeProjection ?? string.Empty),
            explicitDocument,
            engine,
            parserRegistry,
            parserRevision,
            presentationRegistry,
            presentationRevision,
            extensionRegistry,
            extensionRevision,
            stringProvider,
            culture);

    private static MarkdownAutomationDocumentIdentity CreateIdentity(
        MarkdownAutomationTextFingerprint sourceFingerprint,
        MarkdownAutomationTextFingerprint semanticTextFingerprint,
        MarkdownAutomationTextFingerprint semanticRangeFingerprint,
        object? explicitDocument,
        object? engine,
        object? parserRegistry,
        int parserRevision,
        object? presentationRegistry,
        int presentationRevision,
        object? extensionRegistry,
        int extensionRevision,
        object? stringProvider,
        string culture) =>
        new(
            sourceFingerprint,
            semanticTextFingerprint,
            semanticRangeFingerprint,
            explicitDocument,
            engine,
            parserRegistry,
            parserRevision,
            presentationRegistry,
            presentationRevision,
            extensionRegistry,
            extensionRevision,
            stringProvider,
            culture);

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string ReadRepositorySource(params string[] pathParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string path = directory.FullName;
            foreach (string part in pathParts)
                path = Path.Combine(path, part);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        throw new FileNotFoundException(
            $"Could not locate repository source '{string.Join(Path.DirectorySeparatorChar, pathParts)}'.");
    }
}
