using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Source-order contracts for the control's XAML/Win2D publication boundary.
/// These do not substitute for live resize, navigation, and unload validation.
/// </summary>
public sealed class SelectionAdornerSurfaceLifetimeContractTests
{
    [Fact]
    public void DrawingSessionIsClosedBeforePublishingGeometryOrMutatingHandles()
    {
        string body = MethodBody(ReadControl(), "private SelectionAdornerRefreshOperationResult RefreshSelectionAdornerUnderPaintLease(");
        const string scopedSession = "using (var drawingSession = source.CreateDrawingSession(";
        int session = body.IndexOf(scopedSession, StringComparison.Ordinal);
        Assert.True(session >= 0, "The surface draw must use an explicit, bounded using block.");
        int close = BlockEnd(body, body.IndexOf('{', session));
        int commit = body.IndexOf("CommitSelectionAdornerGeometry(paintRects, paintBackgrounds);", close, StringComparison.Ordinal);
        int viewport = body.IndexOf("CommitSelectionAdornerViewport(", close, StringComparison.Ordinal);
        int handles = body.IndexOf("UpdateSelectionHandlesUnderPaint(", close, StringComparison.Ordinal);
        int validation = body.IndexOf("surfaceVersion != _selectionAdornerSurfaceVersion", close, StringComparison.Ordinal);

        Assert.True(validation > close && commit > validation && viewport > commit && handles > viewport);
        Assert.Contains("requestVersion != _selectionAdornerRequestVersion", body[close..commit], StringComparison.Ordinal);
        Assert.Contains("!ReferenceEquals(source, _selectionAdornerSource)", body[close..commit], StringComparison.Ordinal);
        Assert.Contains("snapshot.LayoutRevision != layoutRevision", body[close..commit], StringComparison.Ordinal);
        Assert.DoesNotContain("CommitSelectionAdorner", body[session..close], StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateSelectionHandles", body[session..close], StringComparison.Ordinal);
        Assert.Contains("GC.KeepAlive(source);", body[close..], StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacementPublishesOnlyAfterAttachmentAndRevalidatesRetirement()
    {
        string body = MethodBody(ReadControl(), "private bool EnsureSelectionAdornerSurface(");
        int retain = body.IndexOf("CanvasImageSource? previousSource = _selectionAdornerSource;", StringComparison.Ordinal);
        int version = body.IndexOf("++_selectionAdornerSurfaceVersion", StringComparison.Ordinal);
        int create = body.IndexOf("new CanvasImageSource(", StringComparison.Ordinal);
        int attach = body.IndexOf("adorner.Source = replacement;", StringComparison.Ordinal);
        int publish = body.IndexOf("_selectionAdornerSource = replacement;", StringComparison.Ordinal);

        Assert.True(retain >= 0 && version > retain && create > version && attach > create && publish > attach);
        Assert.Contains("IsSelectionAdornerSurfaceCurrent(adorner, surfaceVersion)", body[create..attach], StringComparison.Ordinal);
        Assert.Contains("IsSelectionAdornerSurfaceCurrent(adorner, surfaceVersion)", body[attach..publish], StringComparison.Ordinal);
        Assert.Contains("GC.KeepAlive(previousSource);", body[publish..], StringComparison.Ordinal);
        Assert.Contains("GC.KeepAlive(replacement);", body[publish..], StringComparison.Ordinal);

        string current = MethodBody(ReadControl(), "private bool IsSelectionAdornerSurfaceCurrent(");
        Assert.Contains("!_isDisposed && !_isUnloaded", current, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(adorner, _selectionAdorner)", current, StringComparison.Ordinal);
        Assert.Contains("surfaceVersion == _selectionAdornerSurfaceVersion", current, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseKeepsPriorOwnerUntilXamlDetachesAndRejectsReentrantReset()
    {
        string body = MethodBody(ReadControl(), "private void ReleaseSelectionAdornerSurface()");
        int retain = body.IndexOf("CanvasImageSource? previousSource = _selectionAdornerSource;", StringComparison.Ordinal);
        int version = body.IndexOf("++_selectionAdornerSurfaceVersion", StringComparison.Ordinal);
        int detach = body.IndexOf("adorner.Source = null;", StringComparison.Ordinal);
        int validate = body.IndexOf("surfaceVersion != _selectionAdornerSurfaceVersion", StringComparison.Ordinal);
        int clear = body.IndexOf("_selectionAdornerSource = null;", StringComparison.Ordinal);

        Assert.True(retain >= 0 && version > retain && detach > version && validate > detach && clear > validate);
        Assert.Contains("!ReferenceEquals(adorner, _selectionAdorner)", body[detach..clear], StringComparison.Ordinal);
        Assert.Contains("GC.KeepAlive(previousSource);", body[clear..], StringComparison.Ordinal);
        Assert.Contains("finally", body[clear..], StringComparison.Ordinal);
    }

    private static string ReadControl()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer", "Controls", "MarkdownRendererControl.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException("Could not locate MarkdownRendererControl.cs.");
    }

    private static string MethodBody(string source, string signature)
    {
        int member = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find {signature}");
        int open = source.IndexOf('{', member + signature.Length);
        Assert.True(open >= 0);
        return source[open..(BlockEnd(source, open) + 1)];
    }

    private static int BlockEnd(string source, int open)
    {
        Assert.True(open >= 0);
        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0) return index;
        }
        throw new InvalidOperationException("Unbalanced source block.");
    }
}
