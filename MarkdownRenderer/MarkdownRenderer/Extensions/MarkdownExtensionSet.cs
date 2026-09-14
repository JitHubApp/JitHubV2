using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace MarkdownRenderer.Extensions;

/// <summary>
/// An immutable snapshot of declarative markdown extension registrations.
/// </summary>
/// <remarks>
/// Registry lookup is thread-safe. Renderer delegates are supplied by
/// extensions and may be invoked concurrently; their implementations and
/// captured state are responsible for thread safety.
/// </remarks>
public sealed class MarkdownExtensionSet
{
    private readonly IReadOnlyDictionary<string, MarkdownAsyncNodeRenderer> _blockRenderers;
    private readonly IReadOnlyDictionary<string, MarkdownAsyncNodeRenderer> _inlineRenderers;
    private readonly IReadOnlyDictionary<string, MarkdownNodeRenderer> _synchronousBlockRenderers;
    private readonly IReadOnlyDictionary<string, MarkdownNodeRenderer> _synchronousInlineRenderers;
    private readonly MarkdownExtensionSet? _reusableTemplate;

    internal MarkdownExtensionSet(
        IEnumerable<string> extensionIds,
        IEnumerable<string> features,
        IReadOnlyDictionary<string, MarkdownAsyncNodeRenderer> blockRenderers,
        IReadOnlyDictionary<string, MarkdownAsyncNodeRenderer> inlineRenderers,
        IReadOnlyDictionary<string, MarkdownNodeRenderer> synchronousBlockRenderers,
        IReadOnlyDictionary<string, MarkdownNodeRenderer> synchronousInlineRenderers,
        IEnumerable<MarkdownOwnedExtensionFactory> ownedExtensionFactories,
        IEnumerable<MarkdownExtensionRegistration> registrations,
        MarkdownExtensionSet? reusableTemplate = null,
        MarkdownExtensionCallbackLifetime? callbackLifetime = null)
    {
        ExtensionIds = CopyStrings(extensionIds);
        Features = CopyStrings(features);
        _blockRenderers = CopyRenderers(blockRenderers, callbackLifetime);
        _inlineRenderers = CopyRenderers(inlineRenderers, callbackLifetime);
        _synchronousBlockRenderers = CopySynchronousRenderers(
            synchronousBlockRenderers,
            callbackLifetime);
        _synchronousInlineRenderers = CopySynchronousRenderers(
            synchronousInlineRenderers,
            callbackLifetime);
        BlockSyntaxKinds = CopyStrings(_blockRenderers.Keys);
        InlineSyntaxKinds = CopyStrings(_inlineRenderers.Keys);
        OwnedExtensionFactories = Array.AsReadOnly(
            new List<MarkdownOwnedExtensionFactory>(ownedExtensionFactories).ToArray());
        Registrations = Array.AsReadOnly(
            new List<MarkdownExtensionRegistration>(registrations).ToArray());
        _reusableTemplate = reusableTemplate;
    }

    /// <summary>Gets an empty extension set.</summary>
    public static MarkdownExtensionSet Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<string>(),
        new Dictionary<string, MarkdownAsyncNodeRenderer>(StringComparer.Ordinal),
        new Dictionary<string, MarkdownAsyncNodeRenderer>(StringComparer.Ordinal),
        new Dictionary<string, MarkdownNodeRenderer>(StringComparer.Ordinal),
        new Dictionary<string, MarkdownNodeRenderer>(StringComparer.Ordinal),
        Array.Empty<MarkdownOwnedExtensionFactory>(),
        Array.Empty<MarkdownExtensionRegistration>());

    /// <summary>
    /// Resource-independent registrations and factories from which this set's
    /// engine-local runtime snapshot was built.
    /// </summary>
    internal MarkdownExtensionSet ReusableTemplate => _reusableTemplate ?? this;

    internal IReadOnlyList<MarkdownOwnedExtensionFactory> OwnedExtensionFactories { get; }

    internal IReadOnlyList<MarkdownExtensionRegistration> Registrations { get; }

    /// <summary>
    /// Binds a fresh disposable service for every owned extension factory.
    /// The returned runtime set may be inspected publicly, while future
    /// Include operations recover this set's reusable template and factories.
    /// </summary>
    internal MarkdownRuntimeExtensionSet BindOwnedExtensions()
    {
        MarkdownExtensionSet template = ReusableTemplate;
        if (template.OwnedExtensionFactories.Count == 0)
        {
            return new MarkdownRuntimeExtensionSet(
                template,
                Array.Empty<IDisposable>(),
                CallbackLifetime: null);
        }

        var builder = new MarkdownExtensionBuilder();
        var resources = new List<IDisposable>(template.OwnedExtensionFactories.Count);
        var activeFactories = new HashSet<string>(StringComparer.Ordinal);
        var boundFactories = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            BindRegistrationsIteratively(
                template.Registrations,
                builder,
                resources,
                activeFactories,
                boundFactories);

            var callbackLifetime = new MarkdownExtensionCallbackLifetime();
            return new MarkdownRuntimeExtensionSet(
                builder.BuildRuntime(template, callbackLifetime),
                resources.AsReadOnly(),
                callbackLifetime);
        }
        catch
        {
            for (int index = resources.Count - 1; index >= 0; index--)
                DisposeIgnoringFailure(resources[index]);
            throw;
        }
    }

    private static void BindRegistrationsIteratively(
        IReadOnlyList<MarkdownExtensionRegistration> registrations,
        MarkdownExtensionBuilder builder,
        List<IDisposable> resources,
        HashSet<string> activeFactories,
        HashSet<string> boundFactories)
    {
        var pending = new Stack<BindingFrame>();
        pending.Push(new BindingFrame(registrations, activeFactoryId: null));
        while (pending.Count != 0)
        {
            BindingFrame frame = pending.Peek();
            if (frame.Index == frame.Registrations.Count)
            {
                pending.Pop();
                if (frame.ActiveFactoryId is not null)
                    activeFactories.Remove(frame.ActiveFactoryId);
                continue;
            }

            MarkdownExtensionRegistration registration = frame.Registrations[frame.Index++];
            if (registration.Kind != MarkdownExtensionRegistrationKind.OwnedFactory)
            {
                builder.ReplayRegistration(registration);
                continue;
            }

            MarkdownOwnedExtensionFactory factory = registration.OwnedFactory!;
            if (activeFactories.Contains(factory.ExtensionId))
            {
                throw new InvalidOperationException(
                    $"The owned markdown extension factory '{factory.ExtensionId}' forms a configuration cycle.");
            }
            if (!boundFactories.Add(factory.ExtensionId))
            {
                throw new InvalidOperationException(
                    $"The markdown extension '{factory.ExtensionId}' is already registered.");
            }

            MarkdownBoundOwnedExtension owned = factory.Create();
            bool transferred = false;
            try
            {
                // Configure into an isolated registration buffer. Replaying that
                // buffer lets nested owned factories be expanded in their exact
                // declaration position without mutating the traversal source.
                var ownedBuilder = new MarkdownExtensionBuilder();
                ownedBuilder.AddValidated(owned.Extension, owned.ExtensionId);
                MarkdownExtensionSet configured = ownedBuilder.Build();

                resources.Add(owned.Resource);
                transferred = true;
                activeFactories.Add(factory.ExtensionId);
                pending.Push(new BindingFrame(
                    configured.Registrations,
                    factory.ExtensionId));
            }
            finally
            {
                if (!transferred)
                    DisposeIgnoringFailure(owned.Resource);
            }
        }
    }

    /// <summary>Gets registered extension identifiers in configuration order.</summary>
    public IReadOnlyList<string> ExtensionIds { get; }
    /// <summary>Gets feature markers in first-registration order.</summary>
    public IReadOnlyList<string> Features { get; }
    /// <summary>Gets registered block syntax kinds.</summary>
    public IReadOnlyList<string> BlockSyntaxKinds { get; }
    /// <summary>Gets registered inline syntax kinds.</summary>
    public IReadOnlyList<string> InlineSyntaxKinds { get; }

    /// <summary>Returns whether an extension identifier is present.</summary>
    public bool ContainsExtension(string extensionId)
        => extensionId is not null && ContainsOrdinal(ExtensionIds, extensionId);

    /// <summary>Returns whether a feature marker is present.</summary>
    public bool HasFeature(string featureId)
        => featureId is not null && ContainsOrdinal(Features, featureId);

    /// <summary>
    /// Looks up an exact wholly synchronous block renderer. Returns false when
    /// any registration in that kind's ordered pipeline is asynchronous.
    /// </summary>
    public bool TryGetBlockRenderer(string syntaxKind, out MarkdownNodeRenderer? renderer)
    {
        if (syntaxKind is not null && _synchronousBlockRenderers.TryGetValue(syntaxKind, out var value))
        {
            renderer = value;
            return true;
        }

        renderer = null;
        return false;
    }

    /// <summary>
    /// Looks up an exact wholly synchronous inline renderer. Returns false when
    /// any registration in that kind's ordered pipeline is asynchronous.
    /// </summary>
    public bool TryGetInlineRenderer(string syntaxKind, out MarkdownNodeRenderer? renderer)
    {
        if (syntaxKind is not null && _synchronousInlineRenderers.TryGetValue(syntaxKind, out var value))
        {
            renderer = value;
            return true;
        }

        renderer = null;
        return false;
    }

    /// <summary>Looks up an exact asynchronous block syntax renderer.</summary>
    public bool TryGetAsyncBlockRenderer(
        string syntaxKind,
        out MarkdownAsyncNodeRenderer? renderer) =>
        TryGetBlockRendererInvoker(syntaxKind, out renderer);

    /// <summary>Looks up an exact asynchronous inline syntax renderer.</summary>
    public bool TryGetAsyncInlineRenderer(
        string syntaxKind,
        out MarkdownAsyncNodeRenderer? renderer) =>
        TryGetInlineRendererInvoker(syntaxKind, out renderer);

    internal bool TryGetBlockRendererInvoker(
        string syntaxKind,
        out MarkdownAsyncNodeRenderer? renderer)
    {
        if (syntaxKind is not null && _blockRenderers.TryGetValue(syntaxKind, out var value))
        {
            renderer = value;
            return true;
        }

        renderer = null;
        return false;
    }

    internal bool TryGetInlineRendererInvoker(
        string syntaxKind,
        out MarkdownAsyncNodeRenderer? renderer)
    {
        if (syntaxKind is not null && _inlineRenderers.TryGetValue(syntaxKind, out var value))
        {
            renderer = value;
            return true;
        }

        renderer = null;
        return false;
    }

    private static IReadOnlyList<string> CopyStrings(IEnumerable<string> source)
    {
        var copy = new List<string>(source);
        return copy.Count == 0
            ? Array.Empty<string>()
            : new ReadOnlyCollection<string>(copy.ToArray());
    }

    private static IReadOnlyDictionary<string, MarkdownAsyncNodeRenderer> CopyRenderers(
        IReadOnlyDictionary<string, MarkdownAsyncNodeRenderer> source,
        MarkdownExtensionCallbackLifetime? callbackLifetime)
    {
        var copy = new Dictionary<string, MarkdownAsyncNodeRenderer>(source, StringComparer.Ordinal);
        if (callbackLifetime is not null)
        {
            foreach (string kind in new List<string>(copy.Keys))
                copy[kind] = Wrap(copy[kind], callbackLifetime);
        }

        return new ReadOnlyDictionary<string, MarkdownAsyncNodeRenderer>(copy);
    }

    private static IReadOnlyDictionary<string, MarkdownNodeRenderer> CopySynchronousRenderers(
        IReadOnlyDictionary<string, MarkdownNodeRenderer> source,
        MarkdownExtensionCallbackLifetime? callbackLifetime)
    {
        var copy = new Dictionary<string, MarkdownNodeRenderer>(source, StringComparer.Ordinal);
        if (callbackLifetime is not null)
        {
            foreach (string kind in new List<string>(copy.Keys))
                copy[kind] = Wrap(copy[kind], callbackLifetime);
        }

        return new ReadOnlyDictionary<string, MarkdownNodeRenderer>(copy);
    }

    private static MarkdownAsyncNodeRenderer Wrap(
        MarkdownAsyncNodeRenderer renderer,
        MarkdownExtensionCallbackLifetime callbackLifetime) => async (context, content) =>
        {
            using MarkdownExtensionCallbackLifetime.Lease lease = callbackLifetime.Enter();
            await renderer(context, content).ConfigureAwait(false);
        };

    private static MarkdownNodeRenderer Wrap(
        MarkdownNodeRenderer renderer,
        MarkdownExtensionCallbackLifetime callbackLifetime) => (context, content) =>
        {
            using MarkdownExtensionCallbackLifetime.Lease lease = callbackLifetime.Enter();
            renderer(context, content);
        };

    private static bool ContainsOrdinal(IReadOnlyList<string> items, string value)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i], value, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private sealed class BindingFrame(
        IReadOnlyList<MarkdownExtensionRegistration> registrations,
        string? activeFactoryId)
    {
        internal string? ActiveFactoryId { get; } = activeFactoryId;

        internal int Index { get; set; }

        internal IReadOnlyList<MarkdownExtensionRegistration> Registrations { get; } = registrations;
    }

    private static void DisposeIgnoringFailure(IDisposable resource)
    {
        try
        {
            resource.Dispose();
        }
        catch
        {
            // Preserve the binding/configuration failure while still visiting
            // every previously created engine-local resource.
        }
    }
}

internal readonly record struct MarkdownRuntimeExtensionSet(
    MarkdownExtensionSet Extensions,
    IReadOnlyList<IDisposable> OwnedResources,
    MarkdownExtensionCallbackLifetime? CallbackLifetime);
