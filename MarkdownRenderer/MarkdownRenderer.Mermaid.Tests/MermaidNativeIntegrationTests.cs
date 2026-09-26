using System.Runtime.InteropServices;
using System.Text;
using MarkdownRenderer.Mermaid;

namespace MarkdownRenderer.Mermaid.Tests;

public sealed class MermaidNativeIntegrationTests
{
    private const string Source = "flowchart LR\n  A[Native] --> B[MMIR]";

    [Fact]
    public void PackagedRuntimeHasExpectedAbi()
    {
        MermaidNativeRuntimeInfo runtime = MermaidNativeRuntime.Probe();

        Assert.True(runtime.IsAvailable, runtime.Message);
        Assert.Equal(new MermaidNativeAbiVersion(1, 0), runtime.AbiVersion);
    }

    [Fact]
    public void WrongArchitectureDllIsRejectedBeforeLoad()
    {
        string arm64 = FindRepositoryFile(
            "MarkdownRenderer",
            "MarkdownRenderer.Mermaid",
            "runtimes",
            "win-arm64",
            "native",
            NativeMethods.LibraryName);

        MermaidNativeRuntimeInfo runtime = MermaidNativeRuntime.ProbeCore(arm64);

        Assert.Equal(MermaidNativeRuntimeStatus.IncompatibleArchitecture, runtime.Status);
    }

    [Fact]
    public void LoadedDllExportsEveryGeneratedAbiEntryPoint()
    {
        string x64 = FindRepositoryFile(
            "MarkdownRenderer",
            "MarkdownRenderer.Mermaid",
            "runtimes",
            "win-x64",
            "native",
            NativeMethods.LibraryName);
        nint library = NativeLibrary.Load(x64);
        try
        {
            foreach (string export in NativeMethods.RequiredExports)
            {
                Assert.True(NativeLibrary.TryGetExport(library, export, out _), export);
            }
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    [Fact]
    public async Task MermanRenderProducesValidatedBackendNeutralScene()
    {
        using var renderer = new MermaidRenderer();

        MermaidRenderResult result = await renderer.RenderAsync(Source);

        Assert.Equal(MermaidRenderStatus.Success, result.Status);
        Assert.False(result.ShouldUseFallback);
        MermaidScene scene = Assert.IsType<MermaidScene>(result.Scene);
        Assert.True(scene.Viewport.Width > 0);
        Assert.True(scene.Viewport.Height > 0);
        Assert.NotEmpty(scene.Commands);
        Assert.Contains(scene.Semantics, item => item.Role == MermaidSemanticRole.Diagram);
        Assert.NotEmpty(scene.SourceMappings);
        Assert.Equal(MermaidSceneVersion.Current, scene.Version);

        MermaidDrawCommand[] textCommands = scene.Commands
            .Where(static command => command.Opcode == MermaidDrawOpcode.Text)
            .ToArray();
        Assert.NotEmpty(textCommands);
        Assert.All(textCommands, command =>
        {
            Assert.Equal(4, command.GeometryCount);
            Assert.InRange(command.FontFamilyIndex, 0, scene.Strings.Count - 1);
            Assert.True(scene.Geometry[command.GeometryStart + 2] > 0);
            Assert.InRange(scene.Geometry[command.GeometryStart + 3], 1, 999);
        });

        MermaidSceneStyle[] nodeStyles = scene.Commands
            .Where(command => command.SemanticIndex >= 0 && command.StyleIndex >= 0 &&
                scene.Semantics[command.SemanticIndex].Role == MermaidSemanticRole.Node)
            .Select(command => scene.Styles[command.StyleIndex])
            .ToArray();
        MermaidSceneStyle[] edgeStyles = scene.Commands
            .Where(command => command.SemanticIndex >= 0 && command.StyleIndex >= 0 &&
                scene.Semantics[command.SemanticIndex].Role == MermaidSemanticRole.Edge)
            .Select(command => scene.Styles[command.StyleIndex])
            .ToArray();
        Assert.Contains(nodeStyles, style => style.Fill.Alpha > 0 && style.Fill != style.Stroke);
        Assert.Contains(edgeStyles, style => style.Stroke.Alpha > 0 && style.Fill.Alpha == 0);

        MermaidRenderResult cached = await renderer.RenderAsync(Source);
        Assert.Equal(MermaidRenderStatus.Success, cached.Status);
        Assert.Same(scene, cached.Scene);
        Assert.Equal(Source, cached.Fallback.Source);
    }

    [Fact]
    public async Task MermaidLinkIsAttachedToItsFocusableNodeSemantic()
    {
        const string sourceLf = "flowchart LR\n  A[Invokable Mermaid node] --> B[Native scene]\n  click A \"https://example.invalid/mermaid-node\" \"Open Mermaid node\"";
        using var renderer = new MermaidRenderer();

        foreach (string source in new[]
        {
            sourceLf,
            sourceLf + "\n",
            sourceLf.Replace("\n", "\r\n", StringComparison.Ordinal),
            sourceLf.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n",
        })
        {
            MermaidRenderResult result = await renderer.RenderAsync(source);

            Assert.Equal(MermaidRenderStatus.Success, result.Status);
            MermaidScene scene = Assert.IsType<MermaidScene>(result.Scene);
            MermaidSceneLink link = Assert.Single(scene.Links);
            Assert.Equal("https://example.invalid/mermaid-node", scene.Strings[link.TargetIndex]);
            Assert.Equal(MermaidLinkFlags.External, link.Flags & MermaidLinkFlags.External);
            MermaidSemanticItem semantic = scene.Semantics[link.SemanticIndex];
            Assert.Equal(MermaidSemanticRole.Node, semantic.Role);
            Assert.Equal(
                MermaidSemanticFlags.Focusable | MermaidSemanticFlags.Linked,
                semantic.Flags & (MermaidSemanticFlags.Focusable | MermaidSemanticFlags.Linked));
            Assert.True(
                semantic.NameIndex >= 0 && semantic.NameIndex < scene.Strings.Count,
                string.Join(" | ", scene.Semantics.Select((item, index) =>
                    $"{index}:{item.Role}:name={item.NameIndex}:parent={item.ParentIndex}:flags={item.Flags}")) +
                " :: mappings=" + string.Join(" | ", scene.SourceMappings.Select(mapping =>
                    $"sem={mapping.SemanticIndex}:source={mapping.SourceRange.Start}+{mapping.SourceRange.Length}")) +
                " :: commands=" + string.Join(" | ", scene.Commands
                    .Select(command => $"{command.Opcode}:sem={command.SemanticIndex}:text={command.TextIndex}:{(command.TextIndex >= 0 ? scene.Strings[command.TextIndex] : "<none>")}:geo=[{string.Join(',', scene.Geometry.Skip(command.GeometryStart).Take(command.GeometryCount))}]")));
            Assert.Contains("Invokable Mermaid node", scene.Strings[semantic.NameIndex], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task LinkedFlowchartLabelsAndArrowheadsStayWithTheirShapes()
    {
        const string source = "flowchart LR\n  A[Invokable Mermaid node] --> B[Native scene]\n  click A \"https://example.invalid/mermaid-node\" \"Open Mermaid node\"";
        using var renderer = new MermaidRenderer();
        MermaidRenderResult result = await renderer.RenderAsync(source);
        Assert.Equal(MermaidRenderStatus.Success, result.Status);
        MermaidScene scene = Assert.IsType<MermaidScene>(result.Scene);
        MermaidDrawCommand[] nodes = scene.Commands.Where(command => command.Opcode == MermaidDrawOpcode.Rectangle &&
            command.SemanticIndex >= 0 && scene.Semantics[command.SemanticIndex].Role == MermaidSemanticRole.Node).ToArray();
        Assert.Equal(2, nodes.Length);
        foreach (MermaidDrawCommand node in nodes)
        {
            float x = scene.Geometry[node.GeometryStart], y = scene.Geometry[node.GeometryStart + 1];
            float right = x + scene.Geometry[node.GeometryStart + 2], bottom = y + scene.Geometry[node.GeometryStart + 3];
            MermaidDrawCommand[] labels = scene.Commands.Where(command => command.Opcode == MermaidDrawOpcode.Text &&
                command.SemanticIndex == node.SemanticIndex).ToArray();
            Assert.NotEmpty(labels);
            foreach (MermaidDrawCommand label in labels)
            {
                Assert.InRange(scene.Geometry[label.GeometryStart], x, right);
                Assert.InRange(scene.Geometry[label.GeometryStart + 1], y + 1, bottom);
            }
        }
        MermaidDrawCommand arrow = Assert.Single(scene.Commands, command => command.Opcode == MermaidDrawOpcode.FillPath &&
            command.SemanticIndex >= 0 && scene.Semantics[command.SemanticIndex].Role == MermaidSemanticRole.Edge);
        float targetX = scene.Geometry[nodes[1].GeometryStart];
        for (int index = 0; index < arrow.GeometryCount; index += 2)
            Assert.InRange(scene.Geometry[arrow.GeometryStart + index], targetX - 20, targetX + 2);
    }

    [Fact]
    public async Task DecisionDiamondPreservesItsFillAndOutlinePasses()
    {
        using var renderer = new MermaidRenderer();
        MermaidRenderResult result = await renderer.RenderAsync(
            "flowchart LR\n  A{Decision} --> B[Done]");

        Assert.Equal(MermaidRenderStatus.Success, result.Status);
        MermaidScene scene = Assert.IsType<MermaidScene>(result.Scene);
        int decisionSemantic = Assert.Single(scene.Commands, command =>
            command.Opcode == MermaidDrawOpcode.Text &&
            command.TextIndex >= 0 &&
            string.Equals(scene.Strings[command.TextIndex], "Decision", StringComparison.Ordinal))
            .SemanticIndex;
        MermaidDrawCommand fill = Assert.Single(scene.Commands, command =>
            command.Opcode == MermaidDrawOpcode.FillPath &&
            command.SemanticIndex == decisionSemantic);
        MermaidDrawCommand stroke = Assert.Single(scene.Commands, command =>
            command.Opcode == MermaidDrawOpcode.StrokePath &&
            command.SemanticIndex == decisionSemantic);

        Assert.Equal(fill.StyleIndex, stroke.StyleIndex);
        Assert.Equal(fill.GeometryCount, stroke.GeometryCount);
        Assert.Equal(
            scene.Geometry.Skip(fill.GeometryStart).Take(fill.GeometryCount),
            scene.Geometry.Skip(stroke.GeometryStart).Take(stroke.GeometryCount));
        MermaidSceneStyle style = scene.Styles[fill.StyleIndex];
        Assert.True(style.Fill.Alpha > 0);
        Assert.True(style.Stroke.Alpha > 0);
        Assert.True(style.StrokeWidth > 0);
    }

    [Fact]
    public async Task DefaultPaletteIncludesItsOpaqueCanvasBackground()
    {
        using var renderer = new MermaidRenderer();
        MermaidRenderResult result = await renderer.RenderAsync("sequenceDiagram\n Alice->>Bob: Visible message");
        Assert.Equal(MermaidRenderStatus.Success, result.Status);
        MermaidScene scene = Assert.IsType<MermaidScene>(result.Scene);
        MermaidDrawCommand background = scene.Commands[0];
        Assert.Equal(MermaidDrawOpcode.Rectangle, background.Opcode);
        Assert.Equal((byte)255, scene.Styles[background.StyleIndex].Fill.Alpha);
        Assert.Equal(scene.Viewport.X, scene.Geometry[background.GeometryStart]);
        Assert.Equal(scene.Viewport.Y, scene.Geometry[background.GeometryStart + 1]);
        Assert.Equal(scene.Viewport.Width, scene.Geometry[background.GeometryStart + 2]);
        Assert.Equal(scene.Viewport.Height, scene.Geometry[background.GeometryStart + 3]);
    }

    [Fact]
    public async Task DashedSequenceResponsePreservesItsStrokePattern()
    {
        using var renderer = new MermaidRenderer();
        MermaidRenderResult result = await renderer.RenderAsync(
            "sequenceDiagram\n Alice->>Bob: Request\n Bob-->>Alice: Response");
        Assert.Equal(MermaidRenderStatus.Success, result.Status);
        MermaidScene scene = Assert.IsType<MermaidScene>(result.Scene);

        MermaidSceneStyle[] dashedStyles = scene.Commands
                .Where(command => command.StyleIndex >= 0 &&
                    command.Opcode is MermaidDrawOpcode.Line or MermaidDrawOpcode.StrokePath)
                .Select(command => scene.Styles[command.StyleIndex])
                .Distinct()
                .Where(style => style.DashGeometryCount > 0)
                .ToArray();
        Assert.NotEmpty(dashedStyles);
        MermaidSceneStyle dashed = dashedStyles[0];
        Assert.InRange(dashed.DashGeometryCount, 2u, 16u);
        Assert.All(
            scene.Geometry
                .Skip(checked((int)dashed.DashGeometryStart))
                .Take(checked((int)dashed.DashGeometryCount)),
            value => Assert.True(float.IsFinite(value) && value >= 0));
    }

    [Fact]
    public async Task PinnedPrimaryFamilyAdoptionCorpusCrossesNativeAbiAndDecoder()
    {
        (string Family, string Source)[] corpus =
        [
            ("er", "erDiagram\n  USER ||--o{ ITEM : owns"),
            ("flowchart", "flowchart LR\n  A --> B"),
            ("state", "stateDiagram-v2\n  [*] --> Ready"),
            ("class", "classDiagram\n  class Example"),
            ("sequence", "sequenceDiagram\n  Alice->>Bob: Hello"),
            ("info", "info"),
            ("pie", "pie\n  \"One\" : 1"),
            ("sankey", "sankey-beta\n\nsource,target,10"),
            ("packet", "packet-beta\n  0-7: \"Header\""),
            ("timeline", "timeline\n  2026 : Event"),
            ("journey", "journey\n  section Work\n    Ship: 5: Team"),
            ("kanban", "kanban\n  backlog[Backlog]\n    task[Task]"),
            ("gitgraph", "gitGraph\n  commit"),
            ("gantt", "gantt\n  title Plan\n  task :done, a, 2026-01-01, 1d"),
            ("c4", "C4Context\n  Person(user, \"User\", \"Person\")"),
            ("block", "block-beta\n  columns 1\n  A"),
            ("radar", "radar-beta\n  axis a\n  curve c{1}"),
            ("requirement", "requirementDiagram\n  requirement r {\n    id: 1\n    text: Test\n    risk: low\n    verifymethod: test\n  }"),
            ("mindmap", "mindmap\n  root\n    child"),
            ("architecture", "architecture-beta\n  service api(server)[API]"),
            ("quadrantchart", "quadrantChart\n  A: [0.2, 0.8]"),
            ("treemap", "treemap-beta\n  \"Root\"\n    \"Leaf\": 1"),
            ("xychart", "xychart-beta\n  x-axis [a, b]\n  bar [1, 2]"),
            ("treeView", "treeView-beta\n  \"root\"\n    \"child\""),
            ("ishikawa", "ishikawa-beta\n  Problem\n    Cause"),
            ("eventmodeling", "eventmodeling\n  timeframe 01 event Start"),
            ("error", "error"),
            ("venn", "venn-beta\n  set A[\"Alpha\"]:20"),
            ("swimlane", "swimlane-beta LR\n  A[Start] --> B[Done]"),
            ("railroad", "railroad-beta\n  rule = terminal(\"a\") ;"),
            ("railroad-ebnf", "railroad-ebnf-beta\n  rule ::= \"a\" ;"),
            ("railroad-abnf", "railroad-abnf-beta\n  rule = %x41 ;"),
            ("railroad-peg", "railroad-peg-beta\n  rule <- \"a\" ;"),
            ("wardley", "wardley-beta\n  title Map\n  anchor User [0.9, 0.9]\n  component App [0.5, 0.5]\n  User -> App"),
            ("cynefin", "cynefin-beta\n  clear\n    \"Runbook\""),
        ];
        Assert.Equal(35, corpus.Length);

        using var renderer = new MermaidRenderer();
        foreach ((string family, string source) in corpus)
        {
            MermaidRenderResult result = await renderer.RenderAsync(source);
            Assert.True(
                result.Status == MermaidRenderStatus.Success && result.Scene is not null,
                $"{family} failed the selected-RID C ABI/MMIR decoder boundary: {result.Status}: " +
                string.Join(" | ", result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            Assert.NotEmpty(result.Scene.Commands);
            Assert.Equal(MermaidSemanticRole.Diagram, result.Scene.Semantics[0].Role);
        }
    }

    [Fact]
    public async Task EmbeddedElkDirectiveFallsBackAtomically()
    {
        const string source = "---\nconfig:\n  layout: elk\n---\nflowchart LR\nA-->B";
        using var renderer = new MermaidRenderer();

        MermaidRenderResult result = await renderer.RenderAsync(source);

        Assert.Equal(MermaidRenderStatus.UnsupportedLayout, result.Status);
        Assert.Equal(source, result.Fallback.Source);
        Assert.Null(result.Scene);
    }

    [Fact]
    public async Task ElkWordsInsideAFlowchartLabelDoNotRequestElkLayout()
    {
        const string source = "flowchart LR\n  A[\"{\\\"layout\\\":\\\"elk\\\"}\"] --> B[\"layout: elk is data\"]";
        using var renderer = new MermaidRenderer();

        MermaidRenderResult result = await renderer.RenderAsync(source);

        Assert.Equal(MermaidRenderStatus.Success, result.Status);
        Assert.NotNull(result.Scene);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "MMR0002");
    }

    [Fact]
    public async Task MalformedSourceFuzzCrossesNativeAbiAndAlwaysFailsClosed()
    {
        var budgets = MermaidRenderBudgets.Default with
        {
            MaxSourceBytes = 4 * 1024,
            MaxNodes = 64,
            MaxEdges = 128,
            MaxDepth = 16,
            MaxLabelBytes = 256,
            MaxSceneBytes = 512 * 1024,
            MaxWorkingMemoryBytes = 8 * 1024 * 1024,
            Deadline = TimeSpan.FromMilliseconds(100),
            MaxConcurrentRenders = 1,
        };
        using var renderer = new MermaidRenderer(MermaidRenderOptions.Default with { Budgets = budgets });
        var random = new Random(0x4d4d_465a);
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-=>[]{}():'\"`,.%#_ /\\\n\r\t☃";

        for (int iteration = 0; iteration < 128; iteration++)
        {
            int length = random.Next(1, 513);
            var source = new char[length];
            for (int i = 0; i < source.Length; i++)
                source[i] = alphabet[random.Next(alphabet.Length)];
            string malformed = new(source);

            MermaidRenderResult result = await renderer.RenderAsync(malformed);

            Assert.Equal(malformed, result.Fallback.Source);
            Assert.Equal("mermaid", result.Fallback.Language);
            if (result.Status == MermaidRenderStatus.Success)
            {
                Assert.NotNull(result.Scene);
                Assert.False(result.ShouldUseFallback);
            }
            else
            {
                Assert.Null(result.Scene);
                Assert.True(result.ShouldUseFallback);
                Assert.NotEmpty(result.Diagnostics);
            }
        }

        // Every native handle created by the fuzz loop has left its SafeHandle
        // scope; a known-good render proves that failure paths did not poison or
        // tear down the shared engine state.
        MermaidRenderResult final = await renderer.RenderAsync(Source);
        Assert.Equal(MermaidRenderStatus.Success, final.Status);
        Assert.NotNull(final.Scene);
    }

    [Fact]
    public unsafe void NativeCancellationAndInvalidHandleStatusesAreExplicit()
    {
        CreateEngine(out MermaidEngineHandle engine);
        using (engine)
        {
            Assert.Equal(NativeStatus.Success, NativeMethods.CancellationCreate(out nint rawCancellation));
            using var cancellation = new MermaidCancellationHandle(rawCancellation);
            Assert.Equal(NativeStatus.Success, NativeMethods.CancellationRequest(rawCancellation));

            byte[] source = Encoding.UTF8.GetBytes(Source);
            NativeRenderOptions options = DefaultRenderOptions();
            fixed (byte* sourcePointer = source)
            {
                NativeStatus status = NativeMethods.Render(engine, sourcePointer, (uint)source.Length, in options, cancellation, out nint buffer);
                Assert.Equal(NativeStatus.Cancelled, status);
                Assert.Equal(0, buffer);
            }
        }

        Assert.Equal(NativeStatus.InvalidHandle, NativeMethods.EngineRelease((nint)int.MaxValue));
        Assert.Equal(NativeStatus.InvalidHandle, NativeMethods.BufferRelease((nint)int.MaxValue));
        Assert.Equal(NativeStatus.InvalidHandle, NativeMethods.CancellationRequest((nint)int.MaxValue));
    }

    [Fact]
    public unsafe void IncompatibleAbiAndStructSizeAreRejectedBeforeAllocation()
    {
        NativeEngineOptions options = DefaultEngineOptions();
        options.AbiVersion++;
        byte[] catalog = MermaidFontCatalog.Default.Serialize();
        fixed (byte* catalogPointer = catalog)
        {
            Assert.Equal(
                NativeStatus.IncompatibleAbi,
                NativeMethods.EngineCreate(in options, catalogPointer, (uint)catalog.Length, out nint engine));
            Assert.Equal(0, engine);
        }
    }

    [Fact]
    public unsafe void NativeAbiRejectsMalformedBuffersAndDoubleRelease()
    {
        CreateEngine(out MermaidEngineHandle engine);
        using (engine)
        {
            Assert.Equal(NativeStatus.Success, NativeMethods.CancellationCreate(out nint rawCancellation));
            using var cancellation = new MermaidCancellationHandle(rawCancellation);
            NativeRenderOptions options = DefaultRenderOptions();

            Assert.Equal(
                NativeStatus.InvalidInput,
                NativeMethods.Render(engine, null, 1, in options, cancellation, out nint nullBuffer));
            Assert.Equal(0, nullBuffer);

            byte[] malformedUtf8 = [0xff, 0xfe];
            fixed (byte* sourcePointer = malformedUtf8)
            {
                Assert.Equal(
                    NativeStatus.InvalidInput,
                    NativeMethods.Render(engine, sourcePointer, (uint)malformedUtf8.Length, in options, cancellation, out nint invalidBuffer));
                Assert.Equal(0, invalidBuffer);
            }

            nint rawEngine = engine.DangerousGetHandle();
            Assert.Equal(NativeStatus.Success, NativeMethods.EngineRelease(rawEngine));
            Assert.Equal(NativeStatus.InvalidHandle, NativeMethods.EngineRelease(rawEngine));
            engine.SetHandleAsInvalid();
        }
    }

    [Fact]
    public async Task EngineReleaseRacingRenderNeverUsesFreedState()
    {
        MermaidEngineHandle engine;
        unsafe
        {
            CreateEngine(out MermaidEngineHandle createdEngine);
            engine = createdEngine;
        }
        Assert.Equal(NativeStatus.Success, NativeMethods.CancellationCreate(out nint rawCancellation));
        using var cancellation = new MermaidCancellationHandle(rawCancellation);
        byte[] source = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(0, 400).Select(i => i == 0 ? "flowchart TD" : $"N{i - 1}-->N{i}")));
        NativeRenderOptions options = DefaultRenderOptions();
        nint rawEngine = engine.DangerousGetHandle();

        Task<NativeStatus> render = Task.Run(() =>
        {
            unsafe
            {
                fixed (byte* sourcePointer = source)
                {
                    NativeStatus status = NativeMethods.Render(engine, sourcePointer, (uint)source.Length, in options, cancellation, out nint rawBuffer);
                    if (rawBuffer != 0)
                    {
                        Assert.Equal(NativeStatus.Success, NativeMethods.BufferRelease(rawBuffer));
                    }
                    return status;
                }
            }
        });

        NativeStatus release = NativeMethods.EngineRelease(rawEngine);
        NativeStatus renderStatus = await render;
        engine.SetHandleAsInvalid();

        Assert.Equal(NativeStatus.Success, release);
        Assert.True(renderStatus is NativeStatus.Success or NativeStatus.InvalidHandle, renderStatus.ToString());
    }

    private static unsafe void CreateEngine(out MermaidEngineHandle engine)
    {
        NativeEngineOptions options = DefaultEngineOptions();
        byte[] catalog = MermaidFontCatalog.Default.Serialize();
        fixed (byte* catalogPointer = catalog)
        {
            Assert.Equal(NativeStatus.Success, NativeMethods.EngineCreate(in options, catalogPointer, (uint)catalog.Length, out nint rawEngine));
            Assert.NotEqual(0, rawEngine);
            engine = new MermaidEngineHandle(rawEngine);
        }
    }

    private static NativeEngineOptions DefaultEngineOptions() => new()
    {
        StructSize = (uint)Marshal.SizeOf<NativeEngineOptions>(),
        AbiVersion = NativeMethods.PackedAbiVersion,
        MaxWorkingMemoryBytes = (ulong)MermaidRenderBudgets.DefaultMaxWorkingMemoryBytes,
        MaxConcurrentRenders = MermaidRenderBudgets.DefaultMaxConcurrentRenders,
    };

    private static NativeRenderOptions DefaultRenderOptions() => new()
    {
        StructSize = (uint)Marshal.SizeOf<NativeRenderOptions>(),
        MaxSourceBytes = MermaidRenderBudgets.DefaultMaxSourceBytes,
        MaxNodes = MermaidRenderBudgets.DefaultMaxNodes,
        MaxEdges = MermaidRenderBudgets.DefaultMaxEdges,
        MaxDepth = MermaidRenderBudgets.DefaultMaxDepth,
        MaxLabelBytes = MermaidRenderBudgets.DefaultMaxLabelBytes,
        MaxSceneBytes = MermaidRenderBudgets.DefaultMaxSceneBytes,
        DeadlineMilliseconds = 2_000,
    };

    private static string FindRepositoryFile(params string[] components)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. components]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Repository test asset was not found: {Path.Combine(components)}");
    }
}
