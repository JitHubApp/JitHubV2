using System.Drawing;
using System.Numerics;
using CSharpMath.Rendering.FrontEnd;
using MarkdownRenderer.Math.Internal.Scenes;
using CSharpMathPath = CSharpMath.Rendering.FrontEnd.Path;

namespace MarkdownRenderer.Math.Internal.Compilation;

internal sealed class RecordingMathPath : CSharpMathPath
{
    private readonly RecordingMathCanvas _canvas;
    private readonly Matrix3x2 _transform;
    private readonly Color _defaultColor;
    private readonly PaintStyle _style;
    private readonly List<MathPathOperation> _operations = new();
    private bool _isDisposed;

    internal RecordingMathPath(
        RecordingMathCanvas canvas,
        Matrix3x2 transform,
        Color defaultColor,
        PaintStyle style)
    {
        _canvas = canvas;
        _transform = transform;
        _defaultColor = defaultColor;
        _style = style;
    }

    public override Color? Foreground { get; set; }

    public override void MoveTo(float x0, float y0)
    {
        Reserve();
        _operations.Add(MathPathOperation.MoveTo(Transform(x0, y0)));
    }

    public override void LineTo(float x1, float y1)
    {
        Reserve();
        _operations.Add(MathPathOperation.LineTo(Transform(x1, y1)));
    }

    public override void Curve3(float x1, float y1, float x2, float y2)
    {
        Reserve();
        _operations.Add(MathPathOperation.QuadraticBezierTo(
            Transform(x1, y1),
            Transform(x2, y2)));
    }

    public override void Curve4(float x1, float y1, float x2, float y2, float x3, float y3)
    {
        Reserve();
        _operations.Add(MathPathOperation.CubicBezierTo(
            Transform(x1, y1),
            Transform(x2, y2),
            Transform(x3, y3)));
    }

    public override void CloseContour()
    {
        Reserve();
        _operations.Add(MathPathOperation.Close());
    }

    public override void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _canvas.AddPath(_operations, Foreground ?? _defaultColor, _style);
    }

    private MathPoint Transform(float x, float y) =>
        RecordingMathCanvas.Transform(x, y, _transform);

    private void Reserve()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(RecordingMathPath));
        _canvas.ReservePathOperation();
    }
}
