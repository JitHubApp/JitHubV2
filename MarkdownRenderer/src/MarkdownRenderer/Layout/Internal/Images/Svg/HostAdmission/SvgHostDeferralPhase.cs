namespace MarkdownRenderer.Layout.Internal.Images.Svg.HostAdmission;

/// <summary>Where an <see cref="SvgHostLoadDeferral"/> is in its denied-load cycle.</summary>
internal enum SvgHostDeferralPhase
{
    /// <summary>No denied load is held.</summary>
    None = 0,

    /// <summary>A denied load waits for demand, capacity or an owner slot.</summary>
    Waiting = 1,

    /// <summary>The denied load's retry has been queued and has not started.</summary>
    RetryQueued = 2,
}
