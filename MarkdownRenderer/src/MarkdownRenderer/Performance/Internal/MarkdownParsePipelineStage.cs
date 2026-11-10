namespace MarkdownRenderer.Performance.Internal;

internal enum MarkdownParsePipelineStage
{
    Begin = 0,
    EngineParseAndCache = 1,
    LegacyParseAndDocument = 2,
    ProgressiveScenePlan = 3,
    StyleRoleDemand = 4,
    SessionTotal = 5,
}
