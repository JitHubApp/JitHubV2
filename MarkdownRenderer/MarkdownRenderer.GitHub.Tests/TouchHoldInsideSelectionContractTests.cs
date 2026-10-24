using System.Text.RegularExpressions;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// P1-034: a touch long-press inside a committed selection must keep that
/// selection (its release then opens the selection's context commands) rather
/// than replacing it with the word under the finger. Live Sandbox C10 observed
/// the drag-extended range collapse to its first word. Source-order contract;
/// the live C10 case remains the behavioral proof.
/// </summary>
public sealed class TouchHoldInsideSelectionContractTests
{
    [Fact]
    public void HoldInsideCommittedSelectionKeepsItBeforeAnyWordSelection()
    {
        string body = MethodBody(ReadControl(), "private void ProcessHolding(ref PointerInput e)");
        Match inside = Regex.Match(body, @"_selection\.IsActive\s*&&\s*IsPointInsideSelection\(snapshot,\s*point\)");
        int wordSelection = body.IndexOf("TrySelectWordForTouchContext(", StringComparison.Ordinal);

        Assert.True(inside.Success, "ProcessHolding must test whether the hold lands inside the committed selection.");
        Assert.True(wordSelection > inside.Index, "The inside-selection branch must run before word selection.");
        string branch = body[inside.Index..wordSelection];
        Assert.Contains("return;", branch, StringComparison.Ordinal);
        Assert.Contains("SuppressTouchTap(point, e.TouchTapGestureEpoch);", branch, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true;", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpandSelectionToWord", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void HoldInsideSelectionUsesTheSameContainmentAsTapAndContextRequests()
    {
        // Tap and ContextRequested already preserve a selection the point lies
        // inside; hold must use the identical containment query.
        string source = ReadControl();
        Assert.True(Regex.Matches(source, @"IsPointInsideSelection\(snapshot,\s*(point|pt)\)").Count >= 3);
    }

    private static string ReadControl()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException("Could not locate MarkdownRendererControl.cs.");
    }

    private static string MethodBody(string source, string signature)
    {
        int member = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find {signature}");
        int open = source.IndexOf('{', member + signature.Length);
        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0) return source[open..(index + 1)];
        }
        throw new InvalidOperationException("Unbalanced source block.");
    }
}
