namespace MarkdownRenderer.Sample.Automation.Uia;

/// <summary>
/// Interprets the boxed values that UIA text attributes return.
/// </summary>
internal static class TextAttributeValues
{
    internal static bool IsNonNoneTextDecoration(object? value)
    {
        if (value is null) return false;
        string s = value.ToString() ?? string.Empty;
        if (string.Equals(s, "None", StringComparison.OrdinalIgnoreCase)) return false;
        return !TryAttributeNumber(value, out var number) || Math.Abs(number) > 0.001;
    }

    internal static bool IsTrueAttribute(object? value) =>
        value is bool b
            ? b
            : bool.TryParse(value?.ToString(), out var parsed) && parsed;

    internal static int ToColorRefValue(object? value)
    {
        if (value is null)
            throw new InvalidOperationException("Color attribute returned null");

        if (value is int i) return i;
        if (value is uint u) return unchecked((int)u);
        if (TryAttributeNumber(value, out var number)) return (int)Math.Round(number);

        throw new InvalidOperationException($"Cannot interpret color attribute value {DescribeAttributeValue(value)}");
    }

    internal static int ColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    internal static bool TryAttributeNumber(object value, out double number)
    {
        try
        {
            var type = Nullable.GetUnderlyingType(value.GetType()) ?? value.GetType();
            if (type.IsEnum)
            {
                number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                    return true;
            }
        }
        catch { }

        number = 0;
        return false;
    }

    internal static string DescribeAttributeValue(object? value) =>
        value is null ? "<null>" : $"{value} ({value.GetType().FullName})";
}
