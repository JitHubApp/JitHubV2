using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// The selection handles live in an overlay whose hit area can extend beyond
/// the short document canvas. These source checks protect the root pointer
/// routing boundary; they are not a live WinUI pointer-dispatch test.
/// </summary>
public sealed class SelectionHandleRootDismissalSourceContractTests
{
    [Fact]
    public void RootPressExemptsOnlyOwnedSelectionHandleAncestryAfterClearingOtherRenderers()
    {
        string source = File.ReadAllText(FindControlSource());
        int handlerStart = source.IndexOf("private void OnAppRootPointerPressed(", StringComparison.Ordinal);
        Assert.True(handlerStart >= 0, "The root pointer-press handler was not found.");
        string handler = MethodBody(source, handlerStart);

        int clearOtherRenderers = handler.IndexOf("MarkdownSelectionCoordinator.ClearSelectionsExcept(this);", StringComparison.Ordinal);
        int codeActionException = handler.IndexOf("if (IsElementWithinCodeBlockAction(source))", StringComparison.Ordinal);
        int selectionHandleException = handler.IndexOf("if (IsElementWithinSelectionHandle(source))", StringComparison.Ordinal);
        int canvasBoundsDismissal = handler.IndexOf("IsPointerOverCanvasTextSurface(e, out var canvasPoint)", StringComparison.Ordinal);
        int externalInteractionDismissal = handler.LastIndexOf("ClearSelectionForExternalInteraction();", StringComparison.Ordinal);

        Assert.True(clearOtherRenderers >= 0 && codeActionException > clearOtherRenderers);
        Assert.True(codeActionException < selectionHandleException && selectionHandleException < canvasBoundsDismissal);
        Assert.True(externalInteractionDismissal > canvasBoundsDismissal);

        int helperStart = source.IndexOf("private bool IsElementWithinSelectionHandle(", StringComparison.Ordinal);
        Assert.True(helperStart >= 0, "Selection-handle exemption must use a dedicated ancestry check.");
        string helper = MethodBody(source, helperStart);
        Assert.Contains("_selectionStartHandle is not null", helper, StringComparison.Ordinal);
        Assert.Contains("IsElementWithin(element, _selectionStartHandle)", helper, StringComparison.Ordinal);
        Assert.Contains("_selectionEndHandle is not null", helper, StringComparison.Ordinal);
        Assert.Contains("IsElementWithin(element, _selectionEndHandle)", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("_overlay", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("_canvas", helper, StringComparison.Ordinal);
    }

    private static string FindControlSource()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string path = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "MarkdownRenderer",
                "Controls",
                "MarkdownRendererControl.cs");
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException("Could not locate MarkdownRendererControl.cs from the test base directory.");
    }

    private static string MethodBody(string source, int signatureStart)
    {
        int openingBrace = source.IndexOf('{', signatureStart);
        Assert.True(openingBrace >= 0);

        int depth = 0;
        for (int index = openingBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[openingBrace..(index + 1)];
        }

        throw new InvalidOperationException("The source method body is not balanced.");
    }
}
