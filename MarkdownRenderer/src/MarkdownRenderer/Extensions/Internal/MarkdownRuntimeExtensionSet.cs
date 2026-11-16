using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Extensions.Internal;

internal readonly struct MarkdownRuntimeExtensionSet(
    MarkdownExtensionSet Extensions,
    IReadOnlyList<IDisposable> OwnedResources,
    MarkdownExtensionCallbackLifetime? CallbackLifetime)
{
    public readonly MarkdownExtensionSet Extensions = Extensions;
    public readonly IReadOnlyList<IDisposable> OwnedResources = OwnedResources;
    public readonly MarkdownExtensionCallbackLifetime? CallbackLifetime = CallbackLifetime;
}
