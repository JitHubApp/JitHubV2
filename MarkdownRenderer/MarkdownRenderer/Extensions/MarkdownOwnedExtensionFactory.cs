using System;

namespace MarkdownRenderer.Extensions;

/// <summary>
/// Recreates an extension and its captured disposable service for each engine.
/// The factory itself is immutable and may be reused by any number of builders.
/// </summary>
internal sealed class MarkdownOwnedExtensionFactory
{
    private readonly Func<MarkdownOwnedExtension> _create;

    internal MarkdownOwnedExtensionFactory(
        string extensionId,
        Func<MarkdownOwnedExtension> create)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ExtensionId = extensionId.Trim();
        _create = create ?? throw new ArgumentNullException(nameof(create));
    }

    internal string ExtensionId { get; }

    internal MarkdownBoundOwnedExtension Create()
    {
        MarkdownOwnedExtension owned = _create() ??
            throw new InvalidOperationException(
                $"The owned markdown extension factory '{ExtensionId}' returned null.");
        try
        {
            // A custom Id getter is executable user code. Snapshot it exactly
            // once while this method still owns failure cleanup so a throwing
            // or stateful getter cannot leak the freshly created resource or
            // mask the validation result through a second read.
            string? createdExtensionId = owned.Extension.Id;
            string? validatedExtensionId = createdExtensionId?.Trim();
            if (validatedExtensionId is null ||
                !string.Equals(validatedExtensionId, ExtensionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The owned markdown extension factory '{ExtensionId}' created extension '{createdExtensionId}'.");
            }

            return new MarkdownBoundOwnedExtension(
                owned.Extension,
                owned.Resource,
                validatedExtensionId);
        }
        catch
        {
            DisposeIgnoringFailure(owned.Resource);
            throw;
        }
    }

    private static void DisposeIgnoringFailure(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch
        {
            // Preserve the deterministic factory-validation failure.
        }
    }
}

/// <summary>
/// Carries the factory-validated identifier with its engine-local extension so
/// binding never needs to execute the extension's <see cref="IMarkdownExtension.Id"/>
/// getter a second time.
/// </summary>
internal readonly record struct MarkdownBoundOwnedExtension(
    IMarkdownExtension Extension,
    IDisposable Resource,
    string ExtensionId);
