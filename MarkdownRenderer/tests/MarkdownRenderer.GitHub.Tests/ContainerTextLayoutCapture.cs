using System.Reflection;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.ContainerRelayoutTestTree;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Captures final inline text origins, native text-layout metrics, character
/// regions and paint diagnostics so the container relayout tests can compare a
/// transferred placement with a fresh fallback reflow.
/// </summary>
internal static class ContainerTextLayoutCapture
{
    internal static (int RootBlockIndex, int BlockIndex, float X, float Y)[] CaptureFinalTextOrigins(
        LayoutSnapshot snapshot)
    {
        MethodInfo getOrigin = typeof(InlineContainerBox).GetMethods(
                BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "GetSnappedOrigin" && method.GetParameters().Length == 3);
        var result = new List<(int RootBlockIndex, int BlockIndex, float X, float Y)>();
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            foreach (BlockBox root in snapshot.Blocks)
            {
                using LayoutPlacementTransformContext.Scope placementScope =
                    snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(root);
                foreach (InlineContainerBox inline in EnumerateBoxes([root]).OfType<InlineContainerBox>())
                {
                    object style = inline.GetContainerStyle()
                        ?? throw new InvalidOperationException("Inline style resolver returned null.");
                    object?[] arguments = [style, 0f, 0f];
                    _ = getOrigin.Invoke(inline, arguments);
                    result.Add((root.BlockIndex, inline.BlockIndex,
                        Assert.IsType<float>(arguments[1]), Assert.IsType<float>(arguments[2])));
                }
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }
        return result.ToArray();
    }

    internal static (int RootBlockIndex, int BlockIndex, string Metrics)[] CaptureCanvasTextLayoutMetrics(
        LayoutSnapshot snapshot)
    {
        var result = new List<(int RootBlockIndex, int BlockIndex, string Metrics)>();
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            foreach (BlockBox root in snapshot.Blocks)
            {
                using LayoutPlacementTransformContext.Scope placementScope =
                    snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(root);
                foreach (InlineContainerBox inline in EnumerateBoxes([root]).OfType<InlineContainerBox>())
                {
                    object? layout = inline.TextLayout;
                    result.Add((root.BlockIndex, inline.BlockIndex,
                        layout is null ? "<no-layout>" : DescribePublicProperties(layout)));
                }
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }
        return result.ToArray();
    }

    private static string DescribePublicProperties(object layout) =>
        string.Join(" | ", layout.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property => property.GetIndexParameters().Length == 0)
            .OrderBy(static property => property.Name, StringComparer.Ordinal)
            .Select(property =>
            {
                try
                {
                    object? value = property.GetValue(layout);
                    if (value is null)
                        return $"{property.Name}=<null>";
                    if (value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or Enum)
                        return $"{property.Name}={value}";
                    if (value is System.Collections.IEnumerable items)
                        return $"{property.Name}=[{string.Join(';', items.Cast<object?>().Select(static item => item?.ToString() ?? "<null>"))}]";
                    return $"{property.Name}={value.GetType().FullName}";
                }
                catch (Exception exception)
                {
                    return $"{property.Name}=<{exception.GetType().Name}>";
                }
            }));

    internal static (int RootBlockIndex, int BlockIndex, string Buffer, string Regions)[] CaptureTextLayoutRegions(
        LayoutSnapshot snapshot)
    {
        var result = new List<(int RootBlockIndex, int BlockIndex, string Buffer, string Regions)>();
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            foreach (BlockBox root in snapshot.Blocks)
            {
                using LayoutPlacementTransformContext.Scope placementScope =
                    snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(root);
                foreach (InlineContainerBox inline in EnumerateBoxes([root]).OfType<InlineContainerBox>())
                {
                    object? layout = inline.TextLayout;
                    string buffer = Assert.IsType<string>(inline.LayoutBuffer);
                    if (layout is null || buffer.Length == 0)
                    {
                        result.Add((root.BlockIndex, inline.BlockIndex, buffer,
                            layout is null ? "<no-layout>" : "<empty>"));
                        continue;
                    }

                    MethodInfo getRegions = layout.GetType().GetMethod("GetCharacterRegions",
                        BindingFlags.Instance | BindingFlags.Public, binder: null,
                        [typeof(int), typeof(int)], modifiers: null)
                        ?? throw new InvalidOperationException("Canvas text region API is unavailable.");
                    object? regionValue = getRegions.Invoke(layout, [0, buffer.Length]);
                    string regions = regionValue is System.Collections.IEnumerable items
                        ? string.Join(" | ", items.Cast<object?>().Select(DescribeTextRegion))
                        : "<no-regions>";
                    result.Add((root.BlockIndex, inline.BlockIndex, buffer, regions));
                }
            }
        }
        finally
        {
            snapshot.EndInteraction();
        }
        return result.ToArray();
    }

    private static string DescribeTextRegion(object? region)
    {
        if (region is null)
            return "<null>";

        string[] values = region.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property => property.GetIndexParameters().Length == 0)
            .OrderBy(static property => property.Name, StringComparer.Ordinal)
            .Select(property =>
            {
                object? value = property.GetValue(region);
                if (value is null)
                    return $"{property.Name}=<null>";
                if (value.GetType().FullName == "Windows.Foundation.Rect")
                {
                    Type type = value.GetType();
                    return $"{property.Name}=({type.GetProperty("X")?.GetValue(value)}," +
                        $"{type.GetProperty("Y")?.GetValue(value)}," +
                        $"{type.GetProperty("Width")?.GetValue(value)}," +
                        $"{type.GetProperty("Height")?.GetValue(value)})";
                }
                return $"{property.Name}={value}";
            })
            .ToArray();
        return string.Join(",", values);
    }

    internal static string CaptureInlinePaintDetails(LayoutSnapshot snapshot, BlockBox root)
    {
        MethodInfo getOrigin = typeof(InlineContainerBox).GetMethods(
                BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "GetSnappedOrigin" && method.GetParameters().Length == 3);
        Assert.True(snapshot.TryBeginInteraction());
        try
        {
            using LayoutPlacementTransformContext.Scope placementScope =
                snapshot.EnterPlacementTransformScopeForTopLevelBlockUnderPaint(root);
            string boxes = string.Join("; ", EnumerateBoxes([root]).Select(box =>
                $"{box.GetType().Name}#{box.BlockIndex} bounds={box.Bounds}"));
            string images = string.Join("; ", EnumerateImages([root]).Select(image =>
                $"ImageBox#{image.BlockIndex} logical={LayoutPlacementTestGeometry.ToLogical(snapshot, image)}"));
            string inlines = string.Join("; ", EnumerateBoxes([root]).OfType<InlineContainerBox>().Select(inline =>
            {
                object style = inline.GetContainerStyle()
                    ?? throw new InvalidOperationException("Inline style resolver returned null.");
                object?[] arguments = [style, 0f, 0f];
                _ = getOrigin.Invoke(inline, arguments);
                float originX = Assert.IsType<float>(arguments[1]);
                float originY = Assert.IsType<float>(arguments[2]);
                string text = Assert.IsType<string>(inline.LayoutBuffer);
                object? foreground = style.GetType().GetProperty("Foreground")?.GetValue(style);
                return $"block={inline.BlockIndex}, bounds={inline.Bounds}, origin=({originX:R},{originY:R}), " +
                    $"foreground={foreground}, text=\"{text}\"";
            }));
            return $"boxes: {boxes}; images: {images}; inlines: {inlines}";
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }
}
