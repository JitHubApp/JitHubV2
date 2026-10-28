using System.Drawing;
using System.Numerics;
using CSharpMath.Rendering.FrontEnd;
using CSharpMathPath = CSharpMath.Rendering.FrontEnd.Path;

namespace MarkdownRenderer.Math.Internal;

internal sealed class RecordingMathCanvas : ICanvas
{
    private readonly List<MathSceneCommand> _commands = new();
    private readonly Stack<CanvasState> _states = new();
    private readonly CancellationToken _cancellationToken;
    private readonly MathWorkingMemoryBudget _memoryBudget;
    private readonly int _maximumCommands;
    private readonly int _maximumPathOperations;
    private Matrix3x2 _transform;
    private int _pathOperationCount;

    internal RecordingMathCanvas(
        float width,
        float height,
        float baseline,
        MathProcessingOptions options,
        MathWorkingMemoryBudget memoryBudget,
        CancellationToken cancellationToken)
    {
        Width = width;
        Height = height;
        _maximumCommands = options.MaximumSceneCommands;
        _maximumPathOperations = options.MaximumPathOperations;
        _memoryBudget = memoryBudget;
        _cancellationToken = cancellationToken;
        _transform = Matrix3x2.CreateTranslation(0, baseline);
        DefaultColor = Color.Black;
        CurrentStyle = PaintStyle.Fill;
    }

    public float Width { get; }

    public float Height { get; }

    public Color DefaultColor { get; set; }

    public Color? CurrentColor { get; set; }

    public PaintStyle CurrentStyle { get; set; }

    public CSharpMathPath StartNewPath()
    {
        Checkpoint();
        _memoryBudget.Reserve(256);
        return new RecordingMathPath(this, _transform, CurrentColor ?? DefaultColor, CurrentStyle);
    }

    public void DrawLine(float x1, float y1, float x2, float y2, float lineThickness)
    {
        Checkpoint();
        MathPoint start = Transform(x1, y1, _transform);
        MathPoint end = Transform(x2, y2, _transform);
        float scale = GetMaximumScale(_transform);
        AddCommand(new MathSceneCommand(
            MathSceneCommandKind.StrokeLine,
            path: null,
            rectangle: default,
            start,
            end,
            MathF.Abs(lineThickness * scale),
            GetSceneColor(CurrentColor ?? DefaultColor)));
    }

    public void StrokeRect(float left, float top, float width, float height) =>
        AddRectangle(MathSceneCommandKind.StrokeRectangle, left, top, width, height, 1);

    public void FillRect(float left, float top, float width, float height) =>
        AddRectangle(MathSceneCommandKind.FillRectangle, left, top, width, height, 0);

    public void Save()
    {
        Checkpoint();
        _memoryBudget.Reserve(64);
        _states.Push(new CanvasState(_transform, CurrentColor, CurrentStyle));
    }

    public void Translate(float dx, float dy)
    {
        Checkpoint();
        EnsureFinite(dx, nameof(dx));
        EnsureFinite(dy, nameof(dy));
        _transform = Matrix3x2.CreateTranslation(dx, dy) * _transform;
    }

    public void Scale(float sx, float sy)
    {
        Checkpoint();
        EnsureFinite(sx, nameof(sx));
        EnsureFinite(sy, nameof(sy));
        _transform = Matrix3x2.CreateScale(sx, sy) * _transform;
    }

    public void Restore()
    {
        Checkpoint();
        if (_states.Count == 0)
            throw new InvalidOperationException("The math canvas state stack is empty.");

        CanvasState state = _states.Pop();
        _transform = state.Transform;
        CurrentColor = state.Color;
        CurrentStyle = state.Style;
    }

    internal MathScene Complete(float baseline)
    {
        Checkpoint();
        if (_states.Count != 0)
            throw new InvalidOperationException("The math canvas state stack was not balanced.");

        return new MathScene(Width, Height, baseline, _commands);
    }

    internal void AddPath(
        IReadOnlyList<MathPathOperation> operations,
        Color color,
        PaintStyle style)
    {
        Checkpoint();
        if (operations.Count == 0 || color.A == 0)
            return;

        AddCommand(new MathSceneCommand(
            style == PaintStyle.Fill ? MathSceneCommandKind.FillPath : MathSceneCommandKind.StrokePath,
            operations,
            rectangle: default,
            start: default,
            end: default,
            style == PaintStyle.Fill ? 0 : 1,
            GetSceneColor(color)));
    }

    internal void ReservePathOperation()
    {
        Checkpoint();
        if (_pathOperationCount == _maximumPathOperations)
            throw new MathSceneBudgetException(MathSceneBudget.PathOperations);
        _memoryBudget.Reserve(128);
        _pathOperationCount++;
    }

    internal static MathPoint Transform(float x, float y, Matrix3x2 transform)
    {
        EnsureFinite(x, nameof(x));
        EnsureFinite(y, nameof(y));
        Vector2 transformed = Vector2.Transform(new Vector2(x, y), transform);
        return new MathPoint(transformed.X, transformed.Y);
    }

    private void AddRectangle(
        MathSceneCommandKind kind,
        float left,
        float top,
        float width,
        float height,
        float strokeWidth)
    {
        Checkpoint();
        Color color = CurrentColor ?? DefaultColor;
        if (color.A == 0)
            return;

        MathPoint corner1 = Transform(left, top, _transform);
        MathPoint corner2 = Transform(left + width, top + height, _transform);
        var rectangle = new MathRectangle(
            MathF.Min(corner1.X, corner2.X),
            MathF.Min(corner1.Y, corner2.Y),
            MathF.Abs(corner2.X - corner1.X),
            MathF.Abs(corner2.Y - corner1.Y));
        AddCommand(new MathSceneCommand(
            kind,
            path: null,
            rectangle,
            start: default,
            end: default,
            strokeWidth * GetMaximumScale(_transform),
            GetSceneColor(color)));
    }

    private void AddCommand(MathSceneCommand command)
    {
        Checkpoint();
        if (_commands.Count == _maximumCommands)
            throw new MathSceneBudgetException(MathSceneBudget.Commands);
        _memoryBudget.Reserve(256);
        _commands.Add(command);
    }

    private void Checkpoint() => _cancellationToken.ThrowIfCancellationRequested();

    private static float GetMaximumScale(Matrix3x2 transform)
    {
        float scaleX = MathF.Sqrt((transform.M11 * transform.M11) + (transform.M12 * transform.M12));
        float scaleY = MathF.Sqrt((transform.M21 * transform.M21) + (transform.M22 * transform.M22));
        return MathF.Max(scaleX, scaleY);
    }

    private static uint? GetSceneColor(Color color)
    {
        if (color.ToArgb() == Color.Black.ToArgb())
            return null;

        return unchecked((uint)color.ToArgb());
    }

    private static void EnsureFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value))
            throw new InvalidOperationException($"CSharpMath produced a non-finite {parameterName} coordinate.");
    }

    private readonly record struct CanvasState(
        Matrix3x2 Transform,
        Color? Color,
        PaintStyle Style);
}

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

internal enum MathSceneBudget
{
    Commands,
    PathOperations,
    WorkingMemory,
}

internal sealed class MathSceneBudgetException : Exception
{
    internal MathSceneBudgetException(MathSceneBudget budget)
        : base($"The formula exceeded the {budget} scene budget.")
    {
        Budget = budget;
    }

    internal MathSceneBudget Budget { get; }
}
