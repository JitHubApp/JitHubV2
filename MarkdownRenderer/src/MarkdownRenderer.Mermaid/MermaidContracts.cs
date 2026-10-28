using System.Collections.ObjectModel;
using System.Globalization;

namespace MarkdownRenderer.Mermaid;

/// <summary>Describes the terminal state of a Mermaid render request.</summary>
public enum MermaidRenderStatus
{
    Success,
    EngineUnavailable,
    InvalidInput,
    InvalidOptions,
    BudgetExceeded,
    Cancelled,
    TimedOut,
    UnsupportedLayout,
    InvalidScene,
    NativeFailure,
    Overloaded,
}

/// <summary>Severity attached to a parser, layout, or scene diagnostic.</summary>
public enum MermaidDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

/// <summary>
/// A half-open range in the Mermaid diagram source, measured in UTF-16 code
/// units. Markdown extension integration maps this range into the containing
/// Markdown document before publishing a <c>MarkdownDiagnostic</c>.
/// </summary>
public readonly record struct MermaidSourceRange(int Start, int Length)
{
    public int End => checked(Start + Length);

    public bool IsValid => Start >= 0 && Length >= 0 && Start <= int.MaxValue - Length;
}

/// <summary>A stable, backend-neutral Mermaid diagnostic.</summary>
public sealed record MermaidDiagnostic(
    string Code,
    MermaidDiagnosticSeverity Severity,
    string Message,
    MermaidSourceRange? SourceRange = null);

/// <summary>The content to show atomically when a native diagram cannot be rendered.</summary>
public sealed record MermaidCodeBlockFallback(string Language, string Source);

/// <summary>The result of a native Mermaid request. A non-success result always carries the original code-block fallback.</summary>
public sealed class MermaidRenderResult
{
    private readonly ReadOnlyCollection<MermaidDiagnostic> _diagnostics;

    internal MermaidRenderResult(
        MermaidRenderStatus status,
        MermaidScene? scene,
        IEnumerable<MermaidDiagnostic>? diagnostics,
        string originalSource)
    {
        Status = status;
        Scene = scene;
        _diagnostics = Array.AsReadOnly(diagnostics?.ToArray() ?? []);
        Fallback = new MermaidCodeBlockFallback("mermaid", originalSource);
    }

    public MermaidRenderStatus Status { get; }

    public MermaidScene? Scene { get; }

    public IReadOnlyList<MermaidDiagnostic> Diagnostics => _diagnostics;

    public MermaidCodeBlockFallback Fallback { get; }

    public bool ShouldUseFallback => Status != MermaidRenderStatus.Success || Scene is null;
}

/// <summary>Native layout selection. ELK is represented so hosts receive a deterministic unsupported result.</summary>
public enum MermaidLayoutMode
{
    NativeDefault,
    Dagre,
    TidyTree,
    ForceDirected,
    Elk,
}

/// <summary>
/// A Mermaid theme request interpreted by the native presentation stage. <see cref="Default"/>
/// and <see cref="Light"/> use Mermaid's documented default palette; the remaining named values
/// use the corresponding pinned Mermaid palette. Windows High Contrast colors are applied by the
/// host drawing layer without changing these authored colors.
/// </summary>
public enum MermaidThemeVariant
{
    Default,
    Light,
    Dark,
    HighContrast,
    Forest,
    Neutral,
}

/// <summary>Resource limits enforced before and during native rendering and MMIR decoding.</summary>
public sealed record MermaidRenderBudgets
{
    public const int DefaultMaxSourceBytes = 256 * 1024;
    public const int DefaultMaxNodes = 2_000;
    public const int DefaultMaxEdges = 4_000;
    public const int DefaultMaxDepth = 64;
    public const int DefaultMaxLabelBytes = 16 * 1024;
    public const int DefaultMaxSceneBytes = 16 * 1024 * 1024;
    public const long DefaultMaxWorkingMemoryBytes = 64L * 1024 * 1024;
    public const int DefaultMaxConcurrentRenders = 4;
    public const int DefaultMaxOutstandingRenders = 16;
    public const long DefaultMaxOutstandingSourceBytes = 4L * 1024 * 1024;

    internal const int AbsoluteMaxSourceBytes = 16 * 1024 * 1024;
    internal const int AbsoluteMaxNodes = 1_000_000;
    internal const int AbsoluteMaxEdges = 2_000_000;
    internal const int AbsoluteMaxDepth = 1_024;
    internal const int AbsoluteMaxLabelBytes = 1024 * 1024;
    internal const int AbsoluteMaxSceneBytes = 256 * 1024 * 1024;
    internal const long AbsoluteMaxWorkingMemoryBytes = 2L * 1024 * 1024 * 1024;
    internal const int AbsoluteMaxOutstandingRenders = 4_096;
    internal const long AbsoluteMaxOutstandingSourceBytes = 1024L * 1024 * 1024;

    public static MermaidRenderBudgets Default { get; } = new();

    public int MaxSourceBytes { get; init; } = DefaultMaxSourceBytes;

    public int MaxNodes { get; init; } = DefaultMaxNodes;

    public int MaxEdges { get; init; } = DefaultMaxEdges;

    public int MaxDepth { get; init; } = DefaultMaxDepth;

    public int MaxLabelBytes { get; init; } = DefaultMaxLabelBytes;

    public int MaxSceneBytes { get; init; } = DefaultMaxSceneBytes;

    public long MaxWorkingMemoryBytes { get; init; } = DefaultMaxWorkingMemoryBytes;

    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(2);

    public int MaxConcurrentRenders { get; init; } = DefaultMaxConcurrentRenders;

    /// <summary>Maximum running plus queued unique render operations.</summary>
    public int MaxOutstandingRenders { get; init; } = DefaultMaxOutstandingRenders;

    /// <summary>Maximum retained UTF-8 bytes across running plus queued unique sources.</summary>
    public long MaxOutstandingSourceBytes { get; init; } = DefaultMaxOutstandingSourceBytes;

    internal string? Validate(bool allowIncreases)
    {
        if (MaxSourceBytes is <= 0 or > AbsoluteMaxSourceBytes)
        {
            return $"{nameof(MaxSourceBytes)} must be between 1 and {AbsoluteMaxSourceBytes}.";
        }

        if (MaxNodes is <= 0 or > AbsoluteMaxNodes || MaxEdges is <= 0 or > AbsoluteMaxEdges ||
            MaxDepth is <= 0 or > AbsoluteMaxDepth || MaxLabelBytes is <= 0 or > AbsoluteMaxLabelBytes ||
            MaxSceneBytes is <= 0 or > AbsoluteMaxSceneBytes ||
            MaxWorkingMemoryBytes is <= 0 or > AbsoluteMaxWorkingMemoryBytes ||
            MaxSceneBytes > MaxWorkingMemoryBytes ||
            Deadline <= TimeSpan.Zero || Deadline > TimeSpan.FromMinutes(2) ||
            MaxConcurrentRenders is <= 0 or > 64 ||
            MaxOutstandingRenders is <= 0 or > AbsoluteMaxOutstandingRenders ||
            MaxOutstandingSourceBytes is <= 0 or > AbsoluteMaxOutstandingSourceBytes ||
            MaxConcurrentRenders > MaxOutstandingRenders ||
            MaxSourceBytes > MaxOutstandingSourceBytes)
        {
            return "One or more Mermaid resource limits are outside the supported range.";
        }

        if (!allowIncreases &&
            (MaxSourceBytes > DefaultMaxSourceBytes || MaxNodes > DefaultMaxNodes ||
             MaxEdges > DefaultMaxEdges || MaxDepth > DefaultMaxDepth ||
              MaxLabelBytes > DefaultMaxLabelBytes || MaxSceneBytes > DefaultMaxSceneBytes ||
              MaxWorkingMemoryBytes > DefaultMaxWorkingMemoryBytes ||
              Deadline > TimeSpan.FromSeconds(2) || MaxConcurrentRenders > DefaultMaxConcurrentRenders ||
              MaxOutstandingRenders > DefaultMaxOutstandingRenders ||
              MaxOutstandingSourceBytes > DefaultMaxOutstandingSourceBytes))
        {
            return "Increasing a Mermaid limit above its secure default requires AllowBudgetIncreases.";
        }

        return null;
    }
}

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
