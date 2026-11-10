using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal.Workers.FontReadiness;

internal sealed class WorkerFontWaitReservation : IDisposable
{
    private Action? _release;
    private IDisposable? _promotionRegistration;
    private Action? _consumed;
    private int _state;
    private int _countsAsVisibleRender;
    private int _countsAsVisibleTextRender;
    private int _promotionApplied;
    private readonly bool _isRender;
    private readonly bool _requiresFontCatalog;

    public WorkerFontWaitReservation(
        Action release,
        bool countsAsVisibleRender = false,
        bool countsAsVisibleTextRender = false,
        MarkdownSvgRenderDemand? demandTicket = null,
        bool isRender = false,
        bool requiresFontCatalog = false,
        Action? consumed = null)
    {
        _release = release ?? throw new ArgumentNullException(nameof(release));
        if (countsAsVisibleTextRender && !countsAsVisibleRender)
            throw new ArgumentException("A visible text render must also count as a visible render.", nameof(countsAsVisibleTextRender));
        _countsAsVisibleRender = countsAsVisibleRender ? 1 : 0;
        _countsAsVisibleTextRender = countsAsVisibleTextRender ? 1 : 0;
        DemandTicket = demandTicket;
        _isRender = isRender;
        _requiresFontCatalog = requiresFontCatalog;
        _consumed = consumed;
        _promotionApplied = countsAsVisibleRender || countsAsVisibleTextRender ? 1 : 0;
    }

    public bool IsActive => Volatile.Read(ref _state) == 0;
    public bool CountsAsVisibleRender => Volatile.Read(ref _countsAsVisibleRender) != 0;
    public bool CountsAsVisibleTextRender => Volatile.Read(ref _countsAsVisibleTextRender) != 0;
    internal MarkdownSvgRenderDemand? DemandTicket { get; }

    internal void SetPromotionRegistration(IDisposable registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (Interlocked.CompareExchange(ref _promotionRegistration, registration, null) is not null)
            throw new InvalidOperationException("A font-wait reservation already has a promotion registration.");
        if (!IsActive)
            Interlocked.Exchange(ref _promotionRegistration, null)?.Dispose();
    }

    /// <summary>Called only while the owning worker-pool state lock is held.</summary>
    internal bool PromoteVisible(out bool addedVisibleRender, out bool addedVisibleTextRender)
    {
        addedVisibleRender = false;
        addedVisibleTextRender = false;
        if (!IsActive || DemandTicket?.Priority != MarkdownSvgRenderPriority.Visible ||
            Interlocked.Exchange(ref _promotionApplied, 1) != 0)
            return false;

        if (_isRender)
        {
            addedVisibleRender = Interlocked.Exchange(ref _countsAsVisibleRender, 1) == 0;
            if (_requiresFontCatalog)
                addedVisibleTextRender = Interlocked.Exchange(ref _countsAsVisibleTextRender, 1) == 0;
        }
        return true;
    }

    internal void DisposePromotionRegistration() =>
        Interlocked.Exchange(ref _promotionRegistration, null)?.Dispose();

    public bool TryConsume()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            return false;
        Interlocked.Exchange(ref _release, null);
        DisposePromotionRegistration();
        Interlocked.Exchange(ref _consumed, null)?.Invoke();
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
        {
            DisposePromotionRegistration();
            Interlocked.Exchange(ref _consumed, null);
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
}
