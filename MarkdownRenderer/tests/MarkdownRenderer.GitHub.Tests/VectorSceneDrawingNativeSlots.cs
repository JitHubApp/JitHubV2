using System.Reflection;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Reads the native clip-layer and geometry slots of a <see cref="VectorSceneDrawing"/>
/// for the clip fault tests, which must close exactly the native objects a
/// deterministic fault left open while the real drawing session is still live.
/// </summary>
/// <remarks>
/// <see cref="VectorSceneDrawing"/> has no seam for these slots yet, so this is
/// the one place the vector clip tests read them by name.
/// </remarks>
internal static class VectorSceneDrawingNativeSlots
{
    private static readonly FieldInfo ClipStackField = typeof(VectorSceneDrawing)
        .GetField("_clipStack", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly FieldInfo GeometriesField = typeof(VectorSceneDrawing)
        .GetField("_geometries", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static object? ReadClipStack(VectorSceneDrawing drawing) => ClipStackField.GetValue(drawing);

    internal static object? ReadGeometries(VectorSceneDrawing drawing) => GeometriesField.GetValue(drawing);
}
