namespace MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;

/// <summary>
/// A plain model of a XAML resource dictionary graph: local and ambient
/// values, theme dictionaries and merged dictionaries. It counts how
/// often its value keys are enumerated.
/// </summary>
internal sealed class TestResourceDictionary
{
    internal Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, object> AmbientValues { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, TestResourceDictionary> Themes { get; } = new(StringComparer.Ordinal);
    internal List<TestResourceDictionary> Merged { get; } = [];
    internal int KeyEnumerationCount { get; private set; }

    internal IEnumerable<string> EnumerateValueKeys()
    {
        KeyEnumerationCount++;
        return Values.Keys;
    }

    internal bool TryGetEffectiveValue(string key, out object value)
        => Values.TryGetValue(key, out value!) || AmbientValues.TryGetValue(key, out value!);
}
