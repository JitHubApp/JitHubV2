using System;
using System.Threading;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// One-shot source-free ownership between actual Open completion and Render.
/// Pending disposal retires an unconsumed candidate; after consumption only
/// actual render completion may release documents, cancellation and gates.
/// </summary>
internal sealed class SvgOpenedRenderWork(
    ISvgLoadHost owner, ImageResourceScope resources, SvgPublicationMetadata metadata, string contentCacheKey,
    string? builtInSourceAliasKey, bool usesTiles, IMarkdownSvgDocument document, SvgDocumentUseLease documentUse,
    SvgHostOwnerSlot? ownerSlot, ImageWorkLease imageWork,
    CancellationTokenSource? preparationCancellation, CancellationToken cancellationToken,
    CancellationToken sessionLifetimeToken, BoundedAsyncKeyedGate.Entry? openAdmissionEntry,
    bool providerAdmissionHeld) : IDisposable
{
    private ISvgLoadHost? _owner = owner;
    private readonly ImageResourceScope _resources = resources;
    private SvgOpenedRenderOwnership _ownership;
    private CancellationTokenSource? _preparationCancellation = preparationCancellation;
    private readonly ImageWorkLease _imageWork = imageWork;
    private readonly bool _providerAdmissionHeld = providerAdmissionHeld;
    internal readonly SvgPublicationMetadata Metadata = metadata;
    internal readonly string ContentCacheKey = contentCacheKey;
    internal readonly string? BuiltInSourceAliasKey = builtInSourceAliasKey;
    internal readonly bool UsesTiles = usesTiles;
    internal readonly CancellationToken CancellationToken = cancellationToken;
    internal readonly CancellationToken SessionLifetimeToken = sessionLifetimeToken;
    internal IMarkdownSvgDocument? Document = document;
    internal SvgDocumentUseLease? DocumentUse = documentUse;
    internal SvgHostOwnerSlot? OwnerSlot = ownerSlot;
    private BoundedAsyncKeyedGate.Entry? _openAdmissionEntry = openAdmissionEntry;

    internal bool TryConsume() => _ownership.TryConsume();

    internal void TransferTiledOwnership()
    {
        Document = null;
        DocumentUse = null;
        OwnerSlot = null;
    }

    public void Dispose()
    {
        if (_ownership.TryDisposePending())
            Cleanup();
    }

    internal void Complete()
    {
        if (_ownership.TryCompleteConsumed())
            Cleanup();
    }

    private void Cleanup()
    {
        ISvgLoadHost? owner = Interlocked.Exchange(ref _owner, null);
        if (owner is null)
            return;
        IMarkdownSvgDocument? document = Document;
        SvgDocumentUseLease? documentUse = DocumentUse;
        SvgHostOwnerSlot? ownerSlot = OwnerSlot;
        CancellationTokenSource? cancellation = _preparationCancellation;
        Document = null;
        DocumentUse = null;
        OwnerSlot = null;
        _preparationCancellation = null;
        try
        {
            try
            {
                _resources.SvgOpenAdmissions.Release(ref _openAdmissionEntry);
            }
            catch (Exception exception)
            {
                owner.RecordImageRetirementFailure(exception);
            }
            if (_providerAdmissionHeld)
                owner.TryImageCleanup(() => _resources.SvgProviderAdmission.Release());
            if (document is not null)
                owner.RetireUncommittedSvgDocument(document);
            if (documentUse is not null)
                owner.TryImageCleanup(documentUse.Dispose);
            if (ownerSlot is not null)
                owner.TryImageCleanup(ownerSlot.Dispose);
        }
        finally
        {
            if (cancellation is not null)
                owner.TryImageCleanup(cancellation.Dispose);
            _imageWork.Dispose();
        }
    }
}
