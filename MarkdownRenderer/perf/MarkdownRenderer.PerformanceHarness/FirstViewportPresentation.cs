using MarkdownRenderer.PerformanceHarness.Core.Events;

namespace MarkdownRenderer.PerformanceHarness;

/// <summary>
/// One measured first-viewport presentation: the renderer's first-viewport
/// event, the UI publication time, and the opt-in stage-profile measurements
/// when the presentation was profiled.
/// </summary>
internal readonly record struct FirstViewportPresentation(
    FirstViewportEvent FirstViewport,
    double PublicationMilliseconds,
    FirstViewportStageProfileMeasurements? StageProfileMeasurements);
