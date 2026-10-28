using System.Globalization;

namespace MarkdownRenderer.Math;

/// <summary>
/// Immutable limits for native formula parsing and scene generation. Lower limits
/// may be supplied by a host handling untrusted Markdown.
/// </summary>
public sealed class MathProcessingOptions
{
    private const int HardMaximumParserNestingDepth = 128;
    private CultureInfo? _localizationCulture;

    /// <summary>Gets the default bounded processing configuration.</summary>
    public static MathProcessingOptions Default { get; } = new();

    /// <summary>Creates a processing configuration.</summary>
    public MathProcessingOptions(
        int maximumTexLength = 64 * 1024,
        int maximumNestingDepth = 128,
        int maximumSceneCommands = 100_000,
        int maximumPathOperations = 1_000_000,
        float maximumSceneDimension = 1_000_000,
        float fontSize = 16,
        long maximumWorkingMemoryBytes = 32L * 1024 * 1024,
        TimeSpan? maximumProcessingTime = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTexLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumNestingDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSceneCommands);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPathOperations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumWorkingMemoryBytes);
        MathSceneValidation.ThrowIfNotFiniteOrNegative(maximumSceneDimension, nameof(maximumSceneDimension));
        MathSceneValidation.ThrowIfNotFiniteOrNegative(fontSize, nameof(fontSize));
        if (maximumSceneDimension == 0)
            throw new ArgumentOutOfRangeException(nameof(maximumSceneDimension));
        if (fontSize == 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));

        TimeSpan processingTime = maximumProcessingTime ?? TimeSpan.FromSeconds(2);
        if (processingTime <= TimeSpan.Zero || processingTime.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumProcessingTime),
                "The cooperative processing deadline must be positive and supported by CancellationTokenSource.");
        }

        MaximumTexLength = maximumTexLength;
        MaximumNestingDepth = maximumNestingDepth;
        MaximumSceneCommands = maximumSceneCommands;
        MaximumPathOperations = maximumPathOperations;
        MaximumSceneDimension = maximumSceneDimension;
        FontSize = fontSize;
        MaximumWorkingMemoryBytes = maximumWorkingMemoryBytes;
        MaximumProcessingTime = processingTime;
    }

    /// <summary>Gets the maximum number of UTF-16 code units accepted between delimiters.</summary>
    public int MaximumTexLength { get; }

    /// <summary>
    /// Gets the maximum unescaped brace and recursive command-argument nesting
    /// depth. Native parser recursion is additionally capped at an internal
    /// stack-safe maximum.
    /// </summary>
    public int MaximumNestingDepth { get; }

    // BuildInternal has one root frame in addition to recursive argument/group
    // frames. Clamp even explicitly relaxed host policy to a stack-safe ceiling.
    internal int MaximumParserRecursionDepth =>
        System.Math.Min(MaximumNestingDepth, HardMaximumParserNestingDepth) + 1;

    /// <summary>Gets the maximum number of scene drawing commands.</summary>
    public int MaximumSceneCommands { get; }

    /// <summary>Gets the maximum total number of vector path operations.</summary>
    public int MaximumPathOperations { get; }

    /// <summary>Gets the maximum width or height of a generated scene.</summary>
    public float MaximumSceneDimension { get; }

    /// <summary>Gets the formula font size in device-independent points.</summary>
    public float FontSize { get; }

    /// <summary>
    /// Gets the maximum conservatively accounted bytes for parser/layout working
    /// state plus retained immutable scene data.
    /// </summary>
    public long MaximumWorkingMemoryBytes { get; }

    /// <summary>
    /// Gets the whole-operation deadline for host-callback admission,
    /// localization, diagnostics, accessibility formatting, parsing, font
    /// initialization, layout, and scene generation.
    /// </summary>
    public TimeSpan MaximumProcessingTime { get; }

    /// <summary>
    /// Gets the culture used for diagnostics and accessible text. When omitted,
    /// the math processor or engine extension snapshots
    /// <see cref="CultureInfo.CurrentUICulture"/> at construction time.
    /// </summary>
    public CultureInfo? LocalizationCulture
    {
        get => _localizationCulture;
        init => _localizationCulture = value is null
            ? null
            : CultureInfo.ReadOnly((CultureInfo)value.Clone());
    }
}
