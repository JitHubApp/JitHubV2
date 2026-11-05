using System;

namespace JitHub.Services.Markdown;

/// <summary>Opens a GitHub link inside JitHub instead of the browser.</summary>
internal interface IMarkdownRouteNavigator
{
    /// <summary>Opens <paramref name="uri"/> as an in-app route when JitHub has one for it.</summary>
    /// <param name="uri">The resolved GitHub link.</param>
    /// <returns><see langword="true"/> when the link is now the current in-app route.</returns>
    bool TryOpen(Uri uri);
}
