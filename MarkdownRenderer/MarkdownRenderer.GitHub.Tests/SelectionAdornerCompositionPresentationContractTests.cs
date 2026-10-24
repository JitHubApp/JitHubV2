using System.Text.RegularExpressions;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// P1-032: a page-heap dump of the original-option crash shows XAML releasing
/// a finalized adorner Image whose Source was a Win2D CanvasImageSource. The
/// .NET finalizer had already destroyed Win2D's aggregating outer object, while
/// XAML still held the inner SurfaceImageSource. XAML's reference to the inner
/// peer does not keep Win2D's outer alive, so any XAML image source aggregated
/// around a Win2D outer depends on GC order. The adorner therefore draws into
/// a composition surface hosted under an inert placeholder element. These are
/// source contracts; the live original-option batches remain the proof.
/// </summary>
public sealed class SelectionAdornerCompositionPresentationContractTests
{
    [Fact]
    public void TheAdornerNeverPresentsThroughAnAggregatedWin2DXamlImageSource()
    {
        // Explanatory comments may name the removed type; code may not.
        string control = Regex.Replace(ReadControlsSource("MarkdownRendererControl.cs"), @"//[^\n]*", string.Empty);

        Assert.DoesNotMatch(@"\bCanvasImageSource\b", control);
        Assert.DoesNotMatch(@"\bCanvasVirtualImageSource\b", control);
        Assert.DoesNotMatch(@"Controls\.Image\??\s+_selectionAdorner\b", control);
        Assert.Matches(@"private\s+Border\?\s+_selectionAdorner\s*;", control);
    }

    [Fact]
    public void TheAdornerSurfaceIsACompositionSurfaceHostedAsAChildVisual()
    {
        string control = ReadControlsSource("MarkdownRendererControl.cs");
        string host = MethodBody(control, "internal SelectionAdornerVisualHost(Border placeholder)");
        Assert.Contains("ElementCompositionPreview.SetElementChildVisual(placeholder,", host, StringComparison.Ordinal);

        string dispose = MethodBody(control[control.IndexOf("private sealed class SelectionAdornerVisualHost", StringComparison.Ordinal)..], "public void Dispose()");
        Assert.Contains("ElementCompositionPreview.SetElementChildVisual(Placeholder, null);", dispose, StringComparison.Ordinal);

        string surface = MethodBody(control, "internal SelectionAdornerCompositionSurface(");
        Assert.Contains(".CreateDrawingSurface(", surface, StringComparison.Ordinal);
        Assert.Contains("CreateSurfaceBrush(", surface, StringComparison.Ordinal);

        string refresh = MethodBody(control, "private SelectionAdornerRefreshOperationResult RefreshSelectionAdornerUnderPaintLease(");
        Assert.Contains("CanvasComposition.CreateDrawingSession(source.Surface,", refresh, StringComparison.Ordinal);
    }

    [Fact]
    public void RetiredAndReleasedCompositionSurfacesAreDisposedDeterministically()
    {
        string control = ReadControlsSource("MarkdownRendererControl.cs");
        Assert.Matches(@"_selectionAdornerSurfaces\s*=\s*\r?\n?\s*new\(\s*CreateSelectionAdornerSurface,\s*ReportSelectionAdornerSurfaceReleaseFailure,\s*DisposeSelectionAdornerSurface\s*\)", control);

        string unload = MethodBody(control, "private void ReleaseLoadedResources()");
        int release = unload.IndexOf("TryReleaseSelectionAdornerSurface();", StringComparison.Ordinal);
        int drop = unload.IndexOf("_selectionAdornerSurfaces.DropRetired();", StringComparison.Ordinal);
        int hostDispose = unload.IndexOf("_selectionAdornerHost?.Dispose();", StringComparison.Ordinal);
        int device = unload.IndexOf("ReleaseSelectionAdornerGraphicsDevice();", StringComparison.Ordinal);
        Assert.True(release >= 0 && drop > release && hostDispose > release && device > drop,
            "Unload must detach, then dispose every retired surface, the child visual and the composition graphics device.");

        string lifetime = ReadControlsSource("SelectionAdornerSurfaceLifetime.cs");
        Assert.Contains("DisposeRetired(surface);", MethodBody(lifetime, "internal void AdvanceFrame()"), StringComparison.Ordinal);
        Assert.Contains("DisposeRetired(surface);", MethodBody(lifetime, "internal void DropRetired()"), StringComparison.Ordinal);
        Assert.Contains("_dispose(surface);", MethodBody(lifetime, "private void DisposeRetired(TSurface surface)"), StringComparison.Ordinal);
    }

    [Fact]
    public void AuditCapturePaintsTheAdornerStateInsteadOfReadingBackItsChildVisual()
    {
        // RenderTargetBitmap cannot capture a composition child visual.
        string control = ReadControlsSource("MarkdownRendererControl.cs");
        string capture = ReadControlsSource("MarkdownRendererControl.AuditOverlayCapture.cs");

        string plan = MethodBody(capture, "private AuditOverlayCapturePlan CaptureAuditOverlayPlan(");
        int skip = plan.IndexOf("ReferenceEquals(child, _selectionAdorner)", StringComparison.Ordinal);
        int capturesAdd = plan.IndexOf("captures.Add(", StringComparison.Ordinal);
        Assert.True(skip >= 0 && capturesAdd > skip, "The adorner placeholder must be stamped but never read back.");

        int paint = control.IndexOf("snapshot.Paint(drawingSession, viewport);", StringComparison.Ordinal);
        Assert.True(paint >= 0);
        int adorner = control.IndexOf("PaintAuditSelectionAdornerState(snapshot, drawingSession, viewport);", paint, StringComparison.Ordinal);
        int endPaint = control.IndexOf("snapshot.EndPaint();", paint, StringComparison.Ordinal);
        Assert.True(adorner > paint && endPaint > adorner,
            "Audit adorner state must be painted under the same snapshot paint lease.");
    }

    private static string ReadControlsSource(string fileName)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer", "Controls", fileName);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException($"Could not locate {fileName}.");
    }

    private static string MethodBody(string source, string signature)
    {
        int member = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find {signature}");
        int open = source.IndexOf('{', member + signature.Length);
        Assert.True(open >= 0);
        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0) return source[open..(index + 1)];
        }
        throw new InvalidOperationException("Unbalanced source block.");
    }
}
