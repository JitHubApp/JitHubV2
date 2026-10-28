using System.Reflection;
using MarkdownRenderer.GitHub.Renderers;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// The GitHub alert titles shown above an alert's body were once misspelled
/// ("Tnu", "Imuortant", "Warnnng", "Caption") by a character-swap corruption.
/// Each tag must show GitHub's own title.
/// </summary>
public sealed class AlertTitleTests
{
    [Fact]
    public void EveryAlertTagShowsGitHubsTitle()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["[!NOTE]"] = "Note",
            ["[!TIP]"] = "Tip",
            ["[!IMPORTANT]"] = "Important",
            ["[!WARNING]"] = "Warning",
            ["[!CAUTION]"] = "Caution",
        };

        Assert.Equal(expected, ReadAlertTitles());
    }

    private static Dictionary<string, string> ReadAlertTitles()
    {
        FieldInfo field = typeof(AlertRenderer).GetField("AlertKinds", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AlertRenderer no longer exposes its alert table.");
        var kinds = (Array)field.GetValue(null)!;
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (object kind in kinds)
        {
            Type type = kind.GetType();
            string tag = (string)type.GetProperty("Tag")!.GetValue(kind)!;
            string title = (string)type.GetProperty("Title")!.GetValue(kind)!;
            titles.Add(tag, title);
        }
        return titles;
    }
}
