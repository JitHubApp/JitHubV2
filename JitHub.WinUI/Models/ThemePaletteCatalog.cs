using System;
using System.Collections.Generic;

namespace JitHub.Models;

public static class ThemePaletteIds
{
    public const string JitHub = "jithub";
    public const string Windows11 = "windows-11";
    public const string VisualStudioCode = "visual-studio-code";
    public const string GitHub = "github";
    public const string Solarized = "solarized";
    public const string OneDarkPro = "one-dark-pro";
    public const string Dracula = "dracula";
    public const string Nord = "nord";
    public const string TokyoNight = "tokyo-night";
    public const string Catppuccin = "catppuccin";
    public const string Gruvbox = "gruvbox";
    public const string Monokai = "monokai";
    public const string Ayu = "ayu";
    public const string NightOwl = "night-owl";
    public const string RosePine = "rose-pine";
    public const string Everforest = "everforest";
    public const string Cobalt2 = "cobalt2";
    public const string Material = "material";
    public const string VisualStudio = "visual-studio";
    public const string QuietLight = "quiet-light";
}

public sealed record ThemePalettePreview(
    string Canvas,
    string Rail,
    string Surface,
    string Accent,
    string Ink);

public sealed record ThemePaletteTokenSet(
    ThemePalettePreview Preview,
    IReadOnlyDictionary<string, string> Colors);

public sealed record ThemePaletteDefinition(
    string Id,
    string ResourceKey,
    string Name,
    string Description,
    string? ResourceUri,
    ThemePalettePreview Light,
    ThemePalettePreview Dark,
    ThemePaletteTokenSet? GeneratedLight = null,
    ThemePaletteTokenSet? GeneratedDark = null)
{
    public bool IsGenerated => GeneratedLight is not null && GeneratedDark is not null;
}

public static class ThemePaletteCatalog
{
    public static IReadOnlyList<ThemePaletteDefinition> All { get; } =
    (ThemePaletteDefinition[])
    [
        Resource(ThemePaletteIds.JitHub, "JitHub", "JitHub", "The original jade JitHub palette.",
            "ms-appx:///Styles/Foundation/Tokens.Colors.xaml",
            new("#F3F4F4", "#ECEEEE", "#FFFFFF", "#256B52", "#1F2321"),
            new("#161916", "#121612", "#242A25", "#77B59A", "#F0F2EA")),
        Resource(ThemePaletteIds.Windows11, "Windows11", "Windows 11", "Neutral Windows surfaces with a familiar blue accent.",
            "ms-appx:///Styles/Foundation/Palettes/Tokens.Colors.Windows11.xaml",
            new("#F3F3F3", "#EFEFEF", "#FFFFFF", "#0067C0", "#1B1B1B"),
            new("#202020", "#1C1C1C", "#2D2D2D", "#60CDFF", "#F5F5F5")),
        Resource(ThemePaletteIds.VisualStudioCode, "VisualStudioCode", "Visual Studio Code", "Focused editor colors based on VS Code Modern.",
            "ms-appx:///Styles/Foundation/Palettes/Tokens.Colors.VisualStudioCode.xaml",
            new("#FFFFFF", "#F3F3F3", "#FFFFFF", "#005FB8", "#1F1F1F"),
            new("#1F1F1F", "#181818", "#2B2B2B", "#0078D4", "#D8D8D8")),
        Resource(ThemePaletteIds.GitHub, "GitHub", "GitHub", "Familiar repository colors based on GitHub.",
            "ms-appx:///Styles/Foundation/Palettes/Tokens.Colors.GitHub.xaml",
            new("#F6F8FA", "#F6F8FA", "#FFFFFF", "#0969DA", "#1F2328"),
            new("#0D1117", "#010409", "#1C2128", "#58A6FF", "#E6EDF3")),
        Resource(ThemePaletteIds.Solarized, "Solarized", "Solarized", "A carefully balanced low-glare color family.",
            "ms-appx:///Styles/Foundation/Palettes/Tokens.Colors.Solarized.xaml",
            new("#FDF6E3", "#F5EEDB", "#FFFBED", "#0F6F68", "#3F555C"),
            new("#002B36", "#00232C", "#0B414C", "#2AA198", "#EEE8D5")),
        Generated(ThemePaletteIds.OneDarkPro, "OneDarkPro", "One Dark Pro", "Balanced editor neutrals inspired by One Dark Pro.",
            Light("#FAFAFA", "#F3F4F6", "#FFFFFF", "#E5E7EB", "#EEF0F3", "#DDE8FF", "#C7CBD1", "#20252B", "#4B5563", "#2D64C8", "#2454AA", "#1E478F", "#FFFFFF"),
            Dark("#282C34", "#21252B", "#2C313C", "#1E2228", "#343A46", "#30415A", "#4B5263", "#F2F4F8", "#B8C0CC", "#61AFEF", "#7EC2F3", "#4B9BD5", "#121417")),
        Generated(ThemePaletteIds.Dracula, "Dracula", "Dracula", "Deep violet surfaces and vivid accents inspired by Dracula.",
            Light("#F8F8F2", "#EFEFF4", "#FFFFFF", "#E4E4EA", "#F0F0F5", "#E8DDF6", "#C8C7D0", "#282A36", "#535664", "#6D3FC0", "#5C34A5", "#4D2B8B", "#FFFFFF"),
            Dark("#282A36", "#21222C", "#303341", "#191A22", "#393C4B", "#403653", "#5A5D70", "#F8F8F2", "#D5D1E5", "#BD93F9", "#CBA8FB", "#A879E8", "#1B1625")),
        Generated(ThemePaletteIds.Nord, "Nord", "Nord", "Cool, quiet surfaces inspired by the Nord palette.",
            Light("#ECEFF4", "#E5E9F0", "#FFFFFF", "#D8DEE9", "#E4E9F0", "#D6E5EF", "#B8C0CC", "#2E3440", "#4C566A", "#3B6EA8", "#315E91", "#294F7A", "#FFFFFF"),
            Dark("#2E3440", "#242933", "#3B4252", "#1F242D", "#434B5D", "#394E5A", "#5E687B", "#ECEFF4", "#D8DEE9", "#88C0D0", "#9CCBD8", "#70AABA", "#162027")),
        Generated(ThemePaletteIds.TokyoNight, "TokyoNight", "Tokyo Night", "Calm indigo surfaces inspired by Tokyo Night.",
            Light("#F4F6FB", "#E9ECF5", "#FFFFFF", "#DDE1EC", "#E8ECF6", "#DCE5FA", "#BBC1D0", "#343B58", "#565A6E", "#34548A", "#2B4775", "#243B62", "#FFFFFF"),
            Dark("#1A1B26", "#16161E", "#24283B", "#11121A", "#2D3248", "#293B61", "#414868", "#E6EAF7", "#A9B1D6", "#7AA2F7", "#91B2FA", "#638CDF", "#131722")),
        Generated(ThemePaletteIds.Catppuccin, "Catppuccin", "Catppuccin", "Soft pastel surfaces inspired by Catppuccin Latte and Mocha.",
            Light("#EFF1F5", "#E6E9EF", "#FFFFFF", "#DCE0E8", "#E7EAF0", "#DCE6FA", "#BCC0CC", "#4C4F69", "#5C5F77", "#1E66F5", "#1956CE", "#1548AD", "#FFFFFF"),
            Dark("#1E1E2E", "#181825", "#313244", "#11111B", "#3B3C50", "#304267", "#585B70", "#F0F2FA", "#BAC2DE", "#89B4FA", "#A1C4FB", "#74A1E7", "#171925")),
        Generated(ThemePaletteIds.Gruvbox, "Gruvbox", "Gruvbox", "Warm, grounded colors inspired by Gruvbox.",
            Light("#FBF1C7", "#F2E5BC", "#FFF7D6", "#E8D8A7", "#F0E3B6", "#F3D7B5", "#BDAE93", "#3C3836", "#504945", "#9D3D00", "#843300", "#6F2B00", "#FFFFFF"),
            Dark("#282828", "#1D2021", "#3C3836", "#171918", "#45403C", "#4B3F28", "#665C54", "#F4E5C5", "#D5C4A1", "#D79921", "#E2AA38", "#BC8315", "#1A1712")),
        Generated(ThemePaletteIds.Monokai, "Monokai", "Monokai", "Crisp charcoal and vivid accents inspired by Monokai.",
            Light("#F8F8F2", "#EFEFE8", "#FFFFFF", "#E2E2DA", "#EEEEEA", "#F5DCE7", "#C9C9C0", "#272822", "#4F5048", "#B41455", "#981147", "#7F0E3C", "#FFFFFF"),
            Dark("#272822", "#1E1F1C", "#35362F", "#181916", "#3E3F37", "#4B2D3A", "#5A5B50", "#F8F8F2", "#D6D6C2", "#F92672", "#FA4A8A", "#D91D62", "#151612")),
        Generated(ThemePaletteIds.Ayu, "Ayu", "Ayu", "Clean neutrals and warm accents inspired by Ayu.",
            Light("#FAFAFA", "#F3F4F5", "#FFFFFF", "#E6E8EB", "#EFF1F3", "#F4E2CE", "#C8CCD2", "#4D5566", "#5C6773", "#945000", "#7D4300", "#693800", "#FFFFFF"),
            Dark("#0B0E14", "#070A0F", "#161B22", "#03060A", "#1D2430", "#3B3021", "#3D4757", "#F1EBD9", "#B3B1AD", "#FFB454", "#FFC374", "#E39B3F", "#17130D")),
        Generated(ThemePaletteIds.NightOwl, "NightOwl", "Night Owl", "High-clarity blues inspired by Night Owl.",
            Light("#FBFBFB", "#F0F3F8", "#FFFFFF", "#E2E7EF", "#EEF2F7", "#DCEAFF", "#C2CAD6", "#403F53", "#5F5E72", "#005A9C", "#004C84", "#003F6E", "#FFFFFF"),
            Dark("#011627", "#01111D", "#0B2942", "#000A12", "#123452", "#183D61", "#35556F", "#E8F0F7", "#A7B6C2", "#82AAFF", "#9AB9FF", "#6B96E8", "#101725")),
        Generated(ThemePaletteIds.RosePine, "RosePine", "Rose Pine", "Muted rose and violet tones inspired by Rose Pine.",
            Light("#FAF4ED", "#F2E9DE", "#FFFAF3", "#E8DED3", "#F0E8DF", "#ECDFF0", "#C8BFC0", "#575279", "#6E6A86", "#7A4B8A", "#674075", "#563661", "#FFFFFF"),
            Dark("#191724", "#12101B", "#26233A", "#0D0C14", "#302C46", "#3A3150", "#524F67", "#F2F0FA", "#C5C3D8", "#C4A7E7", "#D0B8EC", "#AE8FD4", "#191520")),
        Generated(ThemePaletteIds.Everforest, "Everforest", "Everforest", "Low-contrast natural colors inspired by Everforest.",
            Light("#FDF6E3", "#F3EAD3", "#FFFBEB", "#E5D9BE", "#F0E8D5", "#DFE8D3", "#B8B7A3", "#4F5B58", "#5F6C68", "#4F6F52", "#435E46", "#384F3B", "#FFFFFF"),
            Dark("#2D353B", "#232A2E", "#343F44", "#1E2428", "#3D494E", "#405044", "#59645E", "#EFE5CC", "#B7B0A0", "#A7C080", "#B7CD97", "#91AA6D", "#182018")),
        Generated(ThemePaletteIds.Cobalt2, "Cobalt2", "Cobalt2", "Saturated blue surfaces and gold accents inspired by Cobalt2.",
            Light("#F5F8FC", "#E8EFF8", "#FFFFFF", "#D8E3F0", "#E7EEF7", "#D8E8FA", "#B6C5D8", "#1F344D", "#4B5F73", "#004B9B", "#003F83", "#00356E", "#FFFFFF"),
            Dark("#12223A", "#0B192C", "#193452", "#07111F", "#21405F", "#4A431A", "#46627D", "#F5F8FF", "#C1D1E2", "#FFC600", "#FFD333", "#E2AE00", "#171A22")),
        Generated(ThemePaletteIds.Material, "Material", "Material", "Clear surfaces and teal-blue accents inspired by Material themes.",
            Light("#FAFAFA", "#F1F3F4", "#FFFFFF", "#E3E6E8", "#EEF1F2", "#D8EBF4", "#C1C7CA", "#263238", "#4E5D63", "#006EBD", "#005D9F", "#004E85", "#FFFFFF"),
            Dark("#212121", "#191919", "#2B2B2B", "#121212", "#343434", "#294542", "#565656", "#F2F5F5", "#BCC6C6", "#80CBC4", "#9AD7D1", "#69B7B0", "#14201F")),
        Generated(ThemePaletteIds.VisualStudio, "VisualStudio", "Visual Studio", "Classic IDE surfaces inspired by Visual Studio.",
            Light("#F5F5F5", "#E9E9E9", "#FFFFFF", "#DCDCDC", "#EEEEEE", "#E4DDF1", "#BEBEBE", "#1E1E1E", "#505050", "#5C2D91", "#4D267A", "#402066", "#FFFFFF"),
            Dark("#1E1E1E", "#181818", "#2D2D30", "#121212", "#363638", "#44344A", "#5A5A5C", "#F1F1F1", "#C8C8C8", "#C586C0", "#D19BCD", "#AE70A9", "#201820")),
        Generated(ThemePaletteIds.QuietLight, "QuietLight", "Quiet Light", "Soft paper-like surfaces inspired by Quiet Light.",
            Light("#F5F5F5", "#ECECEC", "#FFFFFF", "#DEDEDE", "#EFEFEF", "#DCEAF7", "#C4C4C4", "#333333", "#555555", "#0066B8", "#00569B", "#004882", "#FFFFFF"),
            Dark("#20242B", "#181C22", "#2B3038", "#12151A", "#343A44", "#2D415F", "#535D69", "#F2F4F7", "#BEC6D0", "#82AAFF", "#9AB9FF", "#6D97E5", "#151A22"))
    ];

    public static ThemePaletteDefinition Default => All[0];

    public static ThemePaletteDefinition Find(string? id)
    {
        string normalized = Normalize(id);
        foreach (ThemePaletteDefinition palette in All)
        {
            if (string.Equals(palette.Id, normalized, StringComparison.Ordinal))
            {
                return palette;
            }
        }

        return Default;
    }

    public static string Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return ThemePaletteIds.JitHub;
        }

        string candidate = id.Trim().Replace('_', '-').Replace(' ', '-').ToLowerInvariant();
        return candidate switch
        {
            "default" or "jithub-default" => ThemePaletteIds.JitHub,
            "windows" or "win11" => ThemePaletteIds.Windows11,
            "vscode" or "vs-code" => ThemePaletteIds.VisualStudioCode,
            "one-dark" or "onedark" => ThemePaletteIds.OneDarkPro,
            "rosepine" or "rose-pine-moon" => ThemePaletteIds.RosePine,
            _ when IsKnown(candidate) => candidate,
            _ => ThemePaletteIds.JitHub
        };
    }

    private static ThemePaletteDefinition Resource(string id, string resourceKey, string name, string description,
        string resourceUri, ThemePalettePreview light, ThemePalettePreview dark) =>
        new(id, resourceKey, name, description, resourceUri, light, dark);

    private static ThemePaletteDefinition Generated(string id, string resourceKey, string name, string description,
        ThemePaletteSeed light, ThemePaletteSeed dark)
    {
        ThemePaletteTokenSet lightTokens = CreateTokenSet(light);
        ThemePaletteTokenSet darkTokens = CreateTokenSet(dark);
        return new(id, resourceKey, name, description, null, lightTokens.Preview, darkTokens.Preview, lightTokens, darkTokens);
    }

    private static ThemePaletteSeed Light(string canvas, string rail, string raised, string inset, string hover,
        string selected, string outline, string ink, string muted, string accent, string accentHover,
        string accentPressed, string accentForeground) =>
        new(false, canvas, rail, raised, inset, hover, selected, outline, ink, muted, accent, accentHover, accentPressed, accentForeground);

    private static ThemePaletteSeed Dark(string canvas, string rail, string raised, string inset, string hover,
        string selected, string outline, string ink, string muted, string accent, string accentHover,
        string accentPressed, string accentForeground) =>
        new(true, canvas, rail, raised, inset, hover, selected, outline, ink, muted, accent, accentHover, accentPressed, accentForeground);

    private static ThemePaletteTokenSet CreateTokenSet(ThemePaletteSeed seed)
    {
        string transparent = seed.IsDark ? "#00000000" : "#00FFFFFF";
        string inverse = seed.IsDark ? seed.Canvas : "#FFFFFF";
        string actionForeground = seed.IsDark ? "#16181D" : "#FFFFFF";
        string warm = seed.IsDark ? "#E2B340" : "#805500";
        string danger = seed.IsDark ? "#FF7B72" : "#B42318";
        string success = seed.IsDark ? "#56D364" : "#147A3D";

        Dictionary<string, string> colors = new(StringComparer.Ordinal)
        {
            ["AppCanvasColor"] = seed.Canvas,
            ["AppCanvasMaterialTintColor"] = seed.Canvas,
            ["AppCanvasRaisedColor"] = seed.Raised,
            ["AppCanvasInsetColor"] = seed.Inset,
            ["AppRailColor"] = seed.Rail,
            ["AppRailMaterialTintColor"] = seed.Rail,
            ["AppSurfaceColor"] = seed.Raised,
            ["AppSurfaceSubtleColor"] = seed.Hover,
            ["AppCardColor"] = seed.Raised,
            ["AppInputColor"] = seed.Raised,
            ["AppInputHoverColor"] = seed.Hover,
            ["AppRowColor"] = transparent,
            ["AppRowHoverColor"] = seed.Hover,
            ["AppRowPressedColor"] = seed.Inset,
            ["AppRowSelectedColor"] = seed.Selected,
            ["AppRowHoverForegroundColor"] = seed.Ink,
            ["AppRowPressedForegroundColor"] = seed.Ink,
            ["AppRowSelectedForegroundColor"] = seed.Ink,
            ["AppSelectionColor"] = seed.Accent,
            ["AppSelectionForegroundColor"] = seed.AccentForeground,
            ["AppOverlayColor"] = seed.Raised,
            ["AppTransparentColor"] = transparent,
            ["AppPopupSurfaceColor"] = seed.Raised,
            ["AppPopupBorderColor"] = seed.Outline,
            ["AppReactionChipBackgroundColor"] = transparent,
            ["AppReactionChipHoverColor"] = seed.Hover,
            ["AppReactionChipPressedColor"] = seed.Inset,
            ["AppReactionChipBorderColor"] = seed.Outline,
            ["AppReactionChipForegroundColor"] = seed.Ink,
            ["AppReactionChipSelectedColor"] = seed.Selected,
            ["AppInkColor"] = seed.Ink,
            ["AppInkMutedColor"] = seed.Muted,
            ["AppInkSubtleColor"] = seed.Muted,
            ["AppInkInverseColor"] = inverse,
            ["AppLabelDarkTextColor"] = "#000000",
            ["AppLabelLightTextColor"] = "#FFFFFF",
            ["AppOutlineColor"] = seed.Outline,
            ["AppOutlineStrongColor"] = seed.Muted,
            ["AppHairlineColor"] = seed.Outline,
            ["AppAccentColor"] = seed.Accent,
            ["AppAccentHoverColor"] = seed.AccentHover,
            ["AppAccentPressedColor"] = seed.AccentPressed,
            ["AppAccentForegroundColor"] = seed.AccentForeground,
            ["AppWarmAccentColor"] = warm,
            ["AppWarmAccentForegroundColor"] = actionForeground,
            ["AppDangerColor"] = danger,
            ["AppDangerForegroundColor"] = actionForeground,
            ["AppSuccessColor"] = success,
            ["AppSuccessForegroundColor"] = actionForeground,
            ["AppShadowColor"] = "#000000",
            ["AppSmokeColor"] = seed.IsDark ? "#99000000" : "#88000000"
        };

        return new(new(seed.Canvas, seed.Rail, seed.Raised, seed.Accent, seed.Ink), colors);
    }

    private static bool IsKnown(string id)
    {
        foreach (ThemePaletteDefinition palette in All)
        {
            if (string.Equals(palette.Id, id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record ThemePaletteSeed(bool IsDark, string Canvas, string Rail, string Raised, string Inset,
        string Hover, string Selected, string Outline, string Ink, string Muted, string Accent,
        string AccentHover, string AccentPressed, string AccentForeground);
}
