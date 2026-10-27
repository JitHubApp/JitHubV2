using System.Text;
using Xunit.Sdk;

namespace MarkdownRenderer.PublicApi.Tests;

/// <summary>
/// Compares facts found in the built assemblies with a reviewed list and
/// reports both directions, so an intentional change names every line to edit.
/// </summary>
internal static class ReviewedContract
{
    public static void AssertMatches(string contract, IEnumerable<string> reviewed, IEnumerable<string> actual)
    {
        var expected = new SortedSet<string>(reviewed, StringComparer.Ordinal);
        var found = new SortedSet<string>(actual, StringComparer.Ordinal);
        string[] unreviewed = found.Except(expected, StringComparer.Ordinal).ToArray();
        string[] missing = expected.Except(found, StringComparer.Ordinal).ToArray();
        if (unreviewed.Length == 0 && missing.Length == 0)
            return;

        var message = new StringBuilder();
        message.Append(contract).AppendLine(" changed. Update the reviewed list only for an intentional public API change.");
        foreach (string line in unreviewed)
            message.Append("  + ").AppendLine(line);
        foreach (string line in missing)
            message.Append("  - ").AppendLine(line);
        throw new XunitException(message.ToString());
    }
}
