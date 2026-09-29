using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MarkdownRenderer.Conformance;

public sealed class CorpusStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private static readonly Regex ExampleFence = new(
        "^(`{10,}) example(?<extensions>(?:\\s+\\S+)*)\\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _httpClient;

    public CorpusStore(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MarkdownRenderer-Conformance/1.0");
    }

    public async Task<IReadOnlyList<SpecExample>> LoadAsync(
        CorpusDefinition definition,
        string cacheDirectory,
        bool offline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);

        Directory.CreateDirectory(cacheDirectory);
        string extension = definition.SourceFormat == CorpusSourceFormat.SpecJson ? ".spec.json" : ".spec.txt";
        string sourcePath = Path.Combine(cacheDirectory, definition.Id + extension);
        byte[] payload;

        if (File.Exists(sourcePath))
        {
            payload = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            VerifyChecksum(payload, definition.SourceSha256, sourcePath);
        }
        else
        {
            if (offline)
                throw new FileNotFoundException($"Pinned corpus is not cached: {sourcePath}", sourcePath);

            payload = await _httpClient.GetByteArrayAsync(definition.SourceUri, cancellationToken).ConfigureAwait(false);
            VerifyChecksum(payload, definition.SourceSha256, definition.SourceUri.ToString());
            await File.WriteAllBytesAsync(sourcePath, payload, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<SpecExample> examples = definition.SourceFormat switch
        {
            CorpusSourceFormat.SpecJson => ParseSpecJson(payload),
            CorpusSourceFormat.AnnotatedSpec => ParseAnnotatedSpec(payload),
            _ => throw new InvalidOperationException($"Unsupported corpus format {definition.SourceFormat}."),
        };

        if (definition.SourceFormat == CorpusSourceFormat.AnnotatedSpec)
        {
            string normalizedPath = Path.Combine(cacheDirectory, definition.Id + ".spec.json");
            byte[] normalized = JsonSerializer.SerializeToUtf8Bytes(examples, JsonOptions);
            await File.WriteAllBytesAsync(normalizedPath, normalized, cancellationToken).ConfigureAwait(false);
        }

        return examples;
    }

    public static IReadOnlyList<SpecExample> LoadVerifiedSpecJson(
        string path,
        string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] payload = File.ReadAllBytes(path);
        VerifyChecksum(payload, expectedSha256, path);
        return ParseSpecJson(payload);
    }

    public static IReadOnlyList<SpecExample> ParseSpecJson(ReadOnlySpan<byte> payload)
    {
        var examples = JsonSerializer.Deserialize<List<SpecJsonExample>>(payload, JsonOptions)
            ?? throw new InvalidDataException("The spec.json corpus did not contain an example array.");

        return Array.AsReadOnly(examples.Select(static value => new SpecExample(
            value.Example,
            value.Section ?? string.Empty,
            value.Markdown ?? string.Empty,
            value.Html ?? string.Empty,
            value.StartLine,
            value.EndLine)
        {
            Extensions = Array.AsReadOnly(value.Extensions ?? Array.Empty<string>()),
        }).ToArray());
    }

    public static IReadOnlyList<SpecExample> ParseAnnotatedSpec(ReadOnlySpan<byte> payload)
    {
        string text = Encoding.UTF8.GetString(payload).Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] lines = text.Split('\n');
        var examples = new List<SpecExample>();
        string section = string.Empty;
        int exampleNumber = 0;

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].TrimEnd('\r');
            var match = ExampleFence.Match(line);
            if (!match.Success)
            {
                if (line.StartsWith('#') && line.AsSpan().TrimStart('#').StartsWith(' '))
                    section = line.TrimStart('#').Trim();
                continue;
            }

            string fence = match.Groups[1].Value;
            exampleNumber++;
            string[] extensions = match.Groups["extensions"].Value
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int startLine = index + 1;
            var markdown = new StringBuilder();
            var html = new StringBuilder();
            index++;
            while (index < lines.Length && lines[index] != ".")
            {
                markdown.Append(lines[index].Replace('→', '\t')).Append('\n');
                index++;
            }

            if (index >= lines.Length)
                throw new InvalidDataException($"Example at line {startLine} has no markdown/HTML separator.");

            index++;
            while (index < lines.Length && !string.Equals(lines[index].TrimEnd('\r'), fence, StringComparison.Ordinal))
            {
                html.Append(lines[index].Replace('→', '\t')).Append('\n');
                index++;
            }

            if (index >= lines.Length)
                throw new InvalidDataException($"Example at line {startLine} has no closing fence.");

            // The reference test runner numbers every example but deliberately
            // omits fences tagged "disabled". Preserve that numbering so a
            // report entry always identifies the same example as the spec.
            if (!extensions.Contains("disabled", StringComparer.Ordinal))
            {
                examples.Add(new SpecExample(
                    exampleNumber,
                    section,
                    markdown.ToString(),
                    html.ToString(),
                    startLine,
                    index + 1)
                {
                    Extensions = Array.AsReadOnly(extensions),
                });
            }
        }

        if (examples.Count == 0)
            throw new InvalidDataException("The annotated specification did not contain any examples.");

        return Array.AsReadOnly(examples.ToArray());
    }

    public static void VerifyChecksum(ReadOnlySpan<byte> payload, string expectedSha256, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        string actual = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"SHA-256 mismatch for {source}. Expected {expectedSha256.ToLowerInvariant()}, got {actual}.");
        }
    }

    private sealed class SpecJsonExample
    {
        public string? Markdown { get; init; }
        public string? Html { get; init; }
        public int Example { get; init; }
        public int StartLine { get; init; }
        public int EndLine { get; init; }
        public string? Section { get; init; }
        public string[]? Extensions { get; init; }
    }
}
