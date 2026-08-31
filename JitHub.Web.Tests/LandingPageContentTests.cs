using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using JitHub.Models;
using JitHub.Web.Content;
using Xunit;

namespace JitHub.Web.Tests;

public sealed class LandingPageContentTests
{
    [Fact]
    public void Catalog_has_unique_navigation_anchors_and_complete_chapters()
    {
        string[] anchors = ["intro", .. LandingPageContent.Chapters.Select(static chapter => chapter.Id), "themes", "capabilities"];

        Assert.Equal(anchors.Length, anchors.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(8, anchors.Length);
        Assert.Equal(5, LandingPageContent.Chapters.Count);
        Assert.All(LandingPageContent.Chapters, chapter =>
        {
            Assert.False(string.IsNullOrWhiteSpace(chapter.Eyebrow));
            Assert.False(string.IsNullOrWhiteSpace(chapter.Title));
            Assert.False(string.IsNullOrWhiteSpace(chapter.Description));
            Assert.Equal(3, chapter.Highlights.Count);
            Assert.All(chapter.Highlights, highlight => Assert.False(string.IsNullOrWhiteSpace(highlight)));
        });
    }

    [Fact]
    public void Theme_palette_story_uses_the_apps_complete_shared_color_catalog()
    {
        string[] expectedIds = ThemePaletteCatalog.All.Select(static palette => palette.Id).ToArray();

        Assert.Equal(20, expectedIds.Length);
        Assert.Equal(expectedIds, LandingPageContent.ThemePalettes.Select(static palette => palette.Id));
        Assert.Equal(ThemePaletteCatalog.All.Count, LandingPageContent.ThemePalettes.Count);
        Assert.Equal(4, LandingPageContent.FeaturedThemePalettes.Count);
        Assert.Equal(16, LandingPageContent.AdditionalThemePalettes.Count);
        Assert.Empty(LandingPageContent.FeaturedThemePalettes
            .Select(static palette => palette.Id)
            .Intersect(LandingPageContent.AdditionalThemePalettes.Select(static palette => palette.Id), StringComparer.Ordinal));
        Assert.Equal(
            expectedIds.Order(StringComparer.Ordinal),
            LandingPageContent.FeaturedThemePalettes
                .Concat(LandingPageContent.AdditionalThemePalettes)
                .Select(static palette => palette.Id)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            LandingPageContent.ThemePalettes.Count,
            LandingPageContent.ThemePalettes.Select(static palette => palette.Name).Distinct(StringComparer.Ordinal).Count());

        foreach (ThemePaletteStory story in LandingPageContent.ThemePalettes)
        {
            ThemePaletteDefinition appPalette = ThemePaletteCatalog.Find(story.Id);
            Assert.Equal(appPalette.Light, story.Light);
            Assert.Equal(appPalette.Dark, story.Dark);
            Assert.False(string.IsNullOrWhiteSpace(story.Name));
            Assert.False(string.IsNullOrWhiteSpace(story.Description));

            foreach (string color in PaletteColors(story.Light).Concat(PaletteColors(story.Dark)))
            {
                Assert.Matches("^#[0-9A-Fa-f]{6}$", color);
            }
        }
    }

    [Fact]
    public void Landing_page_uses_three_purposeful_product_images()
    {
        MediaAsset[] chapterMedia = LandingPageContent.Chapters
            .Select(static chapter => chapter.FeaturedMedia)
            .Where(static media => media is not null)
            .Cast<MediaAsset>()
            .ToArray();

        Assert.Equal(2, chapterMedia.Length);
        Assert.Equal(
            ["pull-request-conversation", "commit-diff"],
            chapterMedia.Select(static media => media.Id));
        Assert.DoesNotContain(chapterMedia, static media => media.Id == LandingPageContent.HomeWorkspace.Id);
    }

    [Fact]
    public void Capability_index_covers_every_release_surface_once()
    {
        CapabilityItem[] items = LandingPageContent.CapabilityGroups
            .SelectMany(static group => group.Items)
            .ToArray();
        string[] expected = Enumerable.Range(1, LandingPageContent.ReleaseSurfaceCount)
            .Select(static index => $"REL-UI-{index:D3}")
            .ToArray();

        Assert.Equal(LandingPageContent.ReleaseSurfaceCount, items.Length);
        Assert.Equal(expected, items.Select(static item => item.ReleaseSurfaceId).Order(StringComparer.Ordinal));
        Assert.All(items, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Title));
            Assert.False(string.IsNullOrWhiteSpace(item.Description));
        });
    }

    [Fact]
    public void Every_media_asset_is_paired_and_has_descriptive_copy()
    {
        Assert.Equal(8, LandingPageContent.AllMedia.Count);
        Assert.Equal(
            LandingPageContent.AllMedia.Count,
            LandingPageContent.AllMedia.Select(static media => media.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(LandingPageContent.AllMedia, media =>
        {
            Assert.Equal(3200, media.Width);
            Assert.Equal(1800, media.Height);
            Assert.EndsWith($"{media.Id}-light.png", media.LightSource, StringComparison.Ordinal);
            Assert.EndsWith($"{media.Id}-dark.png", media.DarkSource, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(media.Alt));
            Assert.True(media.Alt.Length >= 40, $"{media.Id} needs useful alternative text.");
            Assert.False(string.IsNullOrWhiteSpace(media.Caption));
        });
    }

    [Fact]
    public void Media_manifest_matches_catalog_files_dimensions_and_hashes()
    {
        string webRoot = Path.Combine(FindRepositoryRoot(), "JitHub.Web", "wwwroot");
        string showcaseRoot = Path.Combine(webRoot, "media", "showcase");
        string manifestPath = Path.Combine(showcaseRoot, "media-manifest.json");
        Assert.True(File.Exists(manifestPath), $"Missing website media manifest: {manifestPath}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        JsonElement root = document.RootElement;
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(3200, root.GetProperty("captureWidth").GetInt32());
        Assert.Equal(1800, root.GetProperty("captureHeight").GetInt32());
        Assert.Equal(1200, root.GetProperty("minimumLogicalWidth").GetInt32());
        Assert.Equal(675, root.GetProperty("minimumLogicalHeight").GetInt32());
        Assert.Equal("synthetic-public-preview", root.GetProperty("source").GetString());
        Assert.Equal("blocked-loopback-proxy", root.GetProperty("networkPolicy").GetString());

        JsonElement[] entries = root.GetProperty("assets").EnumerateArray().ToArray();
        Assert.Equal(16, entries.Length);
        foreach (MediaAsset media in LandingPageContent.AllMedia)
        {
            AssertManifestAsset(entries, media, "light", media.LightSource, webRoot);
            AssertManifestAsset(entries, media, "dark", media.DarkSource, webRoot);
        }
    }

    [Fact]
    public void Catalog_copy_stays_grounded_in_user_work()
    {
        string copy = string.Join(
            ' ',
            LandingPageContent.Chapters.SelectMany(static chapter =>
                    new[] { chapter.Title, chapter.Description }.Concat(chapter.Highlights))
                .Concat(LandingPageContent.CapabilityGroups.SelectMany(static group =>
                    group.Items.SelectMany(static item => new[] { item.Title, item.Description })))
                .Concat(LandingPageContent.AllMedia.SelectMany(static media => new[] { media.Alt, media.Caption })));

        Assert.DoesNotContain("morph", copy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shy", copy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("breakpoint", copy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user-collapsed", copy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refreshed_styles_avoid_negative_tracking_and_viewport_scaled_type()
    {
        string webRoot = Path.Combine(FindRepositoryRoot(), "JitHub.Web");
        string css = File.ReadAllText(Path.Combine(webRoot, "wwwroot", "app.css"));
        string layoutCss = File.ReadAllText(Path.Combine(webRoot, "Layout", "MainLayout.razor.css"));
        string allCss = string.Concat(css, Environment.NewLine, layoutCss);

        Assert.DoesNotContain("letter-spacing: -", allCss, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"font-size\s*:[^;]*(vw|vi|vmin|vmax)", allCss);
        Assert.DoesNotMatch(@"(?m)^(?!\s*--[a-z0-9-]+\s*:)[^\r\n]*oklch\(", allCss);
        Assert.DoesNotMatch(@"(?m)^(?!\s*--[a-z0-9-]+\s*:)[^\r\n]*#[0-9a-f]{3,8}\b", allCss);
        Assert.DoesNotMatch(@"(?im)^(?!\s*--[a-z0-9-]+\s*:)[^\r\n]*(?<!-)\b(?:white|black)\b(?!-)", allCss);
        Assert.Contains(":focus-visible", css, StringComparison.Ordinal);
        Assert.Contains("white-space: nowrap", css, StringComparison.Ordinal);
        Assert.Contains("var(--surface-tint)", layoutCss, StringComparison.Ordinal);
        Assert.Matches(
            @"\.product-frame__image\s*\{[\s\S]*?width\s*:\s*100%\s*;\s*height\s*:\s*auto\s*;\s*aspect-ratio\s*:\s*16\s*/\s*9\s*;",
            css);
        Assert.Contains("@media (forced-colors: active)", css, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", css, StringComparison.Ordinal);
        Assert.Contains("background: var(--theme-canvas)", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Primary_button_surface_tokens_keep_white_text_accessible()
    {
        string css = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "JitHub.Web", "wwwroot", "app.css"));
        Match[] surfaces = Regex.Matches(
                css,
                @"--button-primary-surface(?:-hover)?\s*:\s*oklch\(([0-9.]+)\s+([0-9.]+)\s+([0-9.]+)\)")
            .Cast<Match>()
            .ToArray();

        Assert.NotEmpty(surfaces);
        Assert.All(surfaces, match =>
        {
            double lightness = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            double chroma = double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            double hue = double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            double contrast = CalculateWhiteContrast(lightness, chroma, hue);
            Assert.True(contrast >= 4.5, $"{match.Value} provides only {contrast:F2}:1 contrast against white text.");
        });
    }

    [Fact]
    public void Presentation_assets_use_fingerprinted_urls()
    {
        string root = FindRepositoryRoot();
        string app = File.ReadAllText(Path.Combine(root, "JitHub.Web", "App.razor"));
        string host = File.ReadAllText(Path.Combine(root, "JitHub.Web", "Program.cs"));
        string home = File.ReadAllText(Path.Combine(root, "JitHub.Web", "Pages", "Home.razor"));
        string layout = File.ReadAllText(Path.Combine(root, "JitHub.Web", "Layout", "MainLayout.razor"));
        string image = File.ReadAllText(Path.Combine(root, "JitHub.Web", "Components", "ThemeImage.razor"));
        string mediaLoader = File.ReadAllText(Path.Combine(root, "JitHub.Web", "wwwroot", "js", "media.js"));

        Assert.Contains("app.MapStaticAssets()", host, StringComparison.Ordinal);
        Assert.Contains(".WithStaticAssets()", host, StringComparison.Ordinal);
        Assert.DoesNotContain("app.UseStaticFiles()", host, StringComparison.Ordinal);
        Assert.Contains("@Assets[\"app.css\"]", app, StringComparison.Ordinal);
        Assert.Contains("@Assets[\"js/theme.js\"]", app, StringComparison.Ordinal);
        Assert.Contains("@Assets[\"js/media.js\"]", app, StringComparison.Ordinal);
        Assert.Contains("@Assets[\"JitHubLogo.png\"]", layout, StringComparison.Ordinal);
        Assert.Contains("<button class=\"theme-toggle\" type=\"button\"", layout, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Primary\"", layout, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(home, "Loading=\"eager\"").Cast<Match>());
        Assert.Contains("property=\"og:image:width\" content=\"3200\"", home, StringComparison.Ordinal);
        Assert.Contains("property=\"og:image:height\" content=\"1800\"", home, StringComparison.Ordinal);
        Assert.Contains("name=\"twitter:image:alt\"", home, StringComparison.Ordinal);
        Assert.Contains("@Assets[Media.LightSource.TrimStart('/')]", image, StringComparison.Ordinal);
        Assert.Contains("@Assets[Media.DarkSource.TrimStart('/')]", image, StringComparison.Ordinal);
        Assert.Contains("rootMargin: \"20px 0px\"", mediaLoader, StringComparison.Ordinal);
        Assert.Contains("image.dataset.themeImmediate === \"true\"", mediaLoader, StringComparison.Ordinal);
        Assert.Contains("document.addEventListener(\"enhancedload\", registerThemeMedia)", mediaLoader, StringComparison.Ordinal);
        Assert.DoesNotContain("480px", mediaLoader, StringComparison.Ordinal);
    }

    private static void AssertManifestAsset(
        IEnumerable<JsonElement> entries,
        MediaAsset media,
        string theme,
        string source,
        string webRoot)
    {
        JsonElement[] matches = entries
            .Where(entry =>
                string.Equals(entry.GetProperty("id").GetString(), media.Id, StringComparison.Ordinal) &&
                string.Equals(entry.GetProperty("theme").GetString(), theme, StringComparison.Ordinal))
            .ToArray();
        JsonElement entry = Assert.Single(matches);
        Assert.Equal(media.Width, entry.GetProperty("width").GetInt32());
        Assert.Equal(media.Height, entry.GetProperty("height").GetInt32());
        Assert.True(entry.GetProperty("windowDpi").GetUInt32() >= 96);
        Assert.True(entry.GetProperty("logicalWidth").GetInt32() >= 1200);
        Assert.True(entry.GetProperty("logicalHeight").GetInt32() >= 675);
        Assert.StartsWith("synthetic-public-preview/", entry.GetProperty("sourceState").GetString(), StringComparison.Ordinal);

        string expectedFile = Path.GetFileName(source);
        Assert.Equal(expectedFile, entry.GetProperty("file").GetString());
        string filePath = Path.Combine(webRoot, source.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(filePath), $"Missing catalog media asset: {filePath}");
        (int width, int height) = ReadPngDimensions(filePath);
        Assert.Equal(media.Width, width);
        Assert.Equal(media.Height, height);
        Assert.Equal(entry.GetProperty("sha256").GetString(), ComputeSha256(filePath));
    }

    private static IEnumerable<string> PaletteColors(ThemePalettePreview preview) =>
    [
        preview.Canvas,
        preview.Rail,
        preview.Surface,
        preview.Accent,
        preview.Ink
    ];

    private static (int Width, int Height) ReadPngDimensions(string path)
    {
        Span<byte> header = stackalloc byte[24];
        using FileStream stream = File.OpenRead(path);
        stream.ReadExactly(header);
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        Assert.True(header[..8].SequenceEqual(signature), $"'{path}' is not a PNG file.");
        Assert.True(header[12..16].SequenceEqual("IHDR"u8), $"'{path}' has no PNG IHDR header.");
        return (
            BinaryPrimitives.ReadInt32BigEndian(header[16..20]),
            BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static double CalculateWhiteContrast(double lightness, double chroma, double hue)
    {
        double hueRadians = hue * Math.PI / 180;
        double a = chroma * Math.Cos(hueRadians);
        double b = chroma * Math.Sin(hueRadians);
        double lRoot = lightness + (0.3963377774 * a) + (0.2158037573 * b);
        double mRoot = lightness - (0.1055613458 * a) - (0.0638541728 * b);
        double sRoot = lightness - (0.0894841775 * a) - (1.291485548 * b);
        double l = lRoot * lRoot * lRoot;
        double m = mRoot * mRoot * mRoot;
        double s = sRoot * sRoot * sRoot;
        double red = Math.Clamp((4.0767416621 * l) - (3.3077115913 * m) + (0.2309699292 * s), 0, 1);
        double green = Math.Clamp((-1.2684380046 * l) + (2.6097574011 * m) - (0.3413193965 * s), 0, 1);
        double blue = Math.Clamp((-0.0041960863 * l) - (0.7034186147 * m) + (1.707614701 * s), 0, 1);
        double luminance = (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);
        return 1.05 / (luminance + 0.05);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the JitHub repository root from the test output directory.");
    }
}
