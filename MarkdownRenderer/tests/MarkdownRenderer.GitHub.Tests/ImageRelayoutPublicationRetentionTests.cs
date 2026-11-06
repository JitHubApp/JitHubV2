using System.Reflection;
using System.Runtime.CompilerServices;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Controls.Images;
using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ImageRelayoutPublicationRetentionTests
{
    [Theory]
    [InlineData("MarkdownRenderer.Controls.RelayoutDiagnostics", "ReportFailure")]
    [InlineData("MarkdownRenderer.Theming.MarkdownEnvironmentMonitor", "LogDetachFailure")]
    [InlineData("MarkdownRenderer.Performance.MarkdownSessionAdmissions", "LogLeaseCleanupFailure")]
    public void CleanupDiagnosticsPreserveTheExistingDebugOnlyCallContract(string typeName, string methodName)
    {
        Assembly ownerAssembly = typeName.StartsWith("MarkdownRenderer.Performance.", StringComparison.Ordinal)
            ? typeof(MarkdownRenderer.Performance.MarkdownPerformanceSession).Assembly
            : typeof(MarkdownDocumentView).Assembly;
        Type owner = ownerAssembly.GetType(typeName, throwOnError: true)!;
        MethodInfo report = owner.GetMethod(
            methodName, BindingFlags.Static | BindingFlags.NonPublic)!;
        System.Diagnostics.ConditionalAttribute attribute = Assert.Single(
            report.GetCustomAttributes<System.Diagnostics.ConditionalAttribute>());
        Assert.Equal("DEBUG", attribute.ConditionString);
    }

    [Fact]
    public void CompiledImageCompletionDoesNotCaptureReceiverSenderOrEventData()
    {
        const BindingFlags instanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags declaredMethods = BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        Type control = typeof(MarkdownDocumentView).BaseType!;
        Type completion = typeof(ImageLoadCompletion);
        Type closure = Assert.Single(completion.GetNestedTypes(BindingFlags.NonPublic),
            type => type.GetMethods(declaredMethods).Any(method =>
                method.Name.StartsWith("<OnLoadCompleted>", StringComparison.Ordinal)));

        FieldInfo[] fields = closure.GetFields(instanceFields);
        Assert.Contains(fields, field => field.FieldType == typeof(bool));
        Assert.Equal(2, fields.Count(field => field.FieldType.IsGenericType &&
            field.FieldType.GetGenericTypeDefinition() == typeof(WeakReference<>)));
        Assert.Contains(fields, field => field.FieldType == typeof(WeakReference<ImageLoadCompletion>));
        Assert.DoesNotContain(fields, field => field.FieldType == control || field.FieldType == completion ||
            field.FieldType == typeof(object) ||
            field.FieldType == typeof(MarkdownRenderer.Layout.Boxes.ImageBox) ||
            field.FieldType == typeof(MarkdownRenderer.Layout.Boxes.LoadCompletedEventArgs) ||
            field.FieldType == typeof(LayoutSnapshot));
    }

    [Fact]
    public void CompiledControlHandoffDoesNotCaptureGraphOrDirtyIndices()
    {
        const BindingFlags instanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags declaredMethods = BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        Type control = typeof(MarkdownDocumentView).BaseType!;
        Type work = typeof(ImageRelayoutWork);
        Type closure = Assert.Single(work.GetNestedTypes(BindingFlags.NonPublic),
            type => type.GetMethods(declaredMethods).Any(method =>
                method.Name.StartsWith("<CreatePublication>", StringComparison.Ordinal)));

        FieldInfo[] fields = closure.GetFields(instanceFields);
        Assert.Contains(fields, field => field.FieldType == typeof(RetirableCancellationTokenSource));
        Assert.Contains(fields, field => field.FieldType == typeof(WeakReference<ImageRelayout>));
        Assert.DoesNotContain(fields, field =>
            field.FieldType == typeof(LayoutSnapshot) || field.FieldType == typeof(int[]) ||
            field.FieldType == control || field.FieldType == typeof(ImageRelayout) ||
            field.FieldType == typeof(ImageRelayoutWorkPayload<LayoutSnapshot>));

        MethodInfo factory = work.GetMethod(
            "CreatePublication", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.True(factory.IsStatic);
        Assert.Contains(factory.GetParameters(), parameter =>
            parameter.ParameterType == typeof(ImageRelayoutWorkPayload<LayoutSnapshot>));
        Assert.DoesNotContain(factory.GetParameters(), parameter =>
            parameter.ParameterType == typeof(LayoutSnapshot) || parameter.ParameterType == typeof(int[]));
        Assert.NotEqual(0, (int)(factory.GetMethodImplementationFlags() & MethodImplAttributes.NoInlining));

        MethodInfo worker = work.GetMethod(
            "RunAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.True(worker.IsStatic);
        Assert.Contains(worker.GetParameters(), parameter =>
            parameter.ParameterType == typeof(ImageRelayoutWorkPayload<LayoutSnapshot>));
        Assert.DoesNotContain(worker.GetParameters(), parameter =>
            parameter.ParameterType == typeof(LayoutSnapshot) || parameter.ParameterType == typeof(int[]));

        Type stateMachine = worker.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        Assert.DoesNotContain(stateMachine.GetFields(instanceFields), field =>
            field.FieldType == control || field.FieldType == typeof(ImageRelayout) ||
            field.FieldType == typeof(LayoutSnapshot) || field.FieldType == typeof(int[]));
    }

    [Fact]
    public void CompiledRelayoutDispatchCallbackDoesNotCaptureControlOrSnapshot()
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags methods = BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        Type control = typeof(MarkdownDocumentView).BaseType!;
        var callback = Assert.Single(typeof(ImageRelayout).GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(type => type.GetMethods(methods)
                .Where(method => method.Name.StartsWith("<Queue>", StringComparison.Ordinal))
                .Select(method => (Closure: type, Method: method))));

        Assert.Contains(callback.Closure.GetFields(fields), field =>
            field.FieldType == typeof(WeakReference<ImageRelayout>));
        Assert.DoesNotContain(callback.Closure.GetFields(fields), field =>
            field.FieldType == control || field.FieldType == typeof(ImageRelayout) ||
            field.FieldType == typeof(LayoutSnapshot) ||
            field.FieldType == typeof(ImageRelayoutWorkPayload<LayoutSnapshot>));
    }

    [Fact]
    public void CompiledRecoveryClockCallbacksDoNotCaptureClockControlOrSnapshot()
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags methods = BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        Type control = typeof(MarkdownDocumentView).BaseType!;
        Type clock = typeof(RecoveryClock);
        var compiledCallbacks = clock.GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(type => type.GetMethods(methods)
                .Where(method => method.Name.StartsWith("<Start>", StringComparison.Ordinal))
                .Select(method => (Closure: type, Method: method)))
            .ToArray();

        Assert.Equal(2, compiledCallbacks.Length);
        var nativeTick = Assert.Single(compiledCallbacks,
            callback => callback.Method.GetParameters().Length == 2);
        var managedEnqueue = Assert.Single(compiledCallbacks,
            callback => callback.Method.GetParameters() is [{ ParameterType: var parameterType }] &&
                parameterType == typeof(Action));

        Assert.Contains(nativeTick.Closure.GetFields(fields), field => field.FieldType.IsGenericType &&
            field.FieldType.GetGenericTypeDefinition() == typeof(WeakReference<>));
        Assert.Contains(managedEnqueue.Closure.GetFields(fields), field =>
            field.FieldType == typeof(Microsoft.UI.Dispatching.DispatcherQueue));
        foreach (var callback in compiledCallbacks)
        {
            Assert.DoesNotContain(callback.Closure.GetFields(fields), field =>
                field.FieldType == clock || field.FieldType == control ||
                field.FieldType == typeof(LayoutSnapshot) ||
                field.FieldType == typeof(ImageRelayoutWorkPayload<LayoutSnapshot>));
        }

        Type recoveryTimer = control.Assembly.GetType(
            "MarkdownRenderer.Controls.WeakRecoveryTimer`1", throwOnError: true)!;
        FieldInfo target = recoveryTimer.GetField("_target", fields)!;
        Assert.Equal(typeof(WeakReference<>), target.FieldType.GetGenericTypeDefinition());
        Assert.DoesNotContain(recoveryTimer.GetFields(fields), field =>
            field.FieldType == clock || field.FieldType == control);
        MethodInfo sharedTick = clock.GetMethod(
            "Tick", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.True(sharedTick.IsStatic);
    }
}
