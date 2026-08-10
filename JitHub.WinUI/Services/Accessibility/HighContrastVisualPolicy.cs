namespace JitHub.Services.Accessibility;

public static class HighContrastVisualPolicy
{
    public const string AccentBrushKey = "AppAccentBrush";
    public const string AccentForegroundBrushKey = "AppAccentForegroundBrush";
    public const string CanvasBrushKey = "AppCanvasBrush";
    public const string InkBrushKey = "AppInkBrush";
    public const string LabelDarkTextBrushKey = "AppLabelDarkTextBrush";
    public const string LabelLightTextBrushKey = "AppLabelLightTextBrush";

    public static RepositoryLabelBrushPolicy GetRepositoryLabelPolicy(
        bool isHighContrast,
        bool hasSourceColor,
        bool useDarkText) =>
        isHighContrast
            ? new RepositoryLabelBrushPolicy(AccentBrushKey, AccentForegroundBrushKey)
            : new RepositoryLabelBrushPolicy(
                BackgroundResourceKey: null,
                !hasSourceColor
                    ? InkBrushKey
                    : useDarkText ? LabelDarkTextBrushKey : LabelLightTextBrushKey);

    public static string? GetContributionCellBrushKey(bool isHighContrast, int contributionCount) =>
        isHighContrast
            ? contributionCount > 0 ? AccentBrushKey : CanvasBrushKey
            : null;

    public static string GetContributionFocusBrushKey(bool isHighContrast, int contributionCount) =>
        isHighContrast
            ? contributionCount > 0 ? AccentForegroundBrushKey : AccentBrushKey
            : InkBrushKey;
}

public readonly record struct RepositoryLabelBrushPolicy(
    string? BackgroundResourceKey,
    string ForegroundResourceKey);
