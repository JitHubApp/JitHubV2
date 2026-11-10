using System;

namespace JitHub.Services.CodeViewer;

public interface ILanguageIdResolver
{
    /// <summary>
    /// Returns the WinUIEdit HighlightingLanguage id for the given file name.
    /// Pass <paramref name="contentSniff"/> (first bytes of file) to enable shebang detection.
    /// Falls back to "text".
    /// </summary>
    string Resolve(string fileName, ReadOnlySpan<byte> contentSniff = default);

    /// <summary>Returns true when the file name maps to a known language id.</summary>
    bool IsKnown(string fileName);
}
