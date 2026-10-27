using System.Reflection;
using MarkdownRenderer.Controls;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Exercises the private storage contract without constructing WinUI controls
/// or claiming hosted Tab/Shift+Tab UI qualification.
/// </summary>
public sealed class HostedFocusableBufferTests
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void IndexerRejectsNegativeCountAndSpareCapacitySlots()
    {
        object buffer = CreateBuffer();

        Assert.Equal(0, GetCount(buffer));
        AssertIndexerThrowsOutOfRange(buffer, -1);
        AssertIndexerThrowsOutOfRange(buffer, 0);
        AssertIndexerThrowsOutOfRange(buffer, 3);

        AddNullControl(buffer);
        Assert.Equal(1, GetCount(buffer));
        Assert.Null(GetAt(buffer, 0));

        // The first allocation has four slots; only the one below Count is valid.
        AssertIndexerThrowsOutOfRange(buffer, -1);
        AssertIndexerThrowsOutOfRange(buffer, 1);
        AssertIndexerThrowsOutOfRange(buffer, 3);
    }

    [Fact]
    public void GrowthPreservesCountIndexerAndIndexOfSemantics()
    {
        object buffer = CreateBuffer();
        Assert.Equal(-1, FindNullControl(buffer));

        // Cross the 4- and 8-element capacity boundaries without requiring a
        // native Control instance; these tests concern the backing storage only.
        for (int index = 0; index < 9; index++)
            AddNullControl(buffer);

        Assert.Equal(9, GetCount(buffer));
        Assert.Equal(0, FindNullControl(buffer));
        foreach (int index in new[] { 0, 3, 4, 7, 8 })
            Assert.Null(GetAt(buffer, index));

        // Capacity is 16 after the ninth item, but index 9 is not a member.
        AssertIndexerThrowsOutOfRange(buffer, 9);
    }

    private static Type GetBufferType()
        => typeof(MarkdownDocumentView).Assembly.GetType(
            "MarkdownRenderer.Controls.MarkdownRendererControl+HostedFocusableControlBuffer",
            throwOnError: true)!;

    private static object CreateBuffer()
        => Activator.CreateInstance(GetBufferType(), nonPublic: true)
            ?? throw new InvalidOperationException("Could not construct the private hosted-focus buffer.");

    private static int GetCount(object buffer)
        => (int)(GetBufferType().GetProperty("Count", InstanceMembers)?.GetValue(buffer)
            ?? throw new InvalidOperationException("The hosted-focus buffer Count property was not found."));

    private static object? GetAt(object buffer, int index)
    {
        PropertyInfo indexer = GetBufferType().GetProperty("Item", InstanceMembers)
            ?? throw new InvalidOperationException("The hosted-focus buffer indexer was not found.");
        return indexer.GetValue(buffer, new object?[] { index });
    }

    private static void AddNullControl(object buffer)
        => Invoke(buffer, "Add", new object?[] { null });

    private static int FindNullControl(object buffer)
        => (int)(Invoke(buffer, "IndexOf", new object?[] { null })
            ?? throw new InvalidOperationException("The hosted-focus buffer IndexOf returned no value."));

    private static object? Invoke(object target, string methodName, object?[] arguments)
    {
        MethodInfo method = GetBufferType().GetMethod(methodName, InstanceMembers)
            ?? throw new InvalidOperationException($"The hosted-focus buffer {methodName} method was not found.");
        return method.Invoke(target, arguments);
    }

    private static void AssertIndexerThrowsOutOfRange(object buffer, int index)
    {
        PropertyInfo indexer = GetBufferType().GetProperty("Item", InstanceMembers)
            ?? throw new InvalidOperationException("The hosted-focus buffer indexer was not found.");
        TargetInvocationException invocation = Assert.Throws<TargetInvocationException>(
            () => indexer.GetValue(buffer, new object?[] { index }));
        Assert.IsType<ArgumentOutOfRangeException>(invocation.InnerException);
    }
}
