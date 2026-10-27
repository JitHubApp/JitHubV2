using System;
using System.Collections.Generic;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Markdig.Syntax;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class CompactSemanticNodeStorageTests
{
    [Fact]
    public void TypedOwnersStayNullForCompatiblePrimaryBoxAndPreserveDirectSecondaryAndMultipleOwners()
    {
        MarkdownLayoutContext context = CreateContext();
        var inline = new InlineContainerBox(context, MarkdownElementKeys.Body);
        var image = new ImageBox(context, "https://example.test/image.png", "image");
        var embed = new EmbedBox(Markdig.Markdown.Parse("embed")[0], StubEmbedFactory.Instance);
        var table = new TableBox(
            context,
            Array.Empty<InlineContainerBox[]>(),
            Array.Empty<InlineContainerBox[]>());
        DeclarativeHostedElementBox hosted = CreateHostedBox(context);
        VectorSceneBox vector = CreateVectorBox(context);

        try
        {
            Assert.Same(inline, new MarkdownSemanticNode(MarkdownSemanticRole.Group) { InlineBox = inline }.InlineBox);
            Assert.Same(image, new MarkdownSemanticNode(MarkdownSemanticRole.Group) { ImageBox = image }.ImageBox);
            Assert.Same(embed, new MarkdownSemanticNode(MarkdownSemanticRole.Group) { EmbedBox = embed }.EmbedBox);
            Assert.Same(hosted, new MarkdownSemanticNode(MarkdownSemanticRole.Group) { HostedElementBox = hosted }.HostedElementBox);
            Assert.Same(vector, new MarkdownSemanticNode(MarkdownSemanticRole.Group) { VectorSceneBox = vector }.VectorSceneBox);
            Assert.Same(table, new MarkdownSemanticNode(MarkdownSemanticRole.Group) { TableBox = table }.TableBox);

            var primaryBoxOnly = new MarkdownSemanticNode(MarkdownSemanticRole.Group, inline);
            Assert.Same(inline, primaryBoxOnly.Box);
            Assert.Null(primaryBoxOnly.InlineBox);
            Assert.Null(primaryBoxOnly.ImageBox);
            Assert.Null(primaryBoxOnly.TableBox);

            var secondaryOwner = new MarkdownSemanticNode(MarkdownSemanticRole.Group, new StackBox())
            {
                InlineBox = inline,
            };
            Assert.Same(inline, secondaryOwner.InlineBox);
            Assert.Null(secondaryOwner.ImageBox);

            var multipleOwners = new MarkdownSemanticNode(MarkdownSemanticRole.Group, image)
            {
                InlineBox = inline,
                ImageBox = image,
                EmbedBox = embed,
                HostedElementBox = hosted,
                VectorSceneBox = vector,
                TableBox = table,
            };
            Assert.Same(image, multipleOwners.Box);
            Assert.Same(inline, multipleOwners.InlineBox);
            Assert.Same(image, multipleOwners.ImageBox);
            Assert.Same(embed, multipleOwners.EmbedBox);
            Assert.Same(hosted, multipleOwners.HostedElementBox);
            Assert.Same(vector, multipleOwners.VectorSceneBox);
            Assert.Same(table, multipleOwners.TableBox);

            var explicitlyNullOwners = new MarkdownSemanticNode(MarkdownSemanticRole.Group, table)
            {
                InlineBox = null,
                TableBox = null,
            };
            Assert.Null(explicitlyNullOwners.InlineBox);
            Assert.Null(explicitlyNullOwners.TableBox);
        }
        finally
        {
            vector.Dispose();
            hosted.Dispose();
            table.Dispose();
            embed.Dispose();
            image.Dispose();
            inline.Dispose();
        }
    }

    [Fact]
    public void TextMetadataPreservesNullEmptyAndReferenceIdentityForSharedAndDistinctValues()
    {
        string shared = new("shared".ToCharArray());
        var sharedValue = new MarkdownSemanticNode(MarkdownSemanticRole.Link)
        {
            CodeLanguage = shared,
            HelpText = shared,
            AccessibilityName = shared,
            AccessibilityDescription = shared,
            AutomationId = shared,
        };

        Assert.Same(shared, sharedValue.CodeLanguage);
        Assert.Same(shared, sharedValue.HelpText);
        Assert.Same(shared, sharedValue.AccessibilityName);
        Assert.Same(shared, sharedValue.AccessibilityDescription);
        Assert.Same(shared, sharedValue.AutomationId);

        string code = new("code".ToCharArray());
        string help = new("help".ToCharArray());
        string name = new("name".ToCharArray());
        string description = new("desc".ToCharArray());
        string automationId = new("id".ToCharArray());
        var distinctValues = new MarkdownSemanticNode(MarkdownSemanticRole.Embed)
        {
            CodeLanguage = code,
            HelpText = help,
            AccessibilityName = name,
            AccessibilityDescription = description,
            AutomationId = automationId,
        };

        Assert.Same(code, distinctValues.CodeLanguage);
        Assert.Same(help, distinctValues.HelpText);
        Assert.Same(name, distinctValues.AccessibilityName);
        Assert.Same(description, distinctValues.AccessibilityDescription);
        Assert.Same(automationId, distinctValues.AutomationId);

        string equalButDistinctFirst = new("equal".ToCharArray());
        string equalButDistinctSecond = new("equal".ToCharArray());
        Assert.Equal(equalButDistinctFirst, equalButDistinctSecond);
        Assert.NotSame(equalButDistinctFirst, equalButDistinctSecond);
        var equalButDistinct = new MarkdownSemanticNode(MarkdownSemanticRole.Link)
        {
            HelpText = equalButDistinctFirst,
            AutomationId = equalButDistinctSecond,
        };
        Assert.Same(equalButDistinctFirst, equalButDistinct.HelpText);
        Assert.Same(equalButDistinctSecond, equalButDistinct.AutomationId);

        var emptyAndNull = new MarkdownSemanticNode(MarkdownSemanticRole.Image)
        {
            HelpText = string.Empty,
            AccessibilityName = null,
            AutomationId = string.Empty,
        };
        Assert.Same(string.Empty, emptyAndNull.HelpText);
        Assert.Null(emptyAndNull.AccessibilityName);
        Assert.Same(string.Empty, emptyAndNull.AutomationId);
        Assert.Null(emptyAndNull.CodeLanguage);
        Assert.Null(emptyAndNull.AccessibilityDescription);
    }

    [Fact]
    public void NumericMetadataPreservesRoleSpecificDefaultsAndMutableListPositions()
    {
        var defaults = new MarkdownSemanticNode(MarkdownSemanticRole.Paragraph);
        Assert.Equal(-1, defaults.VectorSemanticIndex);
        Assert.Null(defaults.VectorSemanticRole);
        Assert.Equal(MarkdownVectorSemanticFlags.None, defaults.VectorSemanticFlags);
        Assert.Equal(-1, defaults.Row);
        Assert.Equal(-1, defaults.Column);
        Assert.Equal(1, defaults.RowSpan);
        Assert.Equal(1, defaults.ColumnSpan);
        Assert.Equal(0, defaults.RowCount);
        Assert.Equal(0, defaults.ColumnCount);
        Assert.Equal(0, defaults.Level);
        Assert.Equal(0, defaults.PositionInSet);
        Assert.Equal(0, defaults.SizeOfSet);
        Assert.False(defaults.IsHeader);

        var table = new MarkdownSemanticNode(MarkdownSemanticRole.TableCell)
        {
            Row = int.MinValue,
            Column = int.MaxValue,
            RowSpan = 0,
            ColumnSpan = -3,
            RowCount = 91,
            ColumnCount = -77,
            IsHeader = true,
        };
        Assert.Equal(int.MinValue, table.Row);
        Assert.Equal(int.MaxValue, table.Column);
        Assert.Equal(0, table.RowSpan);
        Assert.Equal(-3, table.ColumnSpan);
        Assert.Equal(91, table.RowCount);
        Assert.Equal(-77, table.ColumnCount);
        Assert.True(table.IsHeader);
        Assert.Equal(0, table.Level);

        var list = new MarkdownSemanticNode(MarkdownSemanticRole.ListItem)
        {
            Level = 2,
            PositionInSet = 5,
            SizeOfSet = 8,
        };
        list.Level = int.MinValue;
        list.PositionInSet = int.MaxValue;
        list.SizeOfSet = -9;
        Assert.Equal(int.MinValue, list.Level);
        Assert.Equal(int.MaxValue, list.PositionInSet);
        Assert.Equal(-9, list.SizeOfSet);
        Assert.Equal(-1, list.Row);
        Assert.False(list.IsHeader);
        list.Level = 0;
        list.PositionInSet = 0;
        list.SizeOfSet = 0;
        Assert.Equal(0, list.Level);
        Assert.Equal(0, list.PositionInSet);
        Assert.Equal(0, list.SizeOfSet);

        MarkdownVectorSemanticRole unknownRole = (MarkdownVectorSemanticRole)int.MinValue;
        MarkdownVectorSemanticFlags unknownFlags = (MarkdownVectorSemanticFlags)unchecked((int)0x80001234);
        var vector = new MarkdownSemanticNode(MarkdownSemanticRole.Diagram)
        {
            VectorSemanticIndex = int.MaxValue,
            VectorSemanticRole = unknownRole,
            VectorSemanticFlags = unknownFlags,
        };
        Assert.Equal(int.MaxValue, vector.VectorSemanticIndex);
        Assert.Equal(unknownRole, vector.VectorSemanticRole);
        Assert.Equal(unknownFlags, vector.VectorSemanticFlags);
        Assert.Equal(-1, vector.Row);
        Assert.Equal(0, vector.Level);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void MixedSyntheticNumericMetadataRetainsEveryKindAndListMutation(int order)
    {
        MarkdownVectorSemanticRole unknownRole = (MarkdownVectorSemanticRole)int.MinValue;
        MarkdownVectorSemanticFlags unknownFlags = (MarkdownVectorSemanticFlags)unchecked((int)0x81234567);
        MarkdownSemanticNode node = CreateMixedNumericNode(order, unknownRole, unknownFlags);

        Assert.Equal(int.MinValue, node.Row);
        Assert.Equal(7, node.Column);
        Assert.Equal(0, node.RowSpan);
        Assert.Equal(int.MaxValue, node.ColumnSpan);
        Assert.Equal(-11, node.RowCount);
        Assert.Equal(12, node.ColumnCount);
        Assert.True(node.IsHeader);
        Assert.Equal(13, node.Level);
        Assert.Equal(-14, node.PositionInSet);
        Assert.Equal(int.MaxValue, node.SizeOfSet);
        Assert.Equal(-16, node.VectorSemanticIndex);
        Assert.Equal(unknownRole, node.VectorSemanticRole);
        Assert.Equal(unknownFlags, node.VectorSemanticFlags);
        Assert.Equal(17, node.HeadingLevel);
        Assert.Equal((MarkdownAccessibilityRole)int.MaxValue, node.AccessibilityRole);

        node.Level = int.MinValue;
        node.PositionInSet = int.MaxValue;
        node.SizeOfSet = 0;
        Assert.Equal(int.MinValue, node.Level);
        Assert.Equal(int.MaxValue, node.PositionInSet);
        Assert.Equal(0, node.SizeOfSet);
        Assert.Equal(int.MinValue, node.Row);
        Assert.Equal(unknownRole, node.VectorSemanticRole);

        var vectorFirst = new MarkdownSemanticNode(MarkdownSemanticRole.Diagram)
        {
            VectorSemanticIndex = int.MaxValue,
            VectorSemanticRole = unknownRole,
            VectorSemanticFlags = unknownFlags,
            Row = int.MinValue,
            ColumnSpan = 0,
            Level = int.MaxValue,
        };
        Assert.Equal(int.MaxValue, vectorFirst.VectorSemanticIndex);
        Assert.Equal(unknownRole, vectorFirst.VectorSemanticRole);
        Assert.Equal(unknownFlags, vectorFirst.VectorSemanticFlags);
        Assert.Equal(int.MinValue, vectorFirst.Row);
        Assert.Equal(0, vectorFirst.ColumnSpan);
        Assert.Equal(int.MaxValue, vectorFirst.Level);
    }

    [Fact]
    public void NodeRemainsItsStableLiveChildrenViewAndKeepsParentAndTextRanges()
    {
        var root = new MarkdownSemanticNode(MarkdownSemanticRole.Document)
        {
            TextStart = 3,
            TextEnd = 21,
        };
        MarkdownSemanticNode.ChildrenView retainedView = root.Children;
        var child = new MarkdownSemanticNode(MarkdownSemanticRole.Paragraph)
        {
            TextStart = 4,
            TextEnd = 20,
        };

        root.Add(child);

        Assert.Same(root, retainedView.Owner);
        Assert.Same(root.Children.Owner, retainedView.Owner);
        Assert.Single(retainedView);
        Assert.Same(child, retainedView[0]);
        Assert.Same(root, child.Parent);
        Assert.Equal(3, root.TextStart);
        Assert.Equal(21, root.TextEnd);
        Assert.Equal(4, child.TextStart);
        Assert.Equal(20, child.TextEnd);
    }

    private static MarkdownLayoutContext CreateContext()
    {
        var style = new ElementStyle();
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.Diagram] = style,
            [MarkdownElementKeys.ImageCaption] = style,
            [MarkdownElementKeys.Table] = style,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
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
            new MarkdownSourceMap(string.Empty),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight);
    }

    private static MarkdownSemanticNode CreateMixedNumericNode(
        int order,
        MarkdownVectorSemanticRole vectorRole,
        MarkdownVectorSemanticFlags vectorFlags)
    {
        const MarkdownSemanticRole role = MarkdownSemanticRole.Group;
        return order switch
        {
            0 => new MarkdownSemanticNode(role)
            {
                Row = int.MinValue, Column = 7, RowSpan = 0, ColumnSpan = int.MaxValue,
                RowCount = -11, ColumnCount = 12, IsHeader = true,
                Level = 13, PositionInSet = -14, SizeOfSet = int.MaxValue,
                VectorSemanticIndex = -16, VectorSemanticRole = vectorRole, VectorSemanticFlags = vectorFlags,
                HeadingLevel = 17, AccessibilityRole = (MarkdownAccessibilityRole)int.MaxValue,
            },
            1 => new MarkdownSemanticNode(role)
            {
                Row = int.MinValue, Column = 7, RowSpan = 0, ColumnSpan = int.MaxValue,
                RowCount = -11, ColumnCount = 12, IsHeader = true,
                VectorSemanticIndex = -16, VectorSemanticRole = vectorRole, VectorSemanticFlags = vectorFlags,
                Level = 13, PositionInSet = -14, SizeOfSet = int.MaxValue,
                HeadingLevel = 17, AccessibilityRole = (MarkdownAccessibilityRole)int.MaxValue,
            },
            2 => new MarkdownSemanticNode(role)
            {
                Level = 13, PositionInSet = -14, SizeOfSet = int.MaxValue,
                Row = int.MinValue, Column = 7, RowSpan = 0, ColumnSpan = int.MaxValue,
                RowCount = -11, ColumnCount = 12, IsHeader = true,
                VectorSemanticIndex = -16, VectorSemanticRole = vectorRole, VectorSemanticFlags = vectorFlags,
                HeadingLevel = 17, AccessibilityRole = (MarkdownAccessibilityRole)int.MaxValue,
            },
            3 => new MarkdownSemanticNode(role)
            {
                Level = 13, PositionInSet = -14, SizeOfSet = int.MaxValue,
                VectorSemanticIndex = -16, VectorSemanticRole = vectorRole, VectorSemanticFlags = vectorFlags,
                Row = int.MinValue, Column = 7, RowSpan = 0, ColumnSpan = int.MaxValue,
                RowCount = -11, ColumnCount = 12, IsHeader = true,
                HeadingLevel = 17, AccessibilityRole = (MarkdownAccessibilityRole)int.MaxValue,
            },
            4 => new MarkdownSemanticNode(role)
            {
                VectorSemanticIndex = -16, VectorSemanticRole = vectorRole, VectorSemanticFlags = vectorFlags,
                Row = int.MinValue, Column = 7, RowSpan = 0, ColumnSpan = int.MaxValue,
                RowCount = -11, ColumnCount = 12, IsHeader = true,
                Level = 13, PositionInSet = -14, SizeOfSet = int.MaxValue,
                HeadingLevel = 17, AccessibilityRole = (MarkdownAccessibilityRole)int.MaxValue,
            },
            5 => new MarkdownSemanticNode(role)
            {
                VectorSemanticIndex = -16, VectorSemanticRole = vectorRole, VectorSemanticFlags = vectorFlags,
                Level = 13, PositionInSet = -14, SizeOfSet = int.MaxValue,
                Row = int.MinValue, Column = 7, RowSpan = 0, ColumnSpan = int.MaxValue,
                RowCount = -11, ColumnCount = 12, IsHeader = true,
                HeadingLevel = 17, AccessibilityRole = (MarkdownAccessibilityRole)int.MaxValue,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(order)),
        };
    }

    private static DeclarativeHostedElementBox CreateHostedBox(MarkdownLayoutContext context)
    {
        var builder = new MarkdownContentBuilder();
        builder.AddHostedElement("test.host", new SourceSpan(0, 1), MarkdownStyleRole.Body);
        MarkdownContentFragment fragment = builder.Build();
        MarkdownContent content = Assert.Single(fragment.Items);
        return new DeclarativeHostedElementBox(
            context,
            content,
            new HostedElementFallbackKey(fragment));
    }

    private static VectorSceneBox CreateVectorBox(MarkdownLayoutContext context)
    {
        var scene = new MarkdownVectorScene(
            width: 10,
            height: 10,
            baseline: 10,
            commands: Array.Empty<MarkdownVectorCommand>(),
            referenceFontSize: 16);
        var builder = new MarkdownContentBuilder();
        builder.AddVectorScene(
            scene,
            new SourceSpan(0, 1),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram);
        MarkdownContent content = Assert.Single(builder.Build().Items);
        return new VectorSceneBox(context, content, MarkdownElementKeys.Diagram);
    }

    private sealed class StubEmbedFactory : IMarkdownEmbedFactory
    {
        internal static StubEmbedFactory Instance { get; } = new();

        public bool CanCreate(Block block) => true;
        public float MeasureHeight(Block block, float availableWidth) => 0;
        public FrameworkElement CreateBlock(Block block) => throw new NotSupportedException();
    }
}
