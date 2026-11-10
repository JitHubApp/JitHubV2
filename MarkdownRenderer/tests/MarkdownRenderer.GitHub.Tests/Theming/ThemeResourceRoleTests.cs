using MarkdownRenderer.GitHub.Tests.TestSupport;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.ThemeSnapshotFixture;

namespace MarkdownRenderer.GitHub.Tests.Theming;

/// <summary>
/// Checks which resource keys project to style roles: global, reserved,
/// malformed and oversized keys are rejected, canonical and extension
/// roles are accepted, and legacy override aliases stay usable.
/// </summary>
[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class ThemeResourceRoleTests
{
    [Theory]
    [InlineData(MarkdownResourceKeys.DocumentPadding)]
    [InlineData(MarkdownResourceKeys.SelectionBackgroundBrush)]
    [InlineData(MarkdownResourceKeys.SelectionForegroundBrush)]
    [InlineData("MarkdownRenderer. Document.FontSize")]
    [InlineData("MarkdownRenderer.Document.FontSize")]
    [InlineData("MarkdownRenderer.Bad\u0001Role.FontSize")]
    public void ResourceRoleDiscovery_RejectsGlobalReservedOrMalformedKeys(string resourceKey)
    {
        Assert.False(MarkdownResourceKeys.TryGetStyleRoleName(resourceKey, out _));
    }

    [Fact]
    public void ResourceRoleDiscovery_RejectsOversizedRoleAndAcceptsCanonicalExtensionRole()
    {
        string oversized = $"MarkdownRenderer.{new string('x', 129)}.FontSize";

        Assert.False(MarkdownResourceKeys.TryGetStyleRoleName(oversized, out _));
        Assert.True(MarkdownResourceKeys.TryGetStyleRoleName(
            "MarkdownRenderer.Extension.Widget.FontSize",
            out string roleName));
        Assert.Equal("Extension.Widget", roleName);
    }

    [Theory]
    [InlineData("Document")]
    [InlineData("Selection")]
    [InlineData("FocusVisual")]
    [InlineData("Interaction")]
    [InlineData("Overflow")]
    public void MarkdownStyleRole_RejectsGlobalResourceScopes(string roleName)
    {
        Assert.Throws<ArgumentException>(() => new MarkdownStyleRole(roleName));
    }

    [Theory]
    [InlineData("Body", true)]
    [InlineData("Extension.Widget", true)]
    [InlineData("Document", false)]
    [InlineData(" Extension.Widget ", false)]
    [InlineData("Bad\u0001Role", false)]
    public void ResourceRoleProjection_OnlyAcceptsCanonicalNonReservedNames(
        string elementKey,
        bool expected)
    {
        bool projected = MarkdownStyleRole.TryCreateCanonical(
            elementKey,
            out MarkdownStyleRole role);

        Assert.Equal(expected, projected);
        Assert.Equal(expected ? elementKey : string.Empty, role.Name);
    }

    [Fact]
    public void ResourceRoleProjection_RejectsLongLegacyAliasWithoutThrowing()
    {
        string legacyAlias = MarkdownElementKeys.Class(new string('x', 129));

        Assert.False(MarkdownStyleRole.TryCreateCanonical(legacyAlias, out MarkdownStyleRole role));
        Assert.True(role.IsEmpty);
    }

    [Fact]
    public void LegacyOverrideAlias_RemainsUsableWhenItCannotProjectToAResourceRole()
    {
        string legacyAlias = MarkdownElementKeys.Class(new string('x', 129));
        var overrides = new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal)
        {
            [legacyAlias] = new ElementStyleOverride { FontSize = 19 },
        };
        ThemeSnapshot snapshot = CreateSnapshot(
            new ElementStyle { FontSize = 13 },
            overrides,
            isHighContrast: false,
            MarkdownStyleSheet.Empty);

        Assert.False(MarkdownStyleRole.TryCreateCanonical(legacyAlias, out _));
        ElementStyle resolved = snapshot.GetStyle(
            MarkdownElementKeys.Body,
            contextKeys: null,
            aliasKeys: [legacyAlias]);
        Assert.Equal(19, resolved.FontSize);
    }
}
