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
    public const string ImageZoom = TelemetryTaxonomy.Actions.ImageZoom;
    public const string BreadcrumbRoot = TelemetryTaxonomy.Actions.BreadcrumbRoot;
    public const string BreadcrumbPath = TelemetryTaxonomy.Actions.BreadcrumbPath;
    public const string CsvCopy = TelemetryTaxonomy.Actions.CsvCopy;
    public const string CsvPlainView = TelemetryTaxonomy.Actions.CsvPlainView;
    public const string CsvReorder = TelemetryTaxonomy.Actions.CsvReorder;
    public const string CsvResize = TelemetryTaxonomy.Actions.CsvResize;
    public const string CsvRichView = TelemetryTaxonomy.Actions.CsvRichView;
    public const string CsvSort = TelemetryTaxonomy.Actions.CsvSort;
    public const string JsonPlainView = TelemetryTaxonomy.Actions.JsonPlainView;
    public const string JsonRichView = TelemetryTaxonomy.Actions.JsonRichView;
    public const string SvgZoom = TelemetryTaxonomy.Actions.SvgZoom;
    public const string XmlPlainView = TelemetryTaxonomy.Actions.XmlPlainView;
    public const string XmlRichView = TelemetryTaxonomy.Actions.XmlRichView;

    public static IReadOnlySet<string> Allowed { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Find,
        Outline,
        CopyPath,
        CopyRaw,
        CopyLineLink,
        Drawer,
        ExternalOpen,
        ImageZoom,
        BreadcrumbRoot,
        BreadcrumbPath,
        CsvCopy,
        CsvPlainView,
        CsvReorder,
        CsvResize,
        CsvRichView,
        CsvSort,
        JsonPlainView,
        JsonRichView,
        SvgZoom,
        XmlPlainView,
        XmlRichView
    };
}
