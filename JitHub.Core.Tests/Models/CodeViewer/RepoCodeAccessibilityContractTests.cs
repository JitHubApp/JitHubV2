using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using JitHub.Core.Models.CodeViewer;
using JitHub.Core.Services.CodeViewer;
using JitHub.Core.ViewModels.CodeViewer;
using Xunit;

namespace JitHub.Core.Tests.Models.CodeViewer;

public sealed class RepoCodeAccessibilityContractTests
{
    [Fact]
    public void TreeNodesExposeFilenameAndStablePathBasedAutomationId()
    {
        var model = new RepoTreeNode
        {
            Name = "Program.cs",
            Path = "src/JitHub/Program.cs",
            Sha = "abc123",
            IsDirectory = false,
        };
        var viewModel = new RepoTreeNodeViewModel(model, new StubLanguageResolver());

        Assert.Equal("Program.cs", viewModel.ToString());
        Assert.Equal("Program.cs, file", viewModel.AutomationName);
        Assert.Equal(
            RepoCodeAutomation.CreateId("RepoCodeTreeItem", "path:src/JitHub/Program.cs"),
            viewModel.AutomationId);
        Assert.StartsWith("RepoCodeTreeItem_path_src_JitHub_Program_", viewModel.AutomationId, StringComparison.Ordinal);
    }

    [Fact]
    public void BreadcrumbSegmentsExposeMeaningfulStableAutomationContracts()
    {
        var segment = new BreadcrumbSegment("CodeViewer", "src/CodeViewer", IsRoot: false);

        Assert.Equal("Open CodeViewer", segment.AutomationName);
        Assert.Equal("src/CodeViewer", segment.AutomationPath);
        Assert.Equal(
            RepoCodeAutomation.CreateId("RepoCodeBreadcrumbSegment", "path:src/CodeViewer"),
            segment.AutomationId);
    }

    [Fact]
    public void AutomationIdsHashRawSemanticIdentityWithoutWhitespaceCollisions()
    {
        string pathWithWhitespace = RepoCodeAutomation.CreateId("RepoCodeTreeItem", "path:src/ file.cs ");
        string trimmedPath = RepoCodeAutomation.CreateId("RepoCodeTreeItem", "path:src/ file.cs");
        string root = RepoCodeAutomation.CreateId("RepoCodeBreadcrumbSegment", "root:src/file.cs");
        string path = RepoCodeAutomation.CreateId("RepoCodeBreadcrumbSegment", "path:src/file.cs");

        Assert.NotEqual(pathWithWhitespace, trimmedPath);
        Assert.NotEqual(root, path);
    }

    [Fact]
    public void OutlineSymbolsExposeStableSemanticAutomationIdentity()
    {
        CodeSymbol symbol = new("Render", "method", 42);

        Assert.Equal(
            RepoCodeAutomation.CreateId("RepoCodeOutlineItem", "symbol:method:Render:42"),
            symbol.AutomationId);
        Assert.Equal("method Render, line 42", symbol.AutomationName);
    }

    private sealed class StubLanguageResolver : ILanguageIdResolver
    {
        public string Resolve(string fileName, ReadOnlySpan<byte> contentSniff = default) => "csharp";

        public bool IsKnown(string fileName) => true;
    }
}
