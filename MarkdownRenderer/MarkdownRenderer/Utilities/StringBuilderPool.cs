using System.Text;

namespace MarkdownRenderer.Utilities;

internal static class StringBuilderPool
{
    private const int MaxRetainedCapacity = 8192;

    [System.ThreadStatic]
    private static StringBuilder? _cached;

    public static StringBuilder Rent()
    {
        var builder = _cached;
        if (builder is null)
            return new StringBuilder();

        _cached = null;
        builder.Clear();
        return builder;
    }

    public static string ToStringAndReturn(StringBuilder builder)
    {
        string value = builder.ToString();
        if (builder.Capacity <= MaxRetainedCapacity)
        {
            builder.Clear();
            _cached = builder;
        }

        return value;
    }
}
