using System.Collections.ObjectModel;
using System.Globalization;

namespace MarkdownRenderer.Mermaid;

/// <summary>Resolves localized Mermaid diagnostics without exposing package resources.</summary>
public interface IMermaidStringProvider
{
    /// <summary>
    /// Returns the final localized value for a stable key and its immutable
    /// arguments, or <see langword="null"/> to use the built-in English fallback.
    /// Implementations must be thread-safe and return stable values for the
    /// lifetime of the renderer that owns them.
    /// </summary>
    string? GetString(
        string resourceKey,
        CultureInfo culture,
        IReadOnlyList<object?> arguments);
}

/// <summary>Stable localization keys supplied to <see cref="IMermaidStringProvider"/>.</summary>
public static class MermaidStringKeys
{
    public const string InvalidOptions = "Mermaid.Diagnostic.InvalidOptions";
    public const string ElkLayoutUnavailable = "Mermaid.Diagnostic.ElkLayoutUnavailable";
    public const string SourceNull = "Mermaid.Diagnostic.SourceNull";
    public const string SourceInvalidUtf16 = "Mermaid.Diagnostic.SourceInvalidUtf16";
    public const string SourceTooLarge = "Mermaid.Diagnostic.SourceTooLarge";
    public const string RenderCancelled = "Mermaid.Diagnostic.RenderCancelled";
    public const string RenderTimedOut = "Mermaid.Diagnostic.RenderTimedOut";
    public const string RenderOverloaded = "Mermaid.Diagnostic.RenderOverloaded";
    public const string EngineUnavailable = "Mermaid.Diagnostic.EngineUnavailable";
    public const string EngineWrongArchitecture = "Mermaid.Diagnostic.EngineWrongArchitecture";
    public const string EngineBecameUnavailable = "Mermaid.Diagnostic.EngineBecameUnavailable";
    public const string EngineIncompatibleAbi = "Mermaid.Diagnostic.EngineIncompatibleAbi";
    public const string SceneTooLarge = "Mermaid.Diagnostic.SceneTooLarge";
    public const string SceneInvalid = "Mermaid.Diagnostic.SceneInvalid";
    public const string NativeSourceRejected = "Mermaid.Diagnostic.NativeSourceRejected";
    public const string NativeBudgetExceeded = "Mermaid.Diagnostic.NativeBudgetExceeded";
    public const string NativeLayoutUnsupported = "Mermaid.Diagnostic.NativeLayoutUnsupported";
    public const string NativeFailure = "Mermaid.Diagnostic.NativeFailure";
    public const string NativeDiagnostic = "Mermaid.Diagnostic.NativeDiagnostic";
    public const string RendererUnavailable = "Mermaid.Diagnostic.RendererUnavailable";
    public const string DiagramRenderFailed = "Mermaid.Diagnostic.DiagramRenderFailed";
    public const string SceneRepresentationFailed = "Mermaid.Diagnostic.SceneRepresentationFailed";
}

/// <summary>
/// Contains the trust boundary around host localization. A faulty provider can
/// never suppress a renderer diagnostic or exact-source fallback.
/// </summary>
internal static class MermaidStringResolver
{
    private const int MaximumLocalizedStringLength = 4096;
    private static readonly IReadOnlyList<object?> EmptyArguments =
        new ReadOnlyCollection<object?>(Array.Empty<object?>());

    internal static string Resolve(
        IMermaidStringProvider? provider,
        string key,
        CultureInfo culture,
        string englishFormat,
        params object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(englishFormat);

        IReadOnlyList<object?> immutableArguments = arguments.Length == 0
            ? EmptyArguments
            : new ReadOnlyCollection<object?>((object?[])arguments.Clone());
        if (provider is not null)
        {
            try
            {
                string? candidate = provider.GetString(key, culture, immutableArguments);
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    candidate.Length <= MaximumLocalizedStringLength)
                {
                    return candidate;
                }
            }
            catch
            {
                // Host localization is optional and must never suppress the
                // deterministic diagnostic or exact-source fallback.
            }
        }

        try
        {
            return string.Format(culture, englishFormat, arguments);
        }
        catch (FormatException)
        {
            return englishFormat;
        }
    }
}
