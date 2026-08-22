using System;
using System.Text;

namespace JitHub.WinUI.Views.Controls.Common;

public enum MarkdownHostKind
{
    Conversation,
    Comment,
    RepositoryReadme,
    ProfileReadme,
    EditorPreview,
}

public static class MarkdownHostContract
{
    public const string Conversation = nameof(MarkdownHostKind.Conversation);
    public const string Comment = nameof(MarkdownHostKind.Comment);
    public const string RepositoryReadme = nameof(MarkdownHostKind.RepositoryReadme);
    public const string ProfileReadme = nameof(MarkdownHostKind.ProfileReadme);
    public const string EditorPreview = nameof(MarkdownHostKind.EditorPreview);

    public static MarkdownHostKind Parse(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out MarkdownHostKind kind)
            ? kind
            : MarkdownHostKind.Conversation;

    public static string GetSurfaceColorToken(string? value) => Parse(value) switch
    {
        MarkdownHostKind.Conversation => "AppCanvasInset",
        MarkdownHostKind.Comment => "AppCanvasInset",
        MarkdownHostKind.RepositoryReadme => "AppCanvas",
        MarkdownHostKind.ProfileReadme => "AppSurfaceSubtle",
        MarkdownHostKind.EditorPreview => "AppSurface",
        _ => "AppSurface",
    };

    public static string GetSurfaceFallback(string? value, bool dark) => Parse(value) switch
    {
        MarkdownHostKind.Conversation => dark ? "#11130F" : "#EDEDED",
        MarkdownHostKind.Comment => dark ? "#11130F" : "#EDEDED",
        MarkdownHostKind.RepositoryReadme => dark ? "#171914" : "#F3F3F3",
        MarkdownHostKind.ProfileReadme => dark ? "#252B25" : "#F0F0F0",
        MarkdownHostKind.EditorPreview => dark ? "#212621" : "#FAFAFA",
        _ => dark ? "#212621" : "#FAFAFA",
    };

    public static string GetAutomationName(string? value) => Parse(value) switch
    {
        MarkdownHostKind.Conversation => "Conversation Markdown content",
        MarkdownHostKind.Comment => "Comment Markdown content",
        MarkdownHostKind.RepositoryReadme => "Repository README Markdown content",
        MarkdownHostKind.ProfileReadme => "Profile README Markdown content",
        MarkdownHostKind.EditorPreview => "Markdown preview content",
        _ => "Markdown content",
    };

    public static string GetTelemetrySection(string? value) => Parse(value) switch
    {
        MarkdownHostKind.Conversation => "conversation",
        MarkdownHostKind.Comment => "comments",
        MarkdownHostKind.RepositoryReadme or MarkdownHostKind.ProfileReadme => "readme",
        MarkdownHostKind.EditorPreview => "preview",
        _ => "conversation",
    };

    public static string GetAutomationId(string? value) => Parse(value) switch
    {
        MarkdownHostKind.Conversation => "MarkdownHost_Conversation",
        MarkdownHostKind.Comment => "MarkdownHost_Comment",
        MarkdownHostKind.RepositoryReadme => "MarkdownHost_RepositoryReadme",
        MarkdownHostKind.ProfileReadme => "MarkdownHost_ProfileReadme",
        MarkdownHostKind.EditorPreview => "MarkdownHost_EditorPreview",
        _ => "MarkdownHost_Conversation",
    };

    public static string GetAutomationId(string? value, string? instanceId)
    {
        string baseId = GetAutomationId(value);
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return baseId;
        }

        StringBuilder suffix = new(instanceId.Length);
        foreach (char character in instanceId)
        {
            suffix.Append(char.IsAsciiLetterOrDigit(character) ? character : '_');
        }

        string normalized = suffix.ToString().Trim('_');
        return normalized.Length == 0 ? baseId : $"{baseId}_{normalized}";
    }
}
