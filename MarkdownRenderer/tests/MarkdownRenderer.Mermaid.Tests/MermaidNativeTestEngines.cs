using System.Runtime.InteropServices;
using MarkdownRenderer.Mermaid;
using MarkdownRenderer.Mermaid.Native.Internal;

namespace MarkdownRenderer.Mermaid.Tests;

/// <summary>Creates native Mermaid engines and their default options for ABI tests.</summary>
internal static class MermaidNativeTestEngines
{
    internal const string Source = "flowchart LR\n  A[Native] --> B[MMIR]";

    internal static unsafe void CreateEngine(out MermaidEngineHandle engine)
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

    internal static NativeEngineOptions DefaultEngineOptions() => new()
    {
        StructSize = (uint)Marshal.SizeOf<NativeEngineOptions>(),
        AbiVersion = NativeMethods.PackedAbiVersion,
        MaxWorkingMemoryBytes = (ulong)MermaidRenderBudgets.DefaultMaxWorkingMemoryBytes,
        MaxConcurrentRenders = MermaidRenderBudgets.DefaultMaxConcurrentRenders,
    };

    internal static NativeRenderOptions DefaultRenderOptions() => new()
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
}
