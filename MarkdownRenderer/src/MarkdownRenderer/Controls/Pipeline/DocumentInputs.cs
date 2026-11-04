using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MarkdownRenderer.Controls.Embeds;
using MarkdownRenderer.Controls.Highlighting;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using Microsoft.UI.Xaml;
using RebuildReason = MarkdownRenderer.Controls.MarkdownRebuildCoordinatorReason;

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// The document inputs of a renderer: the Markdown and Document properties
/// and which of them is authoritative, the parser configuration (Engine and
/// the legacy registry), the engine the control owns, and the disclosure
/// states that survive restyles of the same document.
/// </summary>
/// <remarks>
/// Document is both an input and the published output of Markdown parsing.
/// Only a document the host supplied is authoritative; one this control
/// published is cleared whenever the source or parser configuration changes.
/// </remarks>
internal sealed class DocumentInputs
{
    private readonly MarkdownRendererControl _control;
    private readonly RendererEvents _events;
    private readonly InteractionReset _interactionReset;
    private readonly EmbedRealization _embeds;
    private readonly CodeBlockHighlighting _highlighting;
    private readonly RebuildScheduler _rebuild;
    private readonly SnapshotRetirementTracker _snapshotRetirements;
    private readonly Dictionary<string, bool> _disclosureStates = new(StringComparer.Ordinal);
    private MarkdownEngine? _ownedEngine;
    private bool _isUpdatingDocumentProperty;
    private bool _hasExplicitDocument;

    internal DocumentInputs(
        MarkdownRendererControl control,
        RendererEvents events,
        InteractionReset interactionReset,
        EmbedRealization embeds,
        CodeBlockHighlighting highlighting,
        RebuildScheduler rebuild,
        SnapshotRetirementTracker snapshotRetirements)
    {
        _control = control;
        _events = events;
        _interactionReset = interactionReset;
        _embeds = embeds;
        _highlighting = highlighting;
        _rebuild = rebuild;
        _snapshotRetirements = snapshotRetirements;
    }

    /// <summary>True when the host assigned Document, which then takes precedence over Markdown.</summary>
    internal bool HasExplicitDocument => _hasExplicitDocument;

    /// <summary>The markdown source the next pipeline renders.</summary>
    internal string EffectiveSource => _hasExplicitDocument
        ? _control.Document?.Source ?? string.Empty
        : _control.Markdown ?? string.Empty;

    /// <summary>A copy of the disclosure states for one pipeline's layout inputs.</summary>
    internal Dictionary<string, bool> CopyDisclosureStates() =>
        new(_disclosureStates, StringComparer.Ordinal);

    /// <summary>
    /// Assigns an engine created specifically for this control. Public Engine
    /// assignments are borrowed; only engines assigned through this internal
    /// ownership boundary are disposed with the control.
    /// </summary>
    internal void SetOwnedEngine(MarkdownEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        MarkdownEngine? previous = _ownedEngine;
        _ownedEngine = null;
        try
        {
            _control.Engine = engine;
            _ownedEngine = engine;
        }
        catch
        {
            _ownedEngine = previous;
            RetireOwnedEngine(engine);
            throw;
        }

        if (previous is not null && !ReferenceEquals(previous, engine))
            RetireOwnedEngine(previous);
    }

    /// <summary>Retires the engine the control owns, at disposal.</summary>
    internal void ReleaseOwnedEngine()
    {
        MarkdownEngine? ownedEngine = _ownedEngine;
        _ownedEngine = null;
        if (ownedEngine is not null)
            RetireOwnedEngine(ownedEngine);
    }

    internal void OnMarkdownChanged()
    {
        _interactionReset.ResetInputForDocumentReplacement();
        // Document has documented precedence over Markdown. A source change
        // invalidates only the document that this control published itself;
        // it must never clear a host-supplied/bound document based on DP
        // notification order. Hosts switch back to source parsing by clearing
        // Document explicitly.
        if (!_hasExplicitDocument && !_isUpdatingDocumentProperty && _control.Document is not null)
        {
            _isUpdatingDocumentProperty = true;
            try { _control.ClearValue(MarkdownRendererControl.DocumentProperty); }
            finally { _isUpdatingDocumentProperty = false; }
        }
        _disclosureStates.Clear();
        _embeds.ClearHostedElementFallbacks();
        _highlighting.ClearCache();
        _rebuild.Request(RebuildReason.Full);
    }

    internal void OnDocumentChanged(DependencyPropertyChangedEventArgs e)
    {
        if (_isUpdatingDocumentProperty)
            return;

        _interactionReset.ResetInputForDocumentReplacement();
        _hasExplicitDocument = e.NewValue is MarkdownRenderer.Document.MarkdownDocument;
        _disclosureStates.Clear();
        _embeds.ClearHostedElementFallbacks();
        _highlighting.ClearCache();
        _rebuild.Request(RebuildReason.Full);
    }

    internal void OnEngineChanged(DependencyPropertyChangedEventArgs e)
    {
        MarkdownEngine? retiredOwnedEngine = null;
        if (_ownedEngine is not null &&
            ReferenceEquals(e.OldValue, _ownedEngine) &&
            !ReferenceEquals(e.NewValue, _ownedEngine))
        {
            retiredOwnedEngine = _ownedEngine;
            _ownedEngine = null;
        }

        try
        {
            // Supersede the current parse before disposing the previous owner.
            // The retirement helper also snapshots other already-pending work.
            OnParserConfigurationChanged();
        }
        finally
        {
            if (retiredOwnedEngine is not null)
                RetireOwnedEngine(retiredOwnedEngine);
        }
    }

    internal void OnParserConfigurationChanged()
    {
        // Document is both an input and the published output of Markdown
        // parsing. Only a document supplied by the host is authoritative.
        // Clear an internally published value so changing Engine or the legacy
        // registry cannot silently reuse an AST produced by old settings.
        if (!_hasExplicitDocument && _control.Document is not null)
        {
            _isUpdatingDocumentProperty = true;
            try { _control.ClearValue(MarkdownRendererControl.DocumentProperty); }
            finally { _isUpdatingDocumentProperty = false; }
        }

        _disclosureStates.Clear();
        _embeds.ClearHostedElementFallbacks();
        _highlighting.ClearCache();
        _rebuild.Request(RebuildReason.Full);
    }

    internal void SetDisclosureExpanded(LinkRun run, bool expanded)
    {
        if (run is not { IsDisclosure: true, DisclosureId: { Length: > 0 } id } ||
            run.IsExpanded == expanded)
        {
            return;
        }

        SetDisclosureExpanded(id, expanded);
    }

    internal void SetDisclosureExpanded(string id, bool expanded)
    {
        void Apply()
        {
            _disclosureStates[id] = expanded;
            _events.RaiseDisclosureToggled(expanded);
            _rebuild.Request(RebuildReason.Restyle);
        }

        if (_control.DispatcherQueue is { HasThreadAccess: false } dispatcher)
        {
            dispatcher.TryEnqueue(Apply);
        }
        else
        {
            Apply();
        }
    }

    /// <summary>
    /// Publishes the document the committed snapshot was parsed from on the
    /// Document property, without treating that write as a host assignment.
    /// </summary>
    internal void PublishParsedDocument(MarkdownDocument semanticDocument)
    {
        if (!ReferenceEquals(_control.Document, semanticDocument))
        {
            _isUpdatingDocumentProperty = true;
            try { _control.SetValue(MarkdownRendererControl.DocumentProperty, semanticDocument); }
            finally { _isUpdatingDocumentProperty = false; }
        }
    }

    private Task RetireOwnedEngine(MarkdownEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        // Snapshot the barrier before tracking this engine's own retirement;
        // otherwise a later shutdown drain could wait on a task that waits on
        // that same drain.
        if (_rebuild.ActiveTask is { } activePipeline)
            _snapshotRetirements.Track(activePipeline);
        Task lifetime = _snapshotRetirements.WaitForPendingAsync();
        Task retirement = OwnedResourceRetirement.DisposeAfterPropagating(engine, lifetime);
        _snapshotRetirements.Track(retirement);
        return retirement;
    }
}
