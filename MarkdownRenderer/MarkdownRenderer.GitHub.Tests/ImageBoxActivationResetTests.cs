using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ImageBoxActivationResetTests
{
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void CurrentUnstartedActivationClearsItsOwnLoadStartedState()
    {
        MarkdownSvgDemandTicket demand = OverscanDemand();
        ImageBox box = BareBox(demand, loadGeneration: 41);

        ResetUnstartedActivation(box, demand, 41, CancellationToken.None);

        Assert.False(GetField<bool>(box, "_loadStarted"));
    }

    [Fact]
    public void StaleDemandOrGenerationCannotClearCurrentLoadStartedState()
    {
        MarkdownSvgDemandTicket current = OverscanDemand();
        ImageBox box = BareBox(current, loadGeneration: 42);

        ResetUnstartedActivation(box, OverscanDemand(), 42, CancellationToken.None);
        Assert.True(GetField<bool>(box, "_loadStarted"));

        ResetUnstartedActivation(box, current, 41, CancellationToken.None);
        Assert.True(GetField<bool>(box, "_loadStarted"));
    }

    [Fact]
    public void CanceledQueuedActivationCannotClearLoadStartedState()
    {
        MarkdownSvgDemandTicket demand = OverscanDemand();
        ImageBox box = BareBox(demand, loadGeneration: 43);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        ResetUnstartedActivation(box, demand, 43, cancellation.Token);

        Assert.True(GetField<bool>(box, "_loadStarted"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DisposedOrSuspendedBoxDoesNotResetLoadStarted(
        bool disposed,
        bool suspended)
    {
        MarkdownSvgDemandTicket demand = OverscanDemand();
        ImageBox box = BareBox(demand, loadGeneration: 44);
        SetField(box, "_disposed", disposed);
        SetField(box, "_resourcesSuspended", suspended);

        ResetUnstartedActivation(box, demand, 44, CancellationToken.None);

        Assert.True(GetField<bool>(box, "_loadStarted"));
    }

    [Fact]
    public void OldCanceledActivationCannotResetResumedLoadWithSameDemandAndGeneration()
    {
        MarkdownSvgDemandTicket demand = OverscanDemand();
        ImageBox box = BareBox(demand, loadGeneration: 45);
        using var oldResidencyCancellation = new CancellationTokenSource();
        oldResidencyCancellation.Cancel();

        // Model a resume that deliberately reuses the load identity while a
        // late callback from the canceled pre-suspend activation is delivered.
        SetField(box, "_resourcesSuspended", false);
        SetField(box, "_loadStarted", true);
        ResetUnstartedActivation(box, demand, 45, oldResidencyCancellation.Token);

        Assert.True(GetField<bool>(box, "_loadStarted"));
    }

    private static ImageBox BareBox(MarkdownSvgDemandTicket demand, int loadGeneration)
    {
        var box = (ImageBox)RuntimeHelpers.GetUninitializedObject(typeof(ImageBox));
        SetField(box, "_svgHostDeferredSync", new object());
        SetField(box, "_svgLoadDemandTicket", demand);
        SetField(box, "_svgLoadDemandGeneration", loadGeneration);
        SetField(box, "_disposed", false);
        SetField(box, "_resourcesSuspended", false);
        SetField(box, "_loadStarted", true);
        return box;
    }

    private static MarkdownSvgDemandTicket OverscanDemand() =>
        new(MarkdownSvgRenderPriority.Overscan);

    private static T GetField<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, InstancePrivate)
            ?? throw new Xunit.Sdk.XunitException($"Missing field '{name}'."))
            .GetValue(target)!;

    private static void SetField(object target, string name, object? value)
    {
        FieldInfo field = target.GetType().GetField(name, InstancePrivate)
            ?? throw new Xunit.Sdk.XunitException($"Missing field '{name}'.");
        field.SetValue(target, value);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ResetLoadStartedAfterUnstartedActivation")]
    private static extern void ResetUnstartedActivation(
        ImageBox box,
        MarkdownSvgDemandTicket demand,
        int loadGeneration,
        CancellationToken activationCancellation);
}
