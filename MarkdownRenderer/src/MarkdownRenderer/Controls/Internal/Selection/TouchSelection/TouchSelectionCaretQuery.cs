
namespace MarkdownRenderer.Controls.Internal.Selection.TouchSelection;

internal readonly struct TouchSelectionCaretQuery
{
    public TouchSelectionCaretQuery(int CharacterIndex, bool TrailingSideOfCharacter)
    {
        this.CharacterIndex = CharacterIndex;
        this.TrailingSideOfCharacter = TrailingSideOfCharacter;
    }

    public int CharacterIndex { get; init; }
    public bool TrailingSideOfCharacter { get; init; }
}
