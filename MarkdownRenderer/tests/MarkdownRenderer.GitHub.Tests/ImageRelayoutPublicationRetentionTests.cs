using System.Reflection;
using System.Runtime.CompilerServices;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ImageRelayoutPublicationRetentionTests
{
    [Theory]
    [InlineData("MarkdownRenderer.Controls.MarkdownRendererControl", "ReportImageRelayoutFailure")]
    [InlineData("MarkdownRenderer.Theming.MarkdownEnvironmentMonitor", "LogDetachFailure")]
    [InlineData("MarkdownRenderer.Performance.MarkdownPerformanceSession", "LogLeaseCleanupFailure")]
    public void CleanupDiagnosticsPreserveTheExistingDebugOnlyCallContract(string typeName, string methodName)
    {
        Assembly ownerAssembly = typeName == typeof(MarkdownRenderer.Performance.MarkdownPerformanceSession).FullName
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
        Type closure = Assert.Single(control.GetNestedTypes(BindingFlags.NonPublic),
            type => type.GetMethods(declaredMethods).Any(method =>
                method.Name.StartsWith("<OnImageLoadCompleted>", StringComparison.Ordinal)));

        FieldInfo[] fields = closure.GetFields(instanceFields);
        Assert.Contains(fields, field => field.FieldType == typeof(bool));
        Assert.Equal(2, fields.Count(field => field.FieldType.IsGenericType &&
            field.FieldType.GetGenericTypeDefinition() == typeof(WeakReference<>)));
        Assert.DoesNotContain(fields, field => field.FieldType == control ||
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
        Type closure = Assert.Single(control.GetNestedTypes(BindingFlags.NonPublic),
            type => type.GetMethods(declaredMethods).Any(method =>
                method.Name.StartsWith("<CreateImageRelayoutPublication>", StringComparison.Ordinal)));

        FieldInfo[] fields = closure.GetFields(instanceFields);
        Assert.Contains(fields, field => field.FieldType == typeof(CancellationTokenSource));
        Assert.Contains(fields, field => field.FieldType.IsGenericType &&
            field.FieldType.GetGenericTypeDefinition() == typeof(WeakReference<>));
        Assert.DoesNotContain(fields, field =>
            field.FieldType == typeof(LayoutSnapshot) || field.FieldType == typeof(int[]) ||
            field.FieldType == control ||
            field.FieldType == typeof(ImageRelayoutWorkPayload<LayoutSnapshot>));

        MethodInfo factory = control.GetMethod(
            "CreateImageRelayoutPublication", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.True(factory.IsStatic);
        Assert.Contains(factory.GetParameters(), parameter =>
            parameter.ParameterType == typeof(ImageRelayoutWorkPayload<LayoutSnapshot>));
        Assert.DoesNotContain(factory.GetParameters(), parameter =>
            parameter.ParameterType == typeof(LayoutSnapshot) || parameter.ParameterType == typeof(int[]));
        Assert.NotEqual(0, (int)(factory.GetMethodImplementationFlags() & MethodImplAttributes.NoInlining));

        MethodInfo worker = control.GetMethod(
            "RunImageRelayoutAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.True(worker.IsStatic);
        Assert.Contains(worker.GetParameters(), parameter =>
            parameter.ParameterType == typeof(ImageRelayoutWorkPayload<LayoutSnapshot>));
        Assert.DoesNotContain(worker.GetParameters(), parameter =>
            parameter.ParameterType == typeof(LayoutSnapshot) || parameter.ParameterType == typeof(int[]));

        Type stateMachine = worker.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        Assert.DoesNotContain(stateMachine.GetFields(instanceFields), field =>
            field.FieldType == control ||
            field.FieldType == typeof(LayoutSnapshot) || field.FieldType == typeof(int[]));
    }

    [Theory]
    [InlineData("<QueueImageRelayout>")]
    [InlineData("<StartImageRelayoutRecovery>")]
    public void CompiledDispatchAndRecoveryCallbacksDoNotCaptureControlOrSnapshot(string callbackPrefix)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags methods = BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        Type control = typeof(MarkdownDocumentView).BaseType!;
        var compiledCallbacks = control.GetNestedTypes(BindingFlags.NonPublic)
            .SelectMany(type => type.GetMethods(methods)
                .Where(method => method.Name.StartsWith(callbackPrefix, StringComparison.Ordinal))
                .Select(method => (Closure: type, Method: method)))
            .ToArray();

        if (callbackPrefix == "<QueueImageRelayout>")
        {
            var callback = Assert.Single(compiledCallbacks);
            Assert.Contains(callback.Closure.GetFields(fields), field => field.FieldType.IsGenericType &&
                field.FieldType.GetGenericTypeDefinition() == typeof(WeakReference<>));
            Assert.DoesNotContain(callback.Closure.GetFields(fields), field =>
                field.FieldType == control || field.FieldType == typeof(LayoutSnapshot) ||
                field.FieldType == typeof(ImageRelayoutWorkPayload<LayoutSnapshot>));
            return;
        }

        Assert.Equal("<StartImageRelayoutRecovery>", callbackPrefix);
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
                field.FieldType == control || field.FieldType == typeof(LayoutSnapshot) ||
                field.FieldType == typeof(ImageRelayoutWorkPayload<LayoutSnapshot>));
        }

        Type recoveryTimer = control.Assembly.GetType(
            "MarkdownRenderer.Controls.WeakRecoveryTimer`1", throwOnError: true)!;
        FieldInfo target = recoveryTimer.GetField("_target", fields)!;
        Assert.Equal(typeof(WeakReference<>), target.FieldType.GetGenericTypeDefinition());
        Assert.DoesNotContain(recoveryTimer.GetFields(fields), field => field.FieldType == control);
        MethodInfo sharedTick = control.GetMethod(
            "ProcessImageRelayoutRecovery", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.True(sharedTick.IsStatic);
    }
}
