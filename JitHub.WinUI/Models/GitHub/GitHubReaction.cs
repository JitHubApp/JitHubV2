using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using JitHub.WinUI.Helpers;

namespace JitHub.Models.GitHub;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubReaction
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("user")]
    public GitHubActor User { get; set; } = new();
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubReactionSummary
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("+1")]
    public int PlusOne { get; set; }

    [JsonPropertyName("-1")]
    public int MinusOne { get; set; }

    [JsonPropertyName("laugh")]
    public int Laugh { get; set; }

    [JsonPropertyName("hooray")]
    public int Hooray { get; set; }

    [JsonPropertyName("confused")]
    public int Confused { get; set; }

    [JsonPropertyName("heart")]
    public int Heart { get; set; }

    [JsonPropertyName("rocket")]
    public int Rocket { get; set; }

    [JsonPropertyName("eyes")]
    public int Eyes { get; set; }

    [JsonIgnore]
    public string DisplayText => GitHubReactionTextFormatter.FormatSummary(this);

    [JsonIgnore]
    public IReadOnlyList<GitHubReactionChip> Chips => GitHubReactionCatalog.Options
        .Select(option => new GitHubReactionChip(
            option.Content,
            option.Emoji,
            GetCount(option.Content),
            option.Label))
        .Where(static chip => chip.Count > 0)
        .ToArray();

    public int GetCount(string content) => content switch
    {
        "+1" => PlusOne,
        "-1" => MinusOne,
        "laugh" => Laugh,
        "hooray" => Hooray,
        "confused" => Confused,
        "heart" => Heart,
        "rocket" => Rocket,
        "eyes" => Eyes,
        _ => 0
    };
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubReactionOption
{
    public GitHubReactionOption()
    {
    }

    public GitHubReactionOption(string content, string emoji, string label)
    {
        Content = content;
        Emoji = emoji;
        Label = label;
    }

    public string Content { get; set; } = string.Empty;
    public string Emoji { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubReactionChip
{
    public GitHubReactionChip()
    {
    }

    public GitHubReactionChip(string content, string emoji, int count, string label)
    {
        Content = content;
        Emoji = emoji;
        Count = count;
        Label = label;
    }

    public string Content { get; set; } = string.Empty;
    public string Emoji { get; set; } = string.Empty;
    public int Count { get; set; }
    public string Label { get; set; } = string.Empty;

    [JsonIgnore]
    public string AutomationName => GitHubReactionTextFormatter.FormatPickerLabel(Content, Count);
}

public static class GitHubReactionCatalog
{
    public static IReadOnlyList<GitHubReactionOption> Options { get; } =
    (GitHubReactionOption[])
    [
        new("+1", "\U0001F44D", GitHubReactionTextFormatter.GetReactionLabel("+1")),
        new("-1", "\U0001F44E", GitHubReactionTextFormatter.GetReactionLabel("-1")),
        new("laugh", "\U0001F604", GitHubReactionTextFormatter.GetReactionLabel("laugh")),
        new("hooray", "\U0001F389", GitHubReactionTextFormatter.GetReactionLabel("hooray")),
        new("confused", "\U0001F615", GitHubReactionTextFormatter.GetReactionLabel("confused")),
        new("heart", "\u2764\uFE0F", GitHubReactionTextFormatter.GetReactionLabel("heart")),
        new("rocket", "\U0001F680", GitHubReactionTextFormatter.GetReactionLabel("rocket")),
        new("eyes", "\U0001F440", GitHubReactionTextFormatter.GetReactionLabel("eyes"))
    ];
}

public static class GitHubReactionTextFormatter
{
    public static string FormatSummary(GitHubReactionSummary summary)
    {
        if (summary.TotalCount <= 0)
        {
            return LocalizedResourceText.GetString("GitHubReaction.NoneSummary", "Reactions: none");
        }

        List<string> parts = [];
        AddPart(parts, "+1", summary.PlusOne);
        AddPart(parts, "-1", summary.MinusOne);
        AddPart(parts, "laugh", summary.Laugh);
        AddPart(parts, "hooray", summary.Hooray);
        AddPart(parts, "confused", summary.Confused);
        AddPart(parts, "heart", summary.Heart);
        AddPart(parts, "rocket", summary.Rocket);
        AddPart(parts, "eyes", summary.Eyes);

        return parts.Count == 0
            ? LocalizedResourceText.GetString("GitHubReaction.NoneSummary", "Reactions: none")
            : LocalizedResourceText.Format("GitHubReaction.SummaryFormat", "Reactions: {0}", string.Join(", ", parts));
    }

    public static string FormatPickerLabel(string content, int count)
    {
        return LocalizedResourceText.Format(
            "GitHubReaction.PickerLabelFormat",
            "{0} ({1})",
            GetReactionLabel(content),
            count);
    }

    public static string GetReactionLabel(string content)
    {
        return content switch
        {
            "+1" => LocalizedResourceText.GetString("GitHubReaction.LabelPlusOne", "+1"),
            "-1" => LocalizedResourceText.GetString("GitHubReaction.LabelMinusOne", "-1"),
            "laugh" => LocalizedResourceText.GetString("GitHubReaction.LabelLaugh", "Laugh"),
            "hooray" => LocalizedResourceText.GetString("GitHubReaction.LabelHooray", "Hooray"),
            "confused" => LocalizedResourceText.GetString("GitHubReaction.LabelConfused", "Confused"),
            "heart" => LocalizedResourceText.GetString("GitHubReaction.LabelHeart", "Heart"),
            "rocket" => LocalizedResourceText.GetString("GitHubReaction.LabelRocket", "Rocket"),
            "eyes" => LocalizedResourceText.GetString("GitHubReaction.LabelEyes", "Eyes"),
            _ => content
        };
    }

    private static void AddPart(List<string> parts, string label, int count)
    {
        if (count > 0)
        {
            parts.Add(LocalizedResourceText.Format(
                "GitHubReaction.PartFormat",
                "{0} {1}",
                GetReactionLabel(label),
                count));
        }
    }
}
