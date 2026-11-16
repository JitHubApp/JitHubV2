using MarkdownRenderer.PerformanceHarness.Core.Events;
using MarkdownRenderer.PerformanceHarness.StageProfiles;

namespace MarkdownRenderer.PerformanceHarness.Measurements.FirstViewport;

/// <summary>
/// One measured first-viewport presentation: the renderer's first-viewport
/// event, the UI publication time, and the opt-in stage-profile measurements
/// when the presentation was profiled.
/// </summary>
internal readonly record struct FirstViewportPresentation(
    FirstViewportEvent FirstViewport,
    double PublicationMilliseconds,
    FirstViewportStageProfileMeasurements? StageProfileMeasurements);
