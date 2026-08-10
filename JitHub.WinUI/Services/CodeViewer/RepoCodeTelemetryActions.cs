using System;
using System.Collections.Generic;
using JitHub.Services;

namespace JitHub.Services.CodeViewer;

public static class RepoCodeTelemetryActions
{
    public const string Find = TelemetryTaxonomy.Actions.Find;
    public const string Outline = TelemetryTaxonomy.Actions.Outline;
    public const string CopyPath = TelemetryTaxonomy.Actions.CopyPath;
    public const string CopyRaw = TelemetryTaxonomy.Actions.CopyRaw;
    public const string CopyLineLink = TelemetryTaxonomy.Actions.CopyLineLink;
    public const string Drawer = TelemetryTaxonomy.Actions.Drawer;
    public const string ExternalOpen = TelemetryTaxonomy.Actions.ExternalOpen;
    public const string BreadcrumbRoot = TelemetryTaxonomy.Actions.BreadcrumbRoot;
    public const string BreadcrumbPath = TelemetryTaxonomy.Actions.BreadcrumbPath;

    public static IReadOnlySet<string> Allowed { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Find,
        Outline,
        CopyPath,
        CopyRaw,
        CopyLineLink,
        Drawer,
        ExternalOpen,
        BreadcrumbRoot,
        BreadcrumbPath
    };
}
