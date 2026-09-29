using System;

namespace MarkdownRenderer.Extensions;

/// <summary>
/// Captures one immutable builder operation so owned factories can be bound in
/// the exact position where they were declared without re-running reusable
/// extension configuration code.
/// </summary>
internal sealed class MarkdownExtensionRegistration
{
    private MarkdownExtensionRegistration(
        MarkdownExtensionRegistrationKind kind,
        string? identifier = null,
        MarkdownNodeRenderer? synchronousRenderer = null,
        MarkdownAsyncNodeRenderer? asynchronousRenderer = null,
        MarkdownOwnedExtensionFactory? ownedFactory = null)
    {
        Kind = kind;
        Identifier = identifier;
        SynchronousRenderer = synchronousRenderer;
        AsynchronousRenderer = asynchronousRenderer;
        OwnedFactory = ownedFactory;
    }

    internal MarkdownExtensionRegistrationKind Kind { get; }

    internal string? Identifier { get; }

    internal MarkdownNodeRenderer? SynchronousRenderer { get; }

    internal MarkdownAsyncNodeRenderer? AsynchronousRenderer { get; }

    internal MarkdownOwnedExtensionFactory? OwnedFactory { get; }

    internal static MarkdownExtensionRegistration Extension(string id) =>
        new(MarkdownExtensionRegistrationKind.Extension, identifier: id);

    internal static MarkdownExtensionRegistration Feature(string id) =>
        new(MarkdownExtensionRegistrationKind.Feature, identifier: id);

    internal static MarkdownExtensionRegistration SynchronousBlock(
        string syntaxKind,
        MarkdownNodeRenderer renderer) =>
        new(
            MarkdownExtensionRegistrationKind.SynchronousBlock,
            identifier: syntaxKind,
            synchronousRenderer: renderer);

    internal static MarkdownExtensionRegistration AsynchronousBlock(
        string syntaxKind,
        MarkdownAsyncNodeRenderer renderer) =>
        new(
            MarkdownExtensionRegistrationKind.AsynchronousBlock,
            identifier: syntaxKind,
            asynchronousRenderer: renderer);

    internal static MarkdownExtensionRegistration SynchronousInline(
        string syntaxKind,
        MarkdownNodeRenderer renderer) =>
        new(
            MarkdownExtensionRegistrationKind.SynchronousInline,
            identifier: syntaxKind,
            synchronousRenderer: renderer);

    internal static MarkdownExtensionRegistration AsynchronousInline(
        string syntaxKind,
        MarkdownAsyncNodeRenderer renderer) =>
        new(
            MarkdownExtensionRegistrationKind.AsynchronousInline,
            identifier: syntaxKind,
            asynchronousRenderer: renderer);

    internal static MarkdownExtensionRegistration Owned(
        MarkdownOwnedExtensionFactory factory) =>
        new(MarkdownExtensionRegistrationKind.OwnedFactory, ownedFactory: factory);
}

internal enum MarkdownExtensionRegistrationKind
{
    Extension,
    Feature,
    SynchronousBlock,
    AsynchronousBlock,
    SynchronousInline,
    AsynchronousInline,
    OwnedFactory,
}
