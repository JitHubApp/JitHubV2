using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;

namespace MarkdownRenderer.Layout.Internal.Vectors.Drawing;

/// <summary>
/// The prepared native slot arrays one paint pass borrows from its drawing.
/// Geometry, text-layout and stroke slots are consumed in command order; the
/// transform and clip stacks are scratch sized to the scene's nesting depth.
/// </summary>
internal readonly record struct VectorScenePaintSlots(
    CanvasGeometry?[] Geometries,
    CanvasTextLayout?[] TextLayouts,
    CanvasStrokeStyle?[] StrokeStyles,
    Matrix3x2[] TransformStack,
    CanvasActiveLayer?[] ClipStack);
