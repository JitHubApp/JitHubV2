using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MarkdownRenderer.Extensions;

/// <summary>
/// Collects parser-independent extension registrations before an engine is frozen.
/// </summary>
/// <remarks>This type is intended for single-threaded build-time use.</remarks>
public sealed class MarkdownExtensionBuilder
{
    private readonly List<string> _extensionIds = new();
    private readonly HashSet<string> _extensionIdSet = new(StringComparer.Ordinal);
    private readonly List<string> _features = new();
    private readonly HashSet<string> _featureSet = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MarkdownAsyncNodeRenderer> _blockRenderers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MarkdownAsyncNodeRenderer> _inlineRenderers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MarkdownNodeRenderer> _synchronousBlockRenderers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MarkdownNodeRenderer> _synchronousInlineRenderers = new(StringComparer.Ordinal);
    private readonly List<MarkdownOwnedExtensionFactory> _ownedExtensionFactories = new();
    private readonly HashSet<string> _ownedExtensionFactoryIds = new(StringComparer.Ordinal);
    private readonly List<MarkdownExtensionRegistration> _registrations = new();

    /// <summary>
    /// Adds an extension transactionally. If configuration fails, every
    /// registration made by that extension is rolled back.
    /// </summary>
    /// <remarks>
    /// This operation borrows the extension and any state captured by its
    /// registrations; neither this builder nor a resulting extension set
    /// disposes that state. Engine-scoped disposable state must be registered
    /// through <see cref="MarkdownEngineBuilder.UseOwnedExtensionFactory"/>.
    /// </remarks>
    public MarkdownExtensionBuilder Add(IMarkdownExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);

        string id = ValidateIdentifier(extension.Id, nameof(extension));
        return AddValidated(extension, id);
    }

    /// <summary>
    /// Configures an extension whose identifier was already captured and
    /// validated by an owned factory. This must not access
    /// <see cref="IMarkdownExtension.Id"/> again because that getter is
    /// executable third-party code and need not be stable.
    /// </summary>
    internal MarkdownExtensionBuilder AddValidated(
        IMarkdownExtension extension,
        string extensionId)
    {
        ArgumentNullException.ThrowIfNull(extension);

        string id = ValidateIdentifier(extensionId, nameof(extensionId));
        if (_extensionIdSet.Contains(id) || _ownedExtensionFactoryIds.Contains(id))
            throw new InvalidOperationException($"The markdown extension '{id}' is already registered.");

        var snapshot = CaptureSnapshot();
        _extensionIds.Add(id);
        _extensionIdSet.Add(id);
        _registrations.Add(MarkdownExtensionRegistration.Extension(id));

        try
        {
            extension.Configure(this);
            return this;
        }
        catch
        {
            Restore(snapshot);
            throw;
        }
    }

    /// <summary>Adds a stable feature marker used for capability discovery.</summary>
    public MarkdownExtensionBuilder AddFeature(string featureId)
    {
        string id = ValidateIdentifier(featureId, nameof(featureId));
        if (_featureSet.Add(id))
        {
            _features.Add(id);
            _registrations.Add(MarkdownExtensionRegistration.Feature(id));
        }
        return this;
    }

    /// <summary>
    /// Registers an ordered renderer for an exact block syntax kind. When more
    /// than one renderer handles the same kind, registration order is retained
    /// and dispatch stops after a renderer emits content or a diagnostic.
    /// </summary>
    public MarkdownExtensionBuilder RegisterBlock(
        string syntaxKind,
        MarkdownNodeRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        string kind = RegisterSynchronous(
            _blockRenderers,
            _synchronousBlockRenderers,
            syntaxKind,
            renderer);
        _registrations.Add(MarkdownExtensionRegistration.SynchronousBlock(kind, renderer));
        return this;
    }

    /// <summary>
    /// Registers an ordered asynchronous renderer for an exact block syntax
    /// kind. Independent documents may invoke the renderer concurrently.
    /// </summary>
    public MarkdownExtensionBuilder RegisterBlockAsync(
        string syntaxKind,
        MarkdownAsyncNodeRenderer renderer)
    {
        string kind = RegisterAsynchronous(
            _blockRenderers,
            _synchronousBlockRenderers,
            syntaxKind,
            renderer);
        _registrations.Add(MarkdownExtensionRegistration.AsynchronousBlock(kind, renderer));
        return this;
    }

    /// <summary>
    /// Registers an ordered renderer for an exact inline syntax kind. When more
    /// than one renderer handles the same kind, registration order is retained
    /// and dispatch stops after a renderer emits content or a diagnostic.
    /// </summary>
    public MarkdownExtensionBuilder RegisterInline(
        string syntaxKind,
        MarkdownNodeRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        string kind = RegisterSynchronous(
            _inlineRenderers,
            _synchronousInlineRenderers,
            syntaxKind,
            renderer);
        _registrations.Add(MarkdownExtensionRegistration.SynchronousInline(kind, renderer));
        return this;
    }

    /// <summary>
    /// Registers an ordered asynchronous renderer for an exact inline syntax
    /// kind. Independent documents may invoke the renderer concurrently.
    /// </summary>
    public MarkdownExtensionBuilder RegisterInlineAsync(
        string syntaxKind,
        MarkdownAsyncNodeRenderer renderer)
    {
        string kind = RegisterAsynchronous(
            _inlineRenderers,
            _synchronousInlineRenderers,
            syntaxKind,
            renderer);
        _registrations.Add(MarkdownExtensionRegistration.AsynchronousInline(kind, renderer));
        return this;
    }

    /// <summary>Includes an already frozen extension set.</summary>
    public MarkdownExtensionBuilder Include(MarkdownExtensionSet extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);

        var snapshot = CaptureSnapshot();
        try
        {
            MarkdownExtensionSet reusable = extensions.ReusableTemplate;
            for (int index = 0; index < reusable.Registrations.Count; index++)
                ReplayRegistration(reusable.Registrations[index]);

            return this;
        }
        catch
        {
            Restore(snapshot);
            throw;
        }
    }

    /// <summary>Freezes a thread-safe snapshot of all current registrations.</summary>
    public MarkdownExtensionSet Build()
        => _extensionIds.Count == 0 &&
           _features.Count == 0 &&
           _blockRenderers.Count == 0 &&
           _inlineRenderers.Count == 0 &&
           _synchronousBlockRenderers.Count == 0 &&
           _synchronousInlineRenderers.Count == 0 &&
           _ownedExtensionFactories.Count == 0 &&
           _registrations.Count == 0
            ? MarkdownExtensionSet.Empty
            : new MarkdownExtensionSet(
                _extensionIds,
                _features,
                _blockRenderers,
                _inlineRenderers,
                _synchronousBlockRenderers,
                _synchronousInlineRenderers,
                _ownedExtensionFactories,
                _registrations);

    internal void AddOwnedExtensionFactory(MarkdownOwnedExtensionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        string id = ValidateIdentifier(factory.ExtensionId, nameof(factory));
        if (_extensionIdSet.Contains(id) || !_ownedExtensionFactoryIds.Add(id))
            throw new InvalidOperationException($"The markdown extension '{id}' is already registered.");
        _ownedExtensionFactories.Add(factory);
        _registrations.Add(MarkdownExtensionRegistration.Owned(factory));
    }

    internal MarkdownExtensionSet BuildRuntime(
        MarkdownExtensionSet reusableTemplate,
        MarkdownExtensionCallbackLifetime callbackLifetime) =>
        new(
            _extensionIds,
            _features,
            _blockRenderers,
            _inlineRenderers,
            _synchronousBlockRenderers,
            _synchronousInlineRenderers,
            reusableTemplate.OwnedExtensionFactories,
            _registrations,
            reusableTemplate,
            callbackLifetime);

    internal void ReplayRegistration(MarkdownExtensionRegistration registration)
    {
        switch (registration.Kind)
        {
            case MarkdownExtensionRegistrationKind.Extension:
                AddExtensionId(registration.Identifier!);
                break;
            case MarkdownExtensionRegistrationKind.Feature:
                AddFeature(registration.Identifier!);
                break;
            case MarkdownExtensionRegistrationKind.SynchronousBlock:
                RegisterBlock(registration.Identifier!, registration.SynchronousRenderer!);
                break;
            case MarkdownExtensionRegistrationKind.AsynchronousBlock:
                RegisterBlockAsync(registration.Identifier!, registration.AsynchronousRenderer!);
                break;
            case MarkdownExtensionRegistrationKind.SynchronousInline:
                RegisterInline(registration.Identifier!, registration.SynchronousRenderer!);
                break;
            case MarkdownExtensionRegistrationKind.AsynchronousInline:
                RegisterInlineAsync(registration.Identifier!, registration.AsynchronousRenderer!);
                break;
            case MarkdownExtensionRegistrationKind.OwnedFactory:
                AddOwnedExtensionFactory(registration.OwnedFactory!);
                break;
            default:
                throw new InvalidOperationException("Unknown markdown extension registration kind.");
        }
    }

    private void AddExtensionId(string id)
    {
        if (_ownedExtensionFactoryIds.Contains(id) || !_extensionIdSet.Add(id))
            throw new InvalidOperationException($"The markdown extension '{id}' is already registered.");
        _extensionIds.Add(id);
        _registrations.Add(MarkdownExtensionRegistration.Extension(id));
    }

    private static string RegisterAsynchronous(
        Dictionary<string, MarkdownAsyncNodeRenderer> target,
        Dictionary<string, MarkdownNodeRenderer> synchronousTarget,
        string syntaxKind,
        MarkdownAsyncNodeRenderer renderer)
    {
        string kind = ValidateIdentifier(syntaxKind, nameof(syntaxKind));
        ArgumentNullException.ThrowIfNull(renderer);

        RegisterInvoker(target, kind, renderer);
        synchronousTarget.Remove(kind);
        return kind;
    }

    private static string RegisterSynchronous(
        Dictionary<string, MarkdownAsyncNodeRenderer> target,
        Dictionary<string, MarkdownNodeRenderer> synchronousTarget,
        string syntaxKind,
        MarkdownNodeRenderer renderer)
    {
        string kind = ValidateIdentifier(syntaxKind, nameof(syntaxKind));
        ArgumentNullException.ThrowIfNull(renderer);
        bool existingPipelineIsSynchronous =
            !target.ContainsKey(kind) || synchronousTarget.ContainsKey(kind);

        RegisterInvoker(target, kind, (context, content) =>
        {
            renderer(context, content);
            return ValueTask.CompletedTask;
        });

        if (!existingPipelineIsSynchronous)
        {
            synchronousTarget.Remove(kind);
        }
        else if (synchronousTarget.TryGetValue(kind, out MarkdownNodeRenderer? existing))
        {
            synchronousTarget[kind] = Compose(existing, renderer);
        }
        else
        {
            synchronousTarget.Add(kind, renderer);
        }
        return kind;
    }

    private static void RegisterInvoker(
        Dictionary<string, MarkdownAsyncNodeRenderer> target,
        string kind,
        MarkdownAsyncNodeRenderer renderer)
    {
        if (target.TryGetValue(kind, out MarkdownAsyncNodeRenderer? existing))
            target[kind] = Compose(existing, renderer);
        else
            target.Add(kind, renderer);
    }

    private static MarkdownAsyncNodeRenderer Compose(
        MarkdownAsyncNodeRenderer first,
        MarkdownAsyncNodeRenderer second) => async (context, content) =>
        {
            int contentBefore = content.EmissionCount;
            int diagnosticsBefore = context.DiagnosticCount;
            await first(context, content).ConfigureAwait(false);
            if (content.EmissionCount == contentBefore && context.DiagnosticCount == diagnosticsBefore)
                await second(context, content).ConfigureAwait(false);
        };

    private static MarkdownNodeRenderer Compose(
        MarkdownNodeRenderer first,
        MarkdownNodeRenderer second) => (context, content) =>
        {
            int contentBefore = content.EmissionCount;
            int diagnosticsBefore = context.DiagnosticCount;
            first(context, content);
            if (content.EmissionCount == contentBefore && context.DiagnosticCount == diagnosticsBefore)
                second(context, content);
        };

    private static string ValidateIdentifier(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        string id = value.Trim();
        if (id.Length > 128)
            throw new ArgumentOutOfRangeException(parameterName, "Identifiers cannot exceed 128 characters.");

        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':'))
            {
                throw new ArgumentException(
                    "Identifiers may contain only ASCII letters, digits, '.', '_', '-', and ':'.",
                    parameterName);
            }
        }

        return id;
    }

    private BuilderSnapshot CaptureSnapshot()
        => new(
            _extensionIds.ToArray(),
            _features.ToArray(),
            new Dictionary<string, MarkdownAsyncNodeRenderer>(_blockRenderers, StringComparer.Ordinal),
            new Dictionary<string, MarkdownAsyncNodeRenderer>(_inlineRenderers, StringComparer.Ordinal),
            new Dictionary<string, MarkdownNodeRenderer>(_synchronousBlockRenderers, StringComparer.Ordinal),
            new Dictionary<string, MarkdownNodeRenderer>(_synchronousInlineRenderers, StringComparer.Ordinal),
            _ownedExtensionFactories.ToArray(),
            _registrations.ToArray());

    private void Restore(BuilderSnapshot snapshot)
    {
        _extensionIds.Clear();
        _extensionIds.AddRange(snapshot.ExtensionIds);
        _extensionIdSet.Clear();
        _extensionIdSet.UnionWith(snapshot.ExtensionIds);

        _features.Clear();
        _features.AddRange(snapshot.Features);
        _featureSet.Clear();
        _featureSet.UnionWith(snapshot.Features);

        _blockRenderers.Clear();
        foreach (var renderer in snapshot.BlockRenderers)
            _blockRenderers.Add(renderer.Key, renderer.Value);

        _inlineRenderers.Clear();
        foreach (var renderer in snapshot.InlineRenderers)
            _inlineRenderers.Add(renderer.Key, renderer.Value);

        _synchronousBlockRenderers.Clear();
        foreach (var renderer in snapshot.SynchronousBlockRenderers)
            _synchronousBlockRenderers.Add(renderer.Key, renderer.Value);

        _synchronousInlineRenderers.Clear();
        foreach (var renderer in snapshot.SynchronousInlineRenderers)
            _synchronousInlineRenderers.Add(renderer.Key, renderer.Value);

        _ownedExtensionFactories.Clear();
        _ownedExtensionFactories.AddRange(snapshot.OwnedExtensionFactories);
        _ownedExtensionFactoryIds.Clear();
        for (int index = 0; index < snapshot.OwnedExtensionFactories.Length; index++)
            _ownedExtensionFactoryIds.Add(snapshot.OwnedExtensionFactories[index].ExtensionId);

        _registrations.Clear();
        _registrations.AddRange(snapshot.Registrations);
    }

    private sealed record BuilderSnapshot(
        string[] ExtensionIds,
        string[] Features,
        Dictionary<string, MarkdownAsyncNodeRenderer> BlockRenderers,
        Dictionary<string, MarkdownAsyncNodeRenderer> InlineRenderers,
        Dictionary<string, MarkdownNodeRenderer> SynchronousBlockRenderers,
        Dictionary<string, MarkdownNodeRenderer> SynchronousInlineRenderers,
        MarkdownOwnedExtensionFactory[] OwnedExtensionFactories,
        MarkdownExtensionRegistration[] Registrations);
}
