using System.Reflection;
using System.Runtime.CompilerServices;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class CodeBlockHighlightSnapshotRetentionTests
{
    private const BindingFlags InstanceFields =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags DeclaredMethods =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
        BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    [Fact]
    public void CompiledHighlightTasksCarryOnlyWeakSnapshotIdentityAndCopiedText()
    {
        Type control = typeof(MarkdownDocumentView).BaseType!;
        Type snapshotIdentityType = typeof(WeakReference<LayoutSnapshot>);
        MethodInfo worker = control.GetMethod(
            "HighlightCodeBlockAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.Contains(worker.GetParameters(), parameter =>
            parameter.ParameterType == snapshotIdentityType);
        Assert.DoesNotContain(worker.GetParameters(), parameter =>
            IsForbiddenStrongCapture(parameter.ParameterType));

        Type stateMachine = worker.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        FieldInfo[] stateFields = stateMachine.GetFields(InstanceFields);
        Assert.Contains(stateFields, field => field.FieldType == snapshotIdentityType);
        Assert.True(AssertGeneratedCaptureGraphIsSafe(stateMachine, control, snapshotIdentityType));

        Type[] scheduleClosures = GetNestedTypesRecursively(control).Where(type =>
            type.GetMethods(DeclaredMethods).Any(method =>
                method.Name.StartsWith("<ScheduleVisibleCodeBlockHighlighting>", StringComparison.Ordinal)))
            .ToArray();
        Assert.NotEmpty(scheduleClosures);
        // Roslyn may put the weak identity on a parent display class while the
        // per-iteration Task.Run/start closures point to that parent. Inspect
        // every generated scheduler closure graph, not just one compiler class.
        bool schedulerCapturesWeakIdentity = false;
        foreach (Type scheduleClosure in scheduleClosures)
        {
            schedulerCapturesWeakIdentity |= AssertGeneratedCaptureGraphIsSafe(
                scheduleClosure, control, snapshotIdentityType);
        }
        Assert.True(schedulerCapturesWeakIdentity);

        // Depending on compiler lowering, the dispatcher lambda can be stored
        // in a generated display class or directly in the async state machine.
        // Check every emitted closure on the worker path and the state-machine
        // fields above, so either representation remains safe.
        Type[] publicationClosures = GetNestedTypesRecursively(control)
            .Where(type => type.GetMethods(DeclaredMethods).Any(method =>
                method.Name.StartsWith("<HighlightCodeBlockAsync>", StringComparison.Ordinal)))
            .ToArray();
        bool publicationClosureCarriesWeakIdentity = false;
        foreach (Type closure in publicationClosures)
        {
            publicationClosureCarriesWeakIdentity |=
                AssertGeneratedCaptureGraphIsSafe(closure, control, snapshotIdentityType);
        }
        Assert.True(publicationClosureCarriesWeakIdentity);
    }

    private static bool AssertGeneratedCaptureGraphIsSafe(
        Type root,
        Type control,
        Type snapshotIdentityType)
    {
        var pending = new Stack<Type>();
        var visited = new HashSet<Type>();
        pending.Push(root);
        bool foundWeakSnapshotIdentity = false;

        while (pending.Count > 0)
        {
            Type current = pending.Pop();
            if (!visited.Add(current))
                continue;

            foreach (FieldInfo field in current.GetFields(InstanceFields))
            {
                Type fieldType = field.FieldType;
                if (fieldType == snapshotIdentityType)
                {
                    foundWeakSnapshotIdentity = true;
                    continue;
                }

                Assert.False(
                    IsForbiddenStrongCapture(fieldType),
                    $"Generated capture path {current.Name}.{field.Name} strongly retains {fieldType}.");

                // The control is intentionally captured by its own callbacks;
                // it clears/replaces _snapshot before retirement. Do not walk
                // through the control's live owner fields. Other compiler-made
                // closure parents must be followed to inspect the complete
                // captured-local graph.
                if (fieldType != control &&
                    fieldType.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                {
                    pending.Push(fieldType);
                }
            }
        }

        return foundWeakSnapshotIdentity;
    }

    private static bool IsForbiddenStrongCapture(Type fieldType)
    {
        if (fieldType.IsArray)
            return IsForbiddenStrongCapture(fieldType.GetElementType()!);

        if (fieldType.IsGenericType)
        {
            if (fieldType.GetGenericTypeDefinition() == typeof(WeakReference<>))
                return false;

            return fieldType.GetGenericArguments().Any(IsForbiddenStrongCapture);
        }

        return typeof(LayoutSnapshot).IsAssignableFrom(fieldType) ||
            typeof(BlockBox).IsAssignableFrom(fieldType);
    }

    private static IEnumerable<Type> GetNestedTypesRecursively(Type owner)
    {
        foreach (Type nested in owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
        {
            yield return nested;
            foreach (Type descendant in GetNestedTypesRecursively(nested))
                yield return descendant;
        }
    }
}
