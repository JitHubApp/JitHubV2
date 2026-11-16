namespace MarkdownRenderer.Extensions.Internal;

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
