using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Source-wiring checks for the code-action portion of raster placement COW.
/// These do not instantiate WinUI, prove button identity at runtime, or replace
/// the focused native candidate tests.
/// </summary>
public sealed class RasterCodeBlockActionPublicationContractTests
{
    private const string PublishSignature =
        "private bool TryPublishRasterPlacement(LayoutSnapshot source, RasterPlacementPlan plan, long generation)";

    [Fact]
    public void CandidateStagesOnlyMeasuredPrivateCopyActionsAndBuildsTheirIndexBeforeUiPublication_SourceContractOnly()
    {
        string source = ReadSource("MarkdownRendererControl.ImageCandidate.cs");
        string constructor = ReadMethod(source, "internal RasterPlacementPlan(SingleRasterLayoutCandidate owner, long generation,");

        Assert.Contains("owner.CodePlacements", constructor, StringComparison.Ordinal);
        Assert.Contains("CodePlacements = new CodeBlockPlacementIdentity[placements.Count]", constructor, StringComparison.Ordinal);
        Assert.Contains("if (identity.WasMeasured)", constructor, StringComparison.Ordinal);
        Assert.Contains("if (!identity.WasMeasured || !placement.HasCopyButtonAction", constructor, StringComparison.Ordinal);
        Assert.Contains("placement.CopyButtonBounds.Width <= 0 || placement.CopyButtonBounds.Height <= 0", constructor, StringComparison.Ordinal);
        Assert.Contains("CodeActionIndex = BuildPlanIndex(actions);", constructor, StringComparison.Ordinal);
        Assert.Contains("CodeActionSources", constructor, StringComparison.Ordinal);
        Assert.Contains("identity.Source", constructor, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicationRequiresExactOrderedWeakSourceAndCopyActionMembershipBeforeCommit_SourceContractOnly()
    {
        string source = ReadSource("MarkdownRendererControl.ImageCandidate.cs");
        string prepare = ReadMethod(source, "private bool TryPrepareCodeBlockActionPublication(");
        string publish = ReadMethod(source, PublishSignature);

        Assert.Contains("candidatePlans.Count != _codeBlockActionPlans.Count", prepare, StringComparison.Ordinal);
        Assert.Contains("placementIndex <= previousPlacementIndex", prepare, StringComparison.Ordinal);
        Assert.Contains("actionSources[ordinal].TryGetTarget", prepare, StringComparison.Ordinal);
        Assert.Contains("identity.Source.TryGetTarget", prepare, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(sourceBox, oldPlan.Box)", prepare, StringComparison.Ordinal);
        Assert.Contains("oldPlan.Kind != CodeBlockHostedElementKind.Copy", prepare, StringComparison.Ordinal);
        Assert.Contains("newPlan.Kind != CodeBlockHostedElementKind.Copy", prepare, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(newPlan.Box, identity.Placement)", prepare, StringComparison.Ordinal);
        Assert.Contains("FindFocusableItemIndex(plan.Focus, newFocus) < 0", prepare, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(sourceBox.Metadata, identity.Placement.Metadata)", prepare, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(sourceBox.CodeText, identity.Placement.CodeText)", prepare, StringComparison.Ordinal);
        Assert.Contains("sourceBox.IsCurrentPrivatePlacement(identity.Revision)", prepare, StringComparison.Ordinal);
        Assert.DoesNotContain("StableKey", prepare, StringComparison.Ordinal);

        int prepareCall = publish.IndexOf("TryPrepareCodeBlockActionPublication(plan", StringComparison.Ordinal);
        int commit = publish.IndexOf("_publishingLayoutCommit = true;", StringComparison.Ordinal);
        Assert.True(prepareCall >= 0 && commit > prepareCall);
        Assert.DoesNotContain("_codeBlockActionPlans.Count != 0", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainPlanOwnershipAndFocusSwapPrecedeCallbackCapableButtonRebind_SourceContractOnly()
    {
        string source = ReadSource("MarkdownRendererControl.ImageCandidate.cs");
        string publish = ReadMethod(source, PublishSignature);

        int actions = publish.IndexOf("_codeBlockActionPlans = plan.CodeActionPlans!;", StringComparison.Ordinal);
        int index = publish.IndexOf("_codeBlockActionPlanIndex = plan.CodeActionIndex;", StringComparison.Ordinal);
        int active = publish.IndexOf("_activeCodeBlockActionOrdinals = plan.ActiveCodeActionOrdinals!;", StringComparison.Ordinal);
        int rects = publish.IndexOf("_codeBlockActionRects = plan.CodeActionRects!;", StringComparison.Ordinal);
        int focus = publish.IndexOf("_focusableItems = plan.Focus;", StringComparison.Ordinal);
        int snapshot = publish.IndexOf("_snapshot = owner.TransferToControl();", StringComparison.Ordinal);
        int rebind = publish.IndexOf("AdoptRealizedFromRasterCandidate(", StringComparison.Ordinal);
        int oldCleanup = publish.IndexOf("foreach (CodeBlockActionPlan oldPlan in oldCodeActionPlans)", StringComparison.Ordinal);
        int scheduleHighlight = publish.IndexOf("ScheduleVisibleCodeBlockHighlighting();", StringComparison.Ordinal);
        int retirement = publish.IndexOf("RetireSnapshot(source);", snapshot, StringComparison.Ordinal);

        Assert.True(actions >= 0 && index > actions && active > index && rects > active && focus >= 0 && snapshot > focus &&
            retirement > snapshot && rebind > retirement);
        Assert.True(oldCleanup > rebind && scheduleHighlight > rebind);
        Assert.Contains("_focusedItemIndex = focusedIdentity", publish, StringComparison.Ordinal);
        Assert.Contains("_focusResumeItemIndex = resumeFocusIdentity", publish, StringComparison.Ordinal);
        Assert.Contains("_hasMeasuredCodeBlocks = plan.HasMeasuredCodeBlocks;", publish, StringComparison.Ordinal);
        Assert.Contains("_codeBlockActionRects = plan.CodeActionRects!;", publish, StringComparison.Ordinal);
        int residency = publish.IndexOf("PrepareImageResidency(candidate);", StringComparison.Ordinal);
        int realization = publish.IndexOf("RealizeVisibleEmbeds();", residency, StringComparison.Ordinal);
        int realizationFence = publish.IndexOf("IsCurrentRasterCodeActionPublication(", realization, StringComparison.Ordinal);
        int focusRing = publish.IndexOf("UpdateFocusRing();", realization, StringComparison.Ordinal);
        Assert.True(residency > oldCleanup && realization > residency &&
            realizationFence > realization && focusRing > realizationFence);
    }

    [Fact]
    public void ReboundButtonUsesNewPlanHandlerAndChecksPublicationAfterXamlCallbacks_SourceContractOnly()
    {
        string control = ReadSource("MarkdownRendererControl.cs");
        string method = ReadMethod(control, "public bool AdoptRealizedFromRasterCandidate(");

        int oldDetach = method.IndexOf("button.Click -= oldClickHandler", StringComparison.Ordinal);
        int newAttach = method.IndexOf("AttachHandlers(button, owner);", StringComparison.Ordinal);
        int automation = method.IndexOf("AutomationProperties.SetAutomationId", StringComparison.Ordinal);
        int automationFence = method.IndexOf("IsCurrentRasterActionOwner(", automation, StringComparison.Ordinal);
        int feedback = method.IndexOf("SetCodeBlockCopyButtonStateForRasterCandidate(", StringComparison.Ordinal);
        int placementCall = method.IndexOf("UpdatePlacementForRasterCandidate(", feedback, StringComparison.Ordinal);

        Assert.True(oldDetach >= 0 && newAttach > oldDetach && automation > newAttach && automationFence > automation);
        Assert.True(feedback > automationFence && placementCall > feedback);
        Assert.Contains("oldPlan.Box.RealizedCopyButton = null;", method, StringComparison.Ordinal);
        Assert.Contains("Box.RealizedCopyButton = fe;", method, StringComparison.Ordinal);

        string reset = ReadMethod(control, "private bool SetCodeBlockCopyButtonStateForRasterCandidate(");
        foreach (string callbackCapableSetter in new[]
                 {
                     "icon.Symbol = Symbol.Copy;",
                     "button.Content = new SymbolIcon(Symbol.Copy);",
                     "AutomationProperties.SetName(button, label);",
                     "ToolTipService.SetToolTip(button, label);",
                 })
        {
            int setter = reset.IndexOf(callbackCapableSetter, StringComparison.Ordinal);
            Assert.True(setter >= 0, $"Missing candidate-specific reset operation '{callbackCapableSetter}'.");
            Assert.True(reset.IndexOf("plan.IsCurrentRasterActionOwner(", setter, StringComparison.Ordinal) > setter);
        }
        Assert.True(reset.IndexOf("ResolvedCodeBlockCopyButtonLabel", StringComparison.Ordinal) <
            reset.IndexOf("button.Content", StringComparison.Ordinal));
        Assert.Contains("plan.IsCurrentRasterActionOwner(", reset, StringComparison.Ordinal);

        string placement = ReadMethod(control, "private bool UpdatePlacementForRasterCandidate(");
        foreach (string propertySetter in new[]
                 {
                     "element.Width = width;",
                     "element.Height = height;",
                     "Canvas.SetLeft(element, left);",
                     "Canvas.SetTop(element, top);",
                     "Canvas.SetZIndex(element, 2);",
                 })
        {
            int setter = placement.IndexOf(propertySetter, StringComparison.Ordinal);
            Assert.True(setter >= 0, $"Missing fenced placement operation '{propertySetter}'.");
            Assert.True(placement.IndexOf("IsCurrentRasterActionOwner(", setter, StringComparison.Ordinal) > setter);
        }

        string matching = ReadMethod(control, "public bool IsSameLogicalAction(CodeBlockActionPlan other)");
        Assert.Contains("ReferenceEquals(Box, other.Box)", matching, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveRasterCandidateSetupDoesNotDisableDocumentsWithCodeActions_SourceContractOnly()
    {
        string control = ReadSource("MarkdownRendererControl.cs");
        string setup = ReadMethod(control, "private void ProcessQueuedImageRelayout(long ticket)");
        int rasterInputs = setup.IndexOf("RasterPlacementInputs? rasterPlacement", StringComparison.Ordinal);
        int rasterFactory = setup.IndexOf("? new RasterPlacementInputs(", rasterInputs, StringComparison.Ordinal);
        Assert.True(rasterInputs >= 0 && rasterFactory > rasterInputs);

        string eligibility = setup[rasterInputs..rasterFactory];
        Assert.DoesNotContain("_codeBlockActionPlans", eligibility, StringComparison.Ordinal);
        Assert.DoesNotContain("HasMeasuredCodeBlocks", eligibility, StringComparison.Ordinal);
        Assert.Contains("TryPrepareCodeBlockActionPublication(plan", ReadSource("MarkdownRendererControl.ImageCandidate.cs"), StringComparison.Ordinal);
    }

    private static string ReadMethod(string source, string signature)
    {
        int member = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find '{signature}'.");
        int bodyStart = source.IndexOf('{', member + signature.Length);
        int expressionStart = source.IndexOf("=>", member + signature.Length, StringComparison.Ordinal);
        if (expressionStart >= 0 && (bodyStart < 0 || expressionStart < bodyStart))
        {
            int expressionEnd = source.IndexOf(';', expressionStart);
            Assert.True(expressionEnd >= 0, $"Could not find expression end for '{signature}'.");
            return source[expressionStart..(expressionEnd + 1)];
        }
        Assert.True(bodyStart >= 0, $"Could not find body for '{signature}'.");
        int depth = 0;
        for (int index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[bodyStart..(index + 1)];
        }
        throw new InvalidOperationException($"Could not find the end of '{signature}'.");
    }

    private static string ReadSource(string fileName)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer", "Controls", fileName);
            if (File.Exists(path))
                return File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        throw new FileNotFoundException("Could not locate the MarkdownRenderer control source.");
    }
}
