namespace MarkdownRenderer.Svg.Resvg.Internal.Diagnostics;

/// <summary>Numeric worker substage ids carried by <see cref="ResvgEventSource.WorkerStage"/>.</summary>
internal enum SvgWorkerStageId
{
    PoolSchedulingQueueWait = 101,
    WorkerStartupAndPipeHandshake = 102,
    FontCatalogWait = 103,
    WorkerTransactionQueueWait = 104,
    DocumentMissingProbe = 105,
    DocumentMissingAttach = 106,
    RenderTransaction = 107,
    OpenTransaction = 108,
    FontCatalogHelloRoundTrip = 109,
    FontCatalogDatabaseCreate = 110,
    FontCatalogMappedFonts = 111,
    FontCatalogUserLocalFonts = 112,
    FontCatalogUserRoamingFonts = 113,
    FontCatalogFamilyDefaults = 114,
    FontCatalogTextPipelineWarmup = 115,
    FontCatalogTotal = 116,
    PipeRequestWrite = 117,
    PipeRequestFlush = 118,
    PipeResponseRead = 119,
    PreflightDispatchQueueWait = 120,
}
