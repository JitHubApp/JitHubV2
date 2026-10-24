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

    [Fact]
    public void StartLoadUsesDemandIdentityAndForwardsItBeforeReleasingQueuedWork()
    {
        string source = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Layout", "Boxes", "ImageBox.cs");
        string startLoad = GetMethodBody(source, "private void StartLoad(");

        MatchCollection scheduleCalls = Regex.Matches(
            startLoad,
            @"TryScheduleImageActivation\s*\(\s*([^,]+),",
            RegexOptions.CultureInvariant);
        Assert.Equal(2, scheduleCalls.Count);
        Assert.All(scheduleCalls.Cast<Match>(), match =>
            Assert.Equal("demand", match.Groups[1].Value.Trim()));

        MatchCollection resetCalls = Regex.Matches(
            startLoad,
            @"ResetLoadStartedAfterUnstartedActivation\s*\(\s*([^,]+),\s*([^,]+),\s*([^)]+)\)",
            RegexOptions.CultureInvariant);
        Assert.Equal(5, resetCalls.Count);
        Assert.All(resetCalls.Cast<Match>(), match =>
        {
            Assert.Equal("demand", match.Groups[1].Value.Trim());
            Assert.Equal("loadDemandGeneration", match.Groups[2].Value.Trim());
            Assert.Equal("queuedWork.Token", match.Groups[3].Value.Trim());
            int release = startLoad.IndexOf("queuedWork.Dispose();", match.Index, StringComparison.Ordinal);
            Assert.True(release > match.Index, "The queued lease must be released after its reset callback.");
        });
    }

    [Fact]
    public void SchedulerFailureResetsItsExactActivationBeforeReleasingTheQueuedLease()
    {
        string source = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Layout", "Boxes", "ImageBox.cs");
        string startLoad = GetMethodBody(source, "private void StartLoad(");
        MatchCollection catchBodies = Regex.Matches(
            startLoad,
            @"catch\s*\{([^{}]+)\}",
            RegexOptions.CultureInvariant);
        Assert.Equal(2, catchBodies.Count);
        Assert.All(catchBodies.Cast<Match>(), match =>
        {
            string body = match.Groups[1].Value;
            int reset = body.IndexOf("ResetLoadStartedAfterUnstartedActivation(", StringComparison.Ordinal);
            int release = body.IndexOf("queuedWork.Dispose();", StringComparison.Ordinal);
            int rethrow = body.IndexOf("throw;", StringComparison.Ordinal);
            Assert.True(reset >= 0 && release > reset && rethrow > release);
        });
    }

    [Fact]
    public void ResetHelperIsAtomicAndRejectsEveryStaleOrCanceledIdentityBeforeClearing()
    {
        string source = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Layout", "Boxes", "ImageBox.cs");
        string reset = GetMethodBody(
            source,
            "private void ResetLoadStartedAfterUnstartedActivation(");

        int gate = reset.IndexOf("lock (_svgHostDeferredSync)", StringComparison.Ordinal);
        int disposed = reset.IndexOf("_disposed", StringComparison.Ordinal);
        int suspended = reset.IndexOf("_resourcesSuspended", StringComparison.Ordinal);
        int exactDemand = reset.IndexOf(
            "ReferenceEquals(Volatile.Read(ref _svgLoadDemandTicket), demand)",
            StringComparison.Ordinal);
        int exactGeneration = reset.IndexOf(
            "loadGeneration != Volatile.Read(ref _svgLoadDemandGeneration)",
            StringComparison.Ordinal);
        int cancellation = reset.IndexOf("activationCancellation.IsCancellationRequested", StringComparison.Ordinal);
        int resetState = reset.IndexOf("_loadStarted = false", StringComparison.Ordinal);

        Assert.True(gate >= 0 && disposed > gate && suspended > gate);
        Assert.True(exactDemand > gate && exactGeneration > gate && cancellation > gate);
        Assert.True(resetState > disposed && resetState > suspended && resetState > exactDemand &&
            resetState > exactGeneration && resetState > cancellation);
        Assert.Contains("return;", reset, StringComparison.Ordinal);
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

    private static string GetMethodBody(string source, string signature)
    {
        int member = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find method '{signature}'.");
        int bodyStart = source.IndexOf('{', member + signature.Length);
        Assert.True(bodyStart >= 0, $"Could not find method body '{signature}'.");

        int depth = 0;
        for (int index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[bodyStart..(index + 1)];
        }

        throw new InvalidOperationException($"Could not find the end of '{signature}'.");
    }

    private static string ReadRepositorySource(params string[] pathParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string path = directory.FullName;
            foreach (string part in pathParts)
                path = Path.Combine(path, part);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        throw new FileNotFoundException($"Could not find source {Path.Combine(pathParts)}.");
    }
}
