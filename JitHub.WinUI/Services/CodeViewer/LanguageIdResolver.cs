using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JitHub.Services.CodeViewer;

/// <summary>
/// Loads language-map.json at startup and resolves WinUIEdit HighlightingLanguage ids
/// by filename, extension, or shebang sniff.
/// </summary>
public sealed class LanguageIdResolver : ILanguageIdResolver
{
    private const string FallbackLanguage = "plaintext";

    private readonly Dictionary<string, string> _extensionMap;
    private readonly Dictionary<string, string> _filenameMap;
    private readonly Dictionary<string, string> _interpreterMap;

    public LanguageIdResolver()
    {
        string mapPath = Path.Combine(AppContext.BaseDirectory, "Assets", "CodeViewer", "language-map.json");

        LanguageMapData? data = null;
        try
        {
            using var stream = new FileStream(mapPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            data = JsonSerializer.Deserialize(stream, LanguageMapJsonContext.Default.LanguageMapData);
        }
        catch
        {
            // If the asset is missing at runtime, fall back to empty maps.
        }

        _extensionMap = BuildCaseInsensitive(data?.Extensions);
        _filenameMap = BuildCaseInsensitive(data?.Filenames);
        _interpreterMap = BuildCaseInsensitive(data?.Interpreters);
    }

    /// <inheritdoc/>
    public string Resolve(string fileName, ReadOnlySpan<byte> contentSniff = default)
    {
        if (string.IsNullOrEmpty(fileName))
            return FallbackLanguage;

        string baseName = Path.GetFileName(fileName);

        // 1. Exact filename match (case-insensitive).
        if (_filenameMap.TryGetValue(baseName, out string? lang))
            return lang;

        // 2. Longest-matching extension.
        string? extLang = ResolveByExtension(baseName);
        if (extLang is not null)
            return extLang;

        // 3. Shebang sniff — only when content provided and extension unknown.
        if (!contentSniff.IsEmpty)
        {
            string? shebangLang = ResolveByShebang(contentSniff);
            if (shebangLang is not null)
                return shebangLang;
        }

        return FallbackLanguage;
    }

    /// <inheritdoc/>
    public bool IsKnown(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return false;

        string baseName = Path.GetFileName(fileName);
        if (_filenameMap.ContainsKey(baseName))
            return true;

        return ResolveByExtension(baseName) is not null;
    }

    private string? ResolveByExtension(string baseName)
    {
        // Try progressively shorter extensions, e.g. ".min.js" -> ".js"
        ReadOnlySpan<char> remaining = baseName.AsSpan();
        int dotIdx = remaining.IndexOf('.');
        if (dotIdx < 0)
            return null;

        // Iterate from the first dot to get compound extensions handled longest-first.
        string? bestLang = null;
        while (true)
        {
            int nextDot = remaining.Slice(dotIdx).IndexOf('.');
            if (nextDot < 0) break;

            string ext = new string(remaining.Slice(dotIdx + nextDot));
            if (_extensionMap.TryGetValue(ext, out string? lang))
            {
                bestLang = lang;
                break; // longest match found
            }
            dotIdx += nextDot + 1;
            if (dotIdx >= remaining.Length) break;
        }

        if (bestLang is null)
        {
            // simple single-extension fallback
            string simpleExt = Path.GetExtension(baseName);
            if (!string.IsNullOrEmpty(simpleExt) && _extensionMap.TryGetValue(simpleExt, out string? sl))
                bestLang = sl;
        }

        return bestLang;
    }

    private string? ResolveByShebang(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2 || bytes[0] != (byte)'#' || bytes[1] != (byte)'!')
            return null;

        // Find end of first line.
        int end = bytes.IndexOfAny((byte)'\n', (byte)'\r');
        ReadOnlySpan<byte> firstLine = end >= 0 ? bytes.Slice(2, end - 2) : bytes.Slice(2);

        string line = Encoding.UTF8.GetString(firstLine).Trim();

        // Strip "/usr/bin/env " prefix.
        const string envPrefix = "/usr/bin/env ";
        if (line.StartsWith(envPrefix, StringComparison.Ordinal))
            line = line.Substring(envPrefix.Length).Trim();

        // Take just the basename of the interpreter path.
        int lastSlash = line.LastIndexOfAny(['/', '\\']);
        if (lastSlash >= 0)
            line = line.Substring(lastSlash + 1).Trim();

        // Drop arguments.
        int space = line.IndexOf(' ');
        if (space >= 0)
            line = line.Substring(0, space);

        if (string.IsNullOrEmpty(line))
            return null;

        return _interpreterMap.TryGetValue(line, out string? lang) ? lang : null;
    }

    private static Dictionary<string, string> BuildCaseInsensitive(Dictionary<string, string>? source)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (source is null) return dict;
        foreach (var (k, v) in source)
            dict[k] = v;
        return dict;
    }
}

// DTO for the JSON map file.
internal sealed class LanguageMapData
{
    [JsonPropertyName("extensions")]
    public Dictionary<string, string>? Extensions { get; init; }

    [JsonPropertyName("filenames")]
    public Dictionary<string, string>? Filenames { get; init; }

    [JsonPropertyName("interpreters")]
    public Dictionary<string, string>? Interpreters { get; init; }
}

[JsonSerializable(typeof(LanguageMapData))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class LanguageMapJsonContext : JsonSerializerContext
{
}
