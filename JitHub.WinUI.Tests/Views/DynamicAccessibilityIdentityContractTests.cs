using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class DynamicAccessibilityIdentityContractTests
{
    [Fact]
    public void EveryEmojiHostProvidesAnItemScopedIdentity()
    {
        string viewsRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Views");
        List<string> missing = [];
        foreach (string path in Directory.EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories))
        {
            if (path.EndsWith("EmojiButton.xaml", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("EmojiPanelButton.xaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (XElement element in document.Descendants().Where(static element =>
                         element.Name.LocalName is "EmojiButton" or "EmojiPanelButton"))
            {
                if (string.IsNullOrWhiteSpace(element.Attribute("AutomationInstanceId")?.Value))
                {
                    missing.Add($"{Path.GetRelativePath(viewsRoot, path)}: {element.Name.LocalName}");
                }
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void EmojiLauncherAndReactionIdsIncludeHostAndReactionScopes()
    {
        string button = Read("JitHub.WinUI", "Views", "Controls", "Common", "EmojiButton.xaml.cs");
        string panel = Read("JitHub.WinUI", "Views", "Controls", "Common", "EmojiPanelButton.xaml");
        string comment = Read("JitHub.WinUI", "Views", "Controls", "UserCommentBlock.xaml");
        string viewModel = Read("JitHub.WinUI", "ViewModels", "UserViewModel", "UserCommentBlockViewModel.cs");

        Assert.Contains("CreateScopedId(\n                \"EmojiReactionButton\",\n                automationInstanceId,\n                reaction.ToString())", button.Replace("\r", string.Empty), StringComparison.Ordinal);
        Assert.Equal(8, panel.Split("AutomationInstanceId=\"{x:Bind AutomationInstanceId", StringSplitOptions.None).Length - 1);
        Assert.Contains("GetLauncherAutomationId(AutomationInstanceId)", panel, StringComparison.Ordinal);
        Assert.Contains("ViewModel.HeaderReactionAutomationId", comment, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SummaryReactionAutomationId", comment, StringComparison.Ordinal);
        Assert.Contains("AutomationInstanceId=\"{x:Bind AutomationInstanceId", comment, StringComparison.Ordinal);
        Assert.Contains("SummaryReactionAutomationId)", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivityAndTimelineHyperlinksUseDistinctItemScopes()
    {
        string card = Read("JitHub.WinUI", "Views", "Controls", "App", "ActivityCard.xaml.cs");
        string sentence = Read("JitHub.WinUI", "Views", "Controls", "App", "ActivitySentenceLine.xaml.cs");
        string timeline = Read("JitHub.WinUI", "Views", "Controls", "PullRequest", "Conversation", "PullRequestTimelineItem.xaml.cs");
        string timelineModel = Read("JitHub.WinUI", "ViewModels", "PullRequestViewModels", "ConversationViewModels", "PullRequestTimelineItemViewModel.cs");

        Assert.Contains("ActivityScope()", card, StringComparison.Ordinal);
        Assert.Contains("ActivityScope()", sentence, StringComparison.Ordinal);
        Assert.Contains("item.AutomationScope", timeline, StringComparison.Ordinal);
        Assert.Contains("public string AutomationScope", timelineModel, StringComparison.Ordinal);
    }

    private static string Read(params string[] segments) => File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. segments]));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
