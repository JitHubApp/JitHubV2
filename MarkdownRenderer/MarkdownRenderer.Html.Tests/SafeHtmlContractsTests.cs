using System.Reflection;
using Markdig.Syntax;
using MarkdownRenderer.Html.Renderers;
using MarkdownRenderer.Parsing;
using Xunit;

namespace MarkdownRenderer.Html.Tests;

public sealed class SafeHtmlContractsTests
{
    [Fact]
    public void OptionsClampBudgetsToAuditedDefaults()
    {
        var wider = new SafeHtmlBudgets(
            SafeHtmlBudgets.DefaultMaxInputLength + 1,
            SafeHtmlBudgets.DefaultMaxNodeCount + 1,
            SafeHtmlBudgets.DefaultMaxNestingDepth + 1,
            SafeHtmlBudgets.DefaultMaxAttributeCount + 1,
            SafeHtmlBudgets.DefaultMaxAttributeValueLength + 1,
            SafeHtmlBudgets.DefaultMaxTagLength + 1);

        Assert.Equal(SafeHtmlBudgets.Default, new SafeHtmlOptions(wider).Budgets);
    }

    [Fact]
    public void DefaultBudgets_MatchAuditedNativeSubsetCeilings()
    {
        SafeHtmlBudgets budgets = SafeHtmlBudgets.Default;

        Assert.Equal(4 * 1024 * 1024, budgets.MaxInputLength);
        Assert.Equal(20_000, budgets.MaxNodeCount);
        Assert.Equal(64, budgets.MaxNestingDepth);
        Assert.Equal(32, budgets.MaxAttributeCount);
        Assert.Equal(16 * 1024, budgets.MaxAttributeValueLength);
        Assert.Equal(64 * 1024, budgets.MaxTagLength);
    }

    [Fact]
    public void LoweredTo_NeverRaisesAnyBudget()
    {
        var configured = new SafeHtmlBudgets(100, 200, 30, 12, 500, 700);
        var hostCeilings = new SafeHtmlBudgets(50, 300, 20, 20, 400, 900);

        SafeHtmlBudgets lowered = configured.LoweredTo(hostCeilings);

        Assert.Equal(new SafeHtmlBudgets(50, 200, 20, 12, 400, 700), lowered);
    }

    [Fact]
    public void Options_CopyAndValidateSafeStylesheetClassTokens()
    {
        var classes = new List<string> { "markdown-alert", "note_2" };
        var options = new SafeHtmlOptions(allowedStyleClasses: classes);
        classes.Add("added-later");

        Assert.Equal(2, options.AllowedStyleClasses.Count);
        Assert.Contains("markdown-alert", options.AllowedStyleClasses);
        Assert.Throws<ArgumentException>(() => new SafeHtmlOptions(allowedStyleClasses: ["unsafe token"]));
    }

    [Fact]
    public void CapabilityMarker_HasNonConfigurableSecurityBoundary()
    {
        var marker = new SafeHtmlCapabilityMarker(new SafeHtmlOptions(enableImages: false));
        SafeHtmlCapabilityDescriptor capabilities = marker.SafeHtmlCapabilities;

        Assert.True(capabilities.IsNativeSubset);
        Assert.False(capabilities.ExecutesScript);
        Assert.False(capabilities.AppliesCssLayout);
        Assert.False(capabilities.ExposesBrowserDom);
        Assert.False(capabilities.HasFilesystemAccess);
        Assert.False(capabilities.HasDirectNetworkAccess);
        Assert.Equal(SafeHtmlExternalResourceRouting.HostMediated, capabilities.LinkRouting);
        Assert.Equal(SafeHtmlExternalResourceRouting.HostMediated, capabilities.ImageRouting);
        Assert.False(marker.SafeHtmlOptions.EnableImages);
    }

    [Fact]
    public void PublicContract_DoesNotExposeWinUiOrMarkdigTypes()
    {
        Type[] publicTypes = typeof(SafeHtmlFeature).Assembly.GetExportedTypes();
        IEnumerable<Type> signatureTypes = publicTypes.SelectMany(type =>
            type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .SelectMany(GetSignatureTypes));

        Assert.DoesNotContain(signatureTypes, type =>
            type.Namespace?.StartsWith("Microsoft.UI", StringComparison.Ordinal) == true ||
            type.Namespace?.StartsWith("Markdig", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void PackageOwnsNativeRendererAndRegistersExactHtmlBlockDispatch()
    {
        var options = new SafeHtmlOptions(
            enableLinks: false,
            enableImages: false,
            allowedStyleClasses: ["approved"]);
        var registry = new MarkdownExtensionRegistry().ConfigureSafeHtmlRegistry(options);

        Assert.True(registry.TryGetRenderer(typeof(HtmlBlock), out var renderer));
        Assert.IsType<HtmlBlockRenderer>(renderer);
        Assert.NotNull(registry.SafeHtmlPolicy);
        Assert.False(registry.SafeHtmlPolicy.EnableLinks);
        Assert.False(registry.SafeHtmlPolicy.EnableImages);
        Assert.True(registry.SafeHtmlPolicy.IsStyleClassAllowed("approved"));
        Assert.False(registry.SafeHtmlPolicy.IsStyleClassAllowed("unapproved"));
    }

    [Fact]
    public void ParserHonorsHostLoweredBudgetsInsteadOfHardCodedDefaults()
    {
        var limits = new SafeHtmlParseLimits(
            MaxInputLength: 128,
            MaxNodeCount: 2,
            MaxNestingDepth: 1,
            MaxAttributeCount: 1,
            MaxAttributeValueLength: 3,
            MaxTagLength: 128);

        SafeHtmlDocument document = SafeHtmlParser.Parse(
            "<p first='abcdef' second='ignored'>content</p>",
            limits);

        Assert.True(document.IsTruncated);
        SafeHtmlElement paragraph = Assert.IsType<SafeHtmlElement>(Assert.Single(document.Root.Children));
        Assert.True(paragraph.TryGetAttribute("first", out string first));
        Assert.Equal("abc", first);
        Assert.False(paragraph.TryGetAttribute("second", out _));
    }

    [Fact]
    public void ParserRetainsExactUnknownAndSuppressedMarkupForLiteralFallback()
    {
        const string source = "<custom DATA-x='v'>a<script>alert(1)</script></custom>";

        SafeHtmlDocument document = SafeHtmlParser.Parse(source);

        SafeHtmlElement custom = Assert.IsType<SafeHtmlElement>(Assert.Single(document.Root.Children));
        Assert.Equal("<custom DATA-x='v'>", custom.RawOpeningTag);
        Assert.Equal("</custom>", custom.RawClosingTag);
        SafeHtmlElement script = Assert.IsType<SafeHtmlElement>(custom.Children[1]);
        Assert.Equal("<script>", script.RawOpeningTag);
        Assert.Equal("alert(1)</script>", script.RawTrailingMarkup);
    }

    private static IEnumerable<Type> GetSignatureTypes(MemberInfo member) => member switch
    {
        MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType),
        PropertyInfo property => [property.PropertyType],
        FieldInfo field => [field.FieldType],
        EventInfo eventInfo when eventInfo.EventHandlerType is not null => [eventInfo.EventHandlerType],
        _ => [],
    };
}
