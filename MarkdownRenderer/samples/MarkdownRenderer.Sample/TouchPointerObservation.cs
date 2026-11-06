using System.Globalization;
using System.Text;

namespace MarkdownRenderer.Sample;

/// <summary>
/// What <see cref="TouchContactObservation"/> has seen of one touch pointer,
/// and its entry in the published observation JSON.
/// </summary>
internal sealed class TouchPointerObservation(uint pointerId)
{
    internal uint PointerId { get; } = pointerId;
    internal long ContactSerial { get; set; }
    internal int PressCount { get; set; }
    internal int MoveCount { get; set; }
    internal int ReleaseCount { get; set; }
    internal int RawReleaseCount { get; set; }
    internal int NativeCanceledPacketCount { get; set; }
    internal bool LastNativeCanceled { get; set; }
    internal int CancelCount { get; set; }
    internal int CaptureLostCount { get; set; }
    internal bool InContact { get; set; }
    internal string? HandleAutomationId { get; set; }
    internal double LastPositionXDip { get; set; }
    internal double LastPositionYDip { get; set; }
    internal double LastRootPositionXDip { get; set; }
    internal double LastRootPositionYDip { get; set; }
    internal double EventRasterizationScale { get; set; }
    internal TouchPointerEventKind LastEvent { get; set; }
    internal TouchPointerEventKind LastRoutedEvent { get; set; }
    internal long LastEventSequence { get; set; }

    internal void AppendJson(StringBuilder json)
    {
        json.Append("{\"pointerId\":")
            .Append(PointerId.ToString(CultureInfo.InvariantCulture))
            .Append(",\"contactSerial\":")
            .Append(ContactSerial.ToString(CultureInfo.InvariantCulture))
            .Append(",\"handle\":");
        if (HandleAutomationId is null)
            json.Append("null");
        else
            TouchContactJson.AppendString(json, HandleAutomationId);
        json.Append(",\"pressCount\":")
            .Append(PressCount.ToString(CultureInfo.InvariantCulture))
            .Append(",\"moveCount\":")
            .Append(MoveCount.ToString(CultureInfo.InvariantCulture))
            .Append(",\"releaseCount\":")
            .Append(ReleaseCount.ToString(CultureInfo.InvariantCulture))
            .Append(",\"rawReleaseCount\":")
            .Append(RawReleaseCount.ToString(CultureInfo.InvariantCulture))
            .Append(",\"nativeCanceledPacketCount\":")
            .Append(NativeCanceledPacketCount.ToString(CultureInfo.InvariantCulture))
            .Append(",\"lastNativeCanceled\":")
            .Append(LastNativeCanceled ? "true" : "false")
            .Append(",\"cancelCount\":")
            .Append(CancelCount.ToString(CultureInfo.InvariantCulture))
            .Append(",\"captureLostCount\":")
            .Append(CaptureLostCount.ToString(CultureInfo.InvariantCulture))
            .Append(",\"inContact\":")
            .Append(InContact ? "true" : "false")
            .Append(",\"lastPositionDipX\":");
        TouchContactJson.AppendNumber(json, LastPositionXDip);
        json.Append(",\"lastPositionDipY\":");
        TouchContactJson.AppendNumber(json, LastPositionYDip);
        json.Append(",\"lastPositionRootDipX\":");
        TouchContactJson.AppendNumber(json, LastRootPositionXDip);
        json.Append(",\"lastPositionRootDipY\":");
        TouchContactJson.AppendNumber(json, LastRootPositionYDip);
        json.Append(",\"eventRasterizationScale\":");
        TouchContactJson.AppendNumber(json, EventRasterizationScale);
        json.Append(",\"lastEventSequence\":")
            .Append(LastEventSequence.ToString(CultureInfo.InvariantCulture))
            .Append(",\"lastEvent\":");
        TouchContactJson.AppendString(json, LastEvent.ToString());
        json.Append(",\"lastRoutedEvent\":");
        TouchContactJson.AppendString(json, LastRoutedEvent.ToString());
        json.Append('}');
    }
}
