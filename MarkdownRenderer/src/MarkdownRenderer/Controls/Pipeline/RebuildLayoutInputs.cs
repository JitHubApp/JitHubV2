using System;
using System.Collections.Generic;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Hosting.Internal;
using MarkdownRenderer.Images;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// The presentation inputs a rebuild lays out with: theme, hosting, image and
/// code block configuration, localization and scale. Captured on the UI
/// thread once the parse finishes, so layout never reads a dependency
/// property from the pool.
/// </summary>
internal sealed class RebuildLayoutInputs
{
    internal required MarkdownTheme Theme { get; init; }
    internal required MarkdownStyleSheet? StyleSheet { get; init; }
    internal required FlowDirection FlowDirection { get; init; }
    internal required IMarkdownEmbedFactory? EmbedFactory { get; init; }
    internal required IMarkdownHostedElementFactory? HostedElementFactory { get; init; }

    /// <summary>The host's own image resolver, which is also the image carry identity.</summary>
    internal required IMarkdownImageResolver? ImageResolver { get; init; }

    internal required IMarkdownSvgRenderer? SvgRenderer { get; init; }
    internal required IMarkdownRasterDecoder? RasterDecoder { get; init; }
    internal required Uri? ImageBaseUri { get; init; }
    internal required string? ImageDocumentPath { get; init; }
    internal required MarkdownDocumentSource? ImageDocumentSource { get; init; }
    internal required bool AllowThirdPartyRemoteImages { get; init; }
    internal required bool IsCodeBlockCopyEnabled { get; init; }
    internal required bool IsTaskListEditingEnabled { get; init; }
    internal required IMarkdownCommandProvider? CommandProvider { get; init; }
    internal required MarkdownLocalizationSnapshot Localization { get; init; }
    internal required CodeBlockLineNumberMode LineNumberMode { get; init; }
    internal required CodeBlockWrappingMode WrappingMode { get; init; }
    internal required CodeBlockChromeMode ChromeMode { get; init; }

    /// <summary>A private copy of the disclosure (details) states.</summary>
    internal required Dictionary<string, bool> DisclosureStates { get; init; }

    internal required double TextScaleFactor { get; init; }

    /// <summary>The host's rasterization scale, so raster fallbacks render at device pixels.</summary>
    internal required double RasterScale { get; init; }

    internal IMarkdownStringProvider? StringProvider => Localization.StringProvider;

    internal string Language => Localization.Culture.Name;
}
