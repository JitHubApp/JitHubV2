using System.Reflection;
using System.Runtime.CompilerServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Diagnostic for a failed source-collection assertion: a bounded breadth-first
/// walk from the test's roots through async ownership objects that names the
/// first field path still holding one of the original source arrays.
/// </summary>
/// <remarks>
/// It runs only after the collectibility oracle has already failed, so it adds
/// detail to the failure message and never decides the test outcome.
/// </remarks>
internal sealed class StrongSourceRetentionScanner
{
    private const int MaxDepth = 32;
    private const int MaxNodes = 2_048;

    private readonly List<(int Index, byte[] Target)> _liveTargets;
    private readonly PriorityQueue<(object Value, int Depth, string Path), int> _pending = new();
    private readonly HashSet<object> _visited = new(ReferenceEqualityComparer.Instance);
    private int _visitedCount;

    private StrongSourceRetentionScanner(List<(int Index, byte[] Target)> liveTargets) =>
        _liveTargets = liveTargets;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static string Describe(
        Task renderReleaseTask,
        object renderer,
        object image,
        object resolver,
        IReadOnlyList<WeakReference<byte[]>> sourceIdentities)
    {
        var liveTargets = new List<(int Index, byte[] Target)>();
        for (int index = 0; index < sourceIdentities.Count; index++)
        {
            if (sourceIdentities[index].TryGetTarget(out byte[]? target) && target is not null)
                liveTargets.Add((index, target));
        }
        if (liveTargets.Count == 0)
            return "all targets collected before bounded scan";

        var scanner = new StrongSourceRetentionScanner(liveTargets);
        scanner._pending.Enqueue((renderReleaseTask, 0, "$release-task:" + renderReleaseTask.GetType().Name), 0);
        scanner._pending.Enqueue((renderer, 0, "$renderer:" + renderer.GetType().Name), 0);
        scanner._pending.Enqueue((image, 0, "$image:" + image.GetType().Name), 0);
        scanner._pending.Enqueue((resolver, 0, "$resolver:" + resolver.GetType().Name), -1);
        return scanner.Scan();
    }

    private string Scan()
    {
        while (_pending.Count > 0 && _visitedCount < MaxNodes)
        {
            (object value, int depth, string path) = _pending.Dequeue();
            if (TryDescribeTarget(value, path, out string? directPath))
                return directPath;

            if (depth >= MaxDepth || IsOpaqueLeaf(value) || !IsAsyncOwnershipNode(value))
                continue;

            if (!value.GetType().IsValueType && !_visited.Add(value))
                continue;
            _visitedCount++;

            string? foundPath = value is Array array
                ? ScanArray(array, depth, path)
                : ScanFields(value, depth, path);
            if (foundPath is not null)
                return foundPath;
        }

        KeepTargetsAlive();
        return $"no direct path found from provided roots (visited {_visitedCount} objects; " +
            $"live targets {_liveTargets.Count}; depth limit {MaxDepth}; node limit {MaxNodes})";
    }

    private string? ScanArray(Array array, int depth, string path)
    {
        int take = System.Math.Min(array.Length,
            System.Math.Max(0, MaxNodes - _visitedCount - _pending.Count));
        for (int index = 0; index < take; index++)
        {
            object? child;
            try { child = array.GetValue(index); }
            catch { continue; }
            if (child is null)
                continue;
            string childPath = $"{path}[{index}]:{child.GetType().Name}";
            if (TryDescribeTarget(child, childPath, out string? foundPath))
                return foundPath;
            if (_visitedCount + _pending.Count < MaxNodes)
                _pending.Enqueue((child, depth + 1, childPath), GetNodePriority(child, $"[{index}]"));
        }
        return null;
    }

    private string? ScanFields(object value, int depth, string path)
    {
        for (Type? type = value.GetType(); type is not null &&
             _visitedCount + _pending.Count < MaxNodes; type = type.BaseType)
        {
            FieldInfo[] fields;
            try
            {
                fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            }
            catch
            {
                continue;
            }

            foreach (FieldInfo field in fields.OrderBy(static field => GetFieldPriority(field.Name)))
            {
                object? child;
                try { child = field.GetValue(value); }
                catch { continue; }
                if (child is null)
                    continue;

                string childPath = $"{path}.{type.Name}.{field.Name}:{child.GetType().Name}";
                if (TryDescribeTarget(child, childPath, out string? foundPath))
                    return foundPath;
                if (IsOpaqueLeaf(child))
                    continue;
                if (_visitedCount + _pending.Count < MaxNodes)
                    _pending.Enqueue((child, depth + 1, childPath), GetNodePriority(child, field.Name));
            }
        }
        return null;
    }

    private bool TryDescribeTarget(object value, string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? foundPath)
    {
        int targetIndex = _liveTargets.FindIndex(item => ReferenceEquals(value, item.Target));
        if (targetIndex < 0)
        {
            foundPath = null;
            return false;
        }

        foundPath = $"source[{_liveTargets[targetIndex].Index}] via {path}";
        KeepTargetsAlive();
        return true;
    }

    private void KeepTargetsAlive()
    {
        foreach (var item in _liveTargets)
            GC.KeepAlive(item.Target);
    }

    private static bool IsOpaqueLeaf(object value) =>
        value is string || value is Type || value is MemberInfo ||
        value is Assembly || value is Module || value is byte[];

    private static bool IsAsyncOwnershipNode(object value)
    {
        Type type = value.GetType();
        if (value is Task or Delegate or Array || type.IsValueType ||
            type == typeof(MarkdownImageResolution) || type == typeof(MarkdownImageAsset))
        {
            return true;
        }

        string? typeName = type.FullName;
        return value is not MarkdownLayoutContext &&
               typeName?.StartsWith("Microsoft.Graphics.Canvas.", StringComparison.Ordinal) != true &&
               typeName?.StartsWith("Microsoft.UI.", StringComparison.Ordinal) != true &&
               typeName?.StartsWith("Windows.", StringComparison.Ordinal) != true;
    }

    private static int GetNodePriority(object value, string fieldName)
    {
        int fieldPriority = GetFieldPriority(fieldName);
        if (value is Delegate || value.GetType().IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            return 0;
        if (value is Task or Delegate || value.GetType().IsValueType ||
            value.GetType().IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
        {
            return fieldPriority;
        }

        return fieldPriority + 1;
    }

    private static int GetFieldPriority(string fieldName)
    {
        if (fieldName.Contains("result", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("asset", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("resolution", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("source", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("rawBytes", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (fieldName.Contains("continuation", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("action", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("awaiter", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("stateMachine", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("stateObject", StringComparison.OrdinalIgnoreCase) ||
            fieldName.Contains("u__", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 4;
    }
}
