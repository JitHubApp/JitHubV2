using System.Globalization;

namespace MarkdownRenderer.Mermaid;

/// <summary>Immutable settings used by a <see cref="MermaidRenderer"/> instance.</summary>
public sealed record MermaidRenderOptions
{
    private CultureInfo? _diagnosticCulture;

    /// <summary>The default maximum retained weight of decoded Mermaid scenes.</summary>
    public const long DefaultSceneCacheBudgetBytes = 32L * 1024 * 1024;
    internal const long AbsoluteMaxSceneCacheBudgetBytes = 1024L * 1024 * 1024;

    public static MermaidRenderOptions Default { get; } = new();

    public MermaidRenderBudgets Budgets { get; init; } = MermaidRenderBudgets.Default;

    public MermaidLayoutMode Layout { get; init; } = MermaidLayoutMode.NativeDefault;

    /// <summary>
    /// Gets the pinned Mermaid theme used to compile the scene. This selection is explicit and
    /// does not follow the host application's light/dark setting automatically.
    /// </summary>
    public MermaidThemeVariant Theme { get; init; } = MermaidThemeVariant.Default;

    /// <summary>
    /// Gets the optional immutable localization service used for diagnostics.
    /// The provider must be thread-safe and stable for the renderer lifetime.
    /// </summary>
    public IMermaidStringProvider? StringProvider { get; init; }

    /// <summary>
    /// Gets the culture used for diagnostic localization. When omitted, the
    /// renderer snapshots <see cref="CultureInfo.CurrentUICulture"/> at
    /// construction time. An assigned culture is cloned and frozen immediately,
    /// so later caller mutation cannot change a reusable builder or cached result.
    /// </summary>
    public CultureInfo? DiagnosticCulture
    {
        get => _diagnosticCulture;
        init => _diagnosticCulture = value is null
            ? null
            : CultureInfo.ReadOnly((CultureInfo)value.Clone());
    }

    /// <summary>
    /// Gets the renderer-local retained-scene cache budget. Zero disables
    /// retention while preserving in-flight request deduplication.
    /// </summary>
    public long SceneCacheBudgetBytes { get; init; } = DefaultSceneCacheBudgetBytes;

    /// <summary>
    /// Allows explicitly configured limits above the secure defaults. Absolute implementation limits still apply.
    /// </summary>
    public bool AllowBudgetIncreases { get; init; }

    public bool TryValidate(out string? error)
    {
        if (Budgets is null)
        {
            error = $"{nameof(Budgets)} cannot be null.";
            return false;
        }

        if (!Enum.IsDefined(Layout) || !Enum.IsDefined(Theme))
        {
            error = "The Mermaid layout or theme value is not defined.";
            return false;
        }

        if (SceneCacheBudgetBytes is < 0 or > AbsoluteMaxSceneCacheBudgetBytes)
        {
            error = $"{nameof(SceneCacheBudgetBytes)} must be between 0 and {AbsoluteMaxSceneCacheBudgetBytes}.";
            return false;
        }

        error = Budgets.Validate(AllowBudgetIncreases);
        return error is null;
    }
}
