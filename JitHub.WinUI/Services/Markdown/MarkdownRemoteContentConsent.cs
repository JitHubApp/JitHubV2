using System;
using MarkdownRenderer.Images;

namespace JitHub.Services.Markdown;

/// <summary>
/// Tracks third-party image permission for exactly one logical Markdown document.
/// A host reuse or identity change revokes permission immediately.
/// </summary>
public sealed class MarkdownRemoteContentConsent
{
    private string _currentIdentity = CreateAnonymousIdentity();
    private string? _grantedIdentity;

    public string CurrentIdentity => _currentIdentity;

    public bool IsGranted => string.Equals(
        _currentIdentity,
        _grantedIdentity,
        StringComparison.Ordinal);

    public void Activate(MarkdownDocumentSource? source)
    {
        string identity = source?.GetConsentIdentity() ?? string.Empty;
        if (identity.Length == 0)
        {
            ResetForHostReuse();
            return;
        }

        if (string.Equals(_currentIdentity, identity, StringComparison.Ordinal))
        {
            return;
        }

        _currentIdentity = identity;
        _grantedIdentity = null;
    }

    public void ResetForHostReuse()
    {
        _currentIdentity = CreateAnonymousIdentity();
        _grantedIdentity = null;
    }

    public void Grant() => _grantedIdentity = _currentIdentity;

    public void Revoke() => _grantedIdentity = null;

    private static string CreateAnonymousIdentity() => $"anonymous:{Guid.NewGuid():N}";
}
