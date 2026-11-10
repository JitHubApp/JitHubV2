using System;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Internal.Images.BitmapOutput;

/// <summary>
/// What an image box's bitmap-output admission and deferral read from and ask
/// of the box. Members that take a box lock say so; the others only read
/// volatile state, so the deferral may call them under its own lock.
/// </summary>
internal interface IImageBitmapOutputHost
{
    /// <summary>The box's current load demand ticket.</summary>
    MarkdownSvgRenderDemand? LoadDemand { get; }

    /// <summary>The generation of <see cref="LoadDemand"/>.</summary>
    int LoadDemandGeneration { get; }

    /// <summary>The box's SVG render generation.</summary>
    int SvgRenderGeneration { get; }

    /// <summary>True when the box last observed itself in the viewport.</summary>
    bool IsViewportVisible { get; }

    /// <summary>True once the box is disposed or its resources are suspended.</summary>
    bool IsRetired { get; }

    /// <summary>
    /// Takes the image-work lock: true when no image work is active and the
    /// box is not retired.
    /// </summary>
    bool IsImageWorkDrained { get; }

    /// <summary>Takes the image-work lock: true when no image work is active.</summary>
    bool HasNoImageWork { get; }

    /// <summary>
    /// Takes the SVG tile lock: true when the box still shows a bitmap, lease,
    /// fallback tile or (unless the output is an existing tile) SVG tile.
    /// </summary>
    bool HasRetainedBitmapOutput(bool existingTileOutput);

    /// <summary>Records a cleanup or callback failure with the box's retirement.</summary>
    void RecordImageRetirementFailure(Exception exception);

    /// <summary>Runs one cleanup action, recording rather than throwing its failure.</summary>
    void TryImageCleanup(Action cleanup);

    /// <summary>Raises the box's load completion.</summary>
    void RaiseLoadCompleted(bool layoutInvalidated);

    /// <summary>Raises the source-free recovery hint for a waiter that could not register.</summary>
    void RaiseBitmapOutputRecoverySignal();

    /// <summary>Restarts the box's load for a retried deferral.</summary>
    void StartLoad(bool isVisible, MarkdownSvgRenderDemand? demandOverride, int loadDemandGeneration);

    /// <summary>
    /// Takes the host-deferral lock: re-arms the box's load and returns true
    /// when the cancelled request is still current.
    /// </summary>
    bool TryResetLoad(MarkdownSvgRenderDemand demand, int generation, int svgGeneration);
}
