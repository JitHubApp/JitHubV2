using System.Runtime.InteropServices;
using System.Text;
using MarkdownRenderer.Mermaid;
using MarkdownRenderer.Mermaid.Internal.Rendering;
using MarkdownRenderer.Mermaid.NativeRuntime.Internal;
using MarkdownRenderer.Mermaid.Tests.TestSupport;

namespace MarkdownRenderer.Mermaid.Tests.NativeRuntime.Internal;

/// <summary>The native ABI fails closed on malformed source and on engine release racing a render.</summary>
public sealed class MermaidNativeFailClosedTests
{
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
        MermaidRenderResult final = await renderer.RenderAsync(MermaidNativeTestEngines.Source);
        Assert.Equal(MermaidRenderStatus.Success, final.Status);
        Assert.NotNull(final.Scene);
    }

    [Fact]
    public async Task EngineReleaseRacingRenderNeverUsesFreedState()
    {
        MermaidEngineHandle engine;
        unsafe
        {
            MermaidNativeTestEngines.CreateEngine(out MermaidEngineHandle createdEngine);
            engine = createdEngine;
        }
        Assert.Equal(NativeStatus.Success, NativeMethods.CancellationCreate(out nint rawCancellation));
        using var cancellation = new MermaidCancellationHandle(rawCancellation);
        byte[] source = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(0, 400).Select(i => i == 0 ? "flowchart TD" : $"N{i - 1}-->N{i}")));
        NativeRenderOptions options = MermaidNativeTestEngines.DefaultRenderOptions();
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
}
