using Xunit;
using TablePreparationRace = MarkdownRenderer.GitHub.Tests.RootLocalTableImageRelayoutTests.TablePreparationRace;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Tables;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Applies one <see cref="TablePreparationRace"/> from a root-local table
/// relayout's before-measure hook and records the state the rejected relayout
/// must then leave in place.
/// </summary>
internal sealed class RootLocalTablePreparationRaceDriver : IDisposable
{
    private readonly TablePreparationRace _race;
    private readonly LayoutSnapshot _snapshot;
    private readonly TableBox _table;
    private readonly BlockBox _revisionTrigger;
    private readonly float _layoutWidth;

    internal RootLocalTablePreparationRaceDriver(
        TablePreparationRace race,
        LayoutSnapshot snapshot,
        TableBox table,
        BlockBox revisionTrigger,
        float layoutWidth)
    {
        _race = race;
        _snapshot = snapshot;
        _table = table;
        _revisionTrigger = revisionTrigger;
        _layoutWidth = layoutWidth;
        ExpectedPointerEpoch = snapshot.PointerInputEpoch;
        RevisionAfterRace = snapshot.LayoutRevision;
    }

    internal CancellationTokenSource Cancellation { get; } = new();
    internal long ExpectedPointerEpoch { get; private set; }
    internal long RevisionAfterRace { get; private set; }
    internal RootLocalTableSourceState? ExpectedStateAfterRace { get; private set; }
    internal bool Applied { get; private set; }

    internal void Apply()
    {
        switch (_race)
        {
            case TablePreparationRace.Cancellation:
                Cancellation.Cancel();
                break;
            case TablePreparationRace.PointerInput:
                _snapshot.NotifyPointerInput();
                ExpectedPointerEpoch++;
                break;
            case TablePreparationRace.LayoutRevision:
                _snapshot.RelayoutChangedBlocks(
                    [_revisionTrigger.BlockIndex], _layoutWidth, CancellationToken.None);
                RevisionAfterRace = _snapshot.LayoutRevision;
                ExpectedStateAfterRace = RootLocalTableSourceState.Capture(_snapshot, _table);
                break;
            case TablePreparationRace.HorizontalScroll:
                Assert.True(_table.SetHorizontalOffset(_table.HorizontalOffset + 72d));
                ExpectedStateAfterRace = RootLocalTableSourceState.Capture(_snapshot, _table);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(_race));
        }
        Applied = true;
    }

    public void Dispose() => Cancellation.Dispose();
}
