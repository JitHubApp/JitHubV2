using System.Text.Json;

namespace MarkdownRenderer.Conformance;

public sealed class ConformanceExceptionAllowlist
{
    public const int CurrentSchemaVersion = 1;
    private readonly IReadOnlyDictionary<(string Suite, int Example), ConformanceExceptionEntry> _entries;

    private ConformanceExceptionAllowlist(
        IReadOnlyDictionary<(string Suite, int Example), ConformanceExceptionEntry> entries)
    {
        _entries = entries;
    }

    public IReadOnlyCollection<ConformanceExceptionEntry> Entries => _entries.Values.ToArray();

    public static ConformanceExceptionAllowlist Empty { get; } = new(
        new Dictionary<(string, int), ConformanceExceptionEntry>());

    public static ConformanceExceptionAllowlist Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = JsonSerializer.Deserialize<ConformanceExceptionFile>(
            File.ReadAllBytes(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The conformance exception file is empty.");

        if (file.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported exception schema version {file.SchemaVersion}.");

        var entries = new Dictionary<(string, int), ConformanceExceptionEntry>();
        foreach (var entry in file.Entries)
        {
            Validate(entry);
            if (!entries.TryAdd((entry.Suite, entry.Example), entry))
                throw new InvalidDataException($"Duplicate exception {entry.Suite} example {entry.Example}.");
        }

        return new ConformanceExceptionAllowlist(entries);
    }

    public bool TryGet(string suite, int example, out ConformanceExceptionEntry? entry)
    {
        bool found = _entries.TryGetValue((suite, example), out var value);
        entry = value;
        return found;
    }

    private static void Validate(ConformanceExceptionEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Suite) || entry.Example <= 0)
            throw new InvalidDataException("Each exception must identify a suite and positive example number.");
        if (string.IsNullOrWhiteSpace(entry.Reason))
            throw new InvalidDataException($"Exception {entry.Suite}/{entry.Example} requires a reason.");
        if (!Uri.TryCreate(entry.Issue, UriKind.Absolute, out var issue) ||
            issue.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(issue.Host))
        {
            throw new InvalidDataException(
                $"Exception {entry.Suite}/{entry.Example} must link to an HTTPS issue.");
        }
    }
}
