using MarkdownRenderer.Controls.Providers;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// Reads the control's properties into the immutable inputs one rebuild
/// parses and lays out, on the UI thread.
/// </summary>
internal sealed class RebuildInputCapture
{
    private readonly MarkdownRendererControl _control;
    private readonly DocumentInputs _documentInputs;
    private readonly RendererLocalization _localization;
    private readonly EnvironmentSubscription _environment;
    // The theme used while Theme is null. A MarkdownTheme is a thread-affine
    // DependencyObject, so each control owns one rather than sharing a static.
    private readonly MarkdownTheme _defaultTheme = new();

    internal RebuildInputCapture(
        MarkdownRendererControl control,
        DocumentInputs documentInputs,
        RendererLocalization localization,
        EnvironmentSubscription environment)
    {
        _control = control;
        _documentInputs = documentInputs;
        _localization = localization;
        _environment = environment;
    }

    internal RebuildSourceInputs CaptureSource()
    {
        MarkdownExtensionRegistry? extensionRegistry = _control.ExtensionRegistry;
        MarkdownExtensionRegistry legacyRegistry = extensionRegistry ?? RebuildLayoutSetup.DefaultRegistry;
        IMarkdownPerformanceSessionInternal? performanceSession =
            _control.PerformanceSession as IMarkdownPerformanceSessionInternal;
        if (performanceSession?.IsDisposed == true)
            performanceSession = null;
        var reusableDocument = _documentInputs.HasExplicitDocument ? _control.Document : null;
        return new RebuildSourceInputs(
            extensionRegistry,
            legacyRegistry,
            legacyRegistry.Revision,
            _control.Engine,
            performanceSession,
            reusableDocument,
            reusableDocument?.Source ?? _control.Markdown ?? string.Empty);
    }

    internal RebuildLayoutInputs CaptureLayout() => new()
    {
        Theme = _control.Theme ?? _defaultTheme,
        StyleSheet = _control.StyleSheet,
        FlowDirection = _control.FlowDirection,
        EmbedFactory = _control.EmbedFactory,
        HostedElementFactory = _control.HostedElementFactory,
        ImageResolver = _control.ImageResolver,
        SvgRenderer = _control.SvgRenderer,
        RasterDecoder = _control.RasterDecoder,
        ImageBaseUri = _control.ImageBaseUri,
        ImageDocumentPath = _control.ImageDocumentPath,
        ImageDocumentSource = _control.ImageDocumentSource,
        AllowThirdPartyRemoteImages = _control.AllowThirdPartyRemoteImages,
        IsCodeBlockCopyEnabled = _control.IsCodeBlockCopyEnabled,
        IsTaskListEditingEnabled = _control.IsTaskListEditingEnabled,
        CommandProvider = _control.CommandProvider,
        Localization = _localization.CaptureRequested(),
        LineNumberMode = _control.CodeBlockLineNumberMode,
        WrappingMode = _control.CodeBlockWrappingMode,
        ChromeMode = _control.CodeBlockChromeMode,
        DisclosureStates = _documentInputs.CopyDisclosureStates(),
        TextScaleFactor = _environment.Snapshot.TextScaleFactor > 0
            ? _environment.Snapshot.TextScaleFactor
            : 1.0,
        // XamlRoot is null until the control is loaded; default to 1.0.
        RasterScale = _control.XamlRoot?.RasterizationScale ?? 1.0,
    };
}
