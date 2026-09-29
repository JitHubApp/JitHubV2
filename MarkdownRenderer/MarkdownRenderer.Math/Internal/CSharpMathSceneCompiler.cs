using System.Drawing;
using CSharpMath.Atom;
using CSharpMath.Rendering.FrontEnd;

namespace MarkdownRenderer.Math.Internal;

internal static class CSharpMathSceneCompiler
{
    // Invalid user-supplied TeX is an ordinary result, not an exception. Keep
    // cancellation, budget failures and unexpected engine faults distinct.
    internal static MathScene? Compile(
        MathFormulaRequest request,
        MathProcessingOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using MathCancellationScope.Registration cancellationScope =
            MathCancellationScope.Enter(
                cancellationToken,
                options.MaximumParserRecursionDepth);
        int maximumCompilerSourceLength = MathWorkingMemoryBudget.GetMaximumSourceLength(
            options.MaximumWorkingMemoryBytes);
        if (maximumCompilerSourceLength <= 0)
            throw new MathSceneBudgetException(MathSceneBudget.WorkingMemory);
        string compilerSource = MathCompatibilityPreprocessor.Normalize(
            request.TexSource,
            maximumCompilerSourceLength,
            cancellationToken);
        var memoryBudget = new MathWorkingMemoryBudget(
            options.MaximumWorkingMemoryBytes,
            compilerSource.Length);

        var painter = new RecordingMathPainter
        {
            DisplayErrorInline = false,
            FontSize = options.FontSize,
            LineStyle = request.DisplayMode == MathFormulaDisplayMode.Display
                ? LineStyle.Display
                : LineStyle.Text,
            LaTeX = compilerSource,
        };

        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(painter.ErrorMessage))
            return null;

        RectangleF measured = painter.Measure();
        cancellationToken.ThrowIfCancellationRequested();

        float width = measured.Width;
        float height = measured.Height;
        float baseline = -measured.Y;
        ValidateDimension(width, options.MaximumSceneDimension, "width");
        ValidateDimension(height, options.MaximumSceneDimension, "height");
        ValidateDimension(baseline, options.MaximumSceneDimension, "baseline");

        if (width <= 0 || height <= 0 || baseline < 0 || baseline > height)
            throw new InvalidOperationException("CSharpMath returned invalid formula metrics.");

        var canvas = new RecordingMathCanvas(
            width,
            height,
            baseline,
            options,
            memoryBudget,
            cancellationToken);
        painter.Draw(canvas, 0, 0);
        return canvas.Complete(baseline);
    }

    private static void ValidateDimension(float value, float maximum, string name)
    {
        if (!float.IsFinite(value) || value > maximum)
            throw new MathSceneDimensionException(name);
    }

    private sealed class RecordingMathPainter : MathPainter<RecordingMathCanvas, Color>
    {
        public override Color WrapColor(Color color) => color;

        public override Color UnwrapColor(Color color) => color;

        public override ICanvas WrapCanvas(RecordingMathCanvas canvas) => canvas;
    }
}

internal sealed class MathSceneDimensionException : Exception
{
    internal MathSceneDimensionException(string dimension)
        : base($"The formula exceeded its {dimension} budget.")
    {
        Dimension = dimension;
    }

    internal string Dimension { get; }
}
