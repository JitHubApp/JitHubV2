namespace MarkdownRenderer.Extensions;

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
