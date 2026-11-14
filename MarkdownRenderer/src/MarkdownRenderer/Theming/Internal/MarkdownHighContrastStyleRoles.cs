namespace MarkdownRenderer.Theming.Internal;

internal readonly struct MarkdownHighContrastStyleRoles
{
    public MarkdownHighContrastStyleRoles(
        MarkdownHighContrastColorRole Foreground,
        MarkdownHighContrastColorRole? Background = null,
        MarkdownHighContrastColorRole? AccentBar = null,
        bool Underline = false,
        bool Strikethrough = false)
    {
        this.Foreground = Foreground;
        this.Background = Background;
        this.AccentBar = AccentBar;
        this.Underline = Underline;
        this.Strikethrough = Strikethrough;
    }

    public readonly MarkdownHighContrastColorRole Foreground;

    public readonly MarkdownHighContrastColorRole? Background;

    public readonly MarkdownHighContrastColorRole? AccentBar;

    public readonly bool Underline;

    public readonly bool Strikethrough;
}
