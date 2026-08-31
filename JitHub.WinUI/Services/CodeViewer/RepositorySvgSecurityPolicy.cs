using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Xml;

namespace JitHub.Services.CodeViewer;

internal readonly record struct RepositorySvgValidationResult(bool Accepted, string? Reason)
{
    public static RepositorySvgValidationResult Success => new(true, null);

    public static RepositorySvgValidationResult Reject(string reason) => new(false, reason);
}

/// <summary>
/// Applies bounded, self-contained SVG rules before repository content reaches Svg.Skia.
/// </summary>
internal static class RepositorySvgSecurityPolicy
{
    public const int MaxInputBytes = 2 * 1024 * 1024;
    public const int MaxElements = 4096;
    public const int MaxDepth = 64;
    public const int MaxAttributes = 32768;
    public const int MaxTextNodes = 512;
    public const int MaxTextCharacters = 64 * 1024;
    public const int MaxPathCharacters = 512 * 1024;
    public const int MaxTransformCharacters = 64 * 1024;
    public const double MaxDeclaredFontSize = 4096;
    public const double MaxDeclaredDimension = 16384;
    public const double MaxDeclaredArea = 64 * 1024 * 1024;
    public static readonly TimeSpan ValidationDeadline = TimeSpan.FromMilliseconds(750);

    public static RepositorySvgValidationResult Validate(byte[]? bytes, CancellationToken cancellationToken)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return RepositorySvgValidationResult.Reject("empty");
        }

        if (bytes.Length > MaxInputBytes)
        {
            return RepositorySvgValidationResult.Reject("input-bytes");
        }

        long started = Stopwatch.GetTimestamp();
        int elementCount = 0;
        int attributeCount = 0;
        int textNodeCount = 0;
        int textCharacters = 0;
        int pathCharacters = 0;
        int transformCharacters = 0;
        int styleDepth = -1;
        bool sawSvgRoot = false;

        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                Async = false,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = MaxInputBytes,
            });

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(started) > ValidationDeadline)
                {
                    return RepositorySvgValidationResult.Reject("validation-deadline");
                }

                if (reader.Depth > MaxDepth)
                {
                    return RepositorySvgValidationResult.Reject("element-depth");
                }

                if (reader.NodeType == XmlNodeType.Element)
                {
                    string elementName = reader.LocalName;
                    if (elementCount == 0)
                    {
                        sawSvgRoot = elementName.Equals("svg", StringComparison.OrdinalIgnoreCase);
                    }

                    if (++elementCount > MaxElements)
                    {
                        return RepositorySvgValidationResult.Reject("element-count");
                    }

                    if (elementName.Equals("script", StringComparison.OrdinalIgnoreCase) ||
                        elementName.Equals("foreignObject", StringComparison.OrdinalIgnoreCase))
                    {
                        return RepositorySvgValidationResult.Reject("active-content");
                    }

                    if (elementName.Equals("style", StringComparison.OrdinalIgnoreCase) && !reader.IsEmptyElement)
                    {
                        styleDepth = reader.Depth;
                    }

                    if (reader.HasAttributes)
                    {
                        while (reader.MoveToNextAttribute())
                        {
                            if (++attributeCount > MaxAttributes)
                            {
                                return RepositorySvgValidationResult.Reject("attribute-count");
                            }

                            string name = reader.LocalName;
                            string value = reader.Value;

                            if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                            {
                                return RepositorySvgValidationResult.Reject("event-handler");
                            }

                            if (name.Equals("base", StringComparison.OrdinalIgnoreCase) &&
                                reader.NamespaceURI.Equals("http://www.w3.org/XML/1998/namespace", StringComparison.Ordinal))
                            {
                                return RepositorySvgValidationResult.Reject("external-resource");
                            }

                            if (name.Equals("href", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("src", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!IsSafeLocalReference(value))
                                {
                                    return RepositorySvgValidationResult.Reject("external-resource");
                                }
                            }

                            if (name.Equals("style", StringComparison.OrdinalIgnoreCase) && HasUnsafeCssReference(value))
                            {
                                return RepositorySvgValidationResult.Reject("external-resource");
                            }

                            if (name.Equals("d", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("points", StringComparison.OrdinalIgnoreCase))
                            {
                                pathCharacters += value.Length;
                                if (pathCharacters > MaxPathCharacters)
                                {
                                    return RepositorySvgValidationResult.Reject("path-complexity");
                                }
                            }
                            else if (name.Equals("transform", StringComparison.OrdinalIgnoreCase))
                            {
                                transformCharacters += value.Length;
                                if (transformCharacters > MaxTransformCharacters)
                                {
                                    return RepositorySvgValidationResult.Reject("transform-complexity");
                                }
                            }
                            else if (name.Equals("font-size", StringComparison.OrdinalIgnoreCase) &&
                                TryReadAbsoluteLength(value, out double fontSize) &&
                                Math.Abs(fontSize) > MaxDeclaredFontSize)
                            {
                                return RepositorySvgValidationResult.Reject("font-size");
                            }
                            else if ((name.Equals("width", StringComparison.OrdinalIgnoreCase) ||
                                      name.Equals("height", StringComparison.OrdinalIgnoreCase)) &&
                                TryReadAbsoluteLength(value, out double dimension) &&
                                (!double.IsFinite(dimension) || Math.Abs(dimension) > MaxDeclaredDimension))
                            {
                                return RepositorySvgValidationResult.Reject("declared-dimension");
                            }
                            else if (name.Equals("viewBox", StringComparison.OrdinalIgnoreCase) &&
                                !IsSafeViewBox(value))
                            {
                                return RepositorySvgValidationResult.Reject("declared-dimension");
                            }
                        }

                        reader.MoveToElement();
                    }
                }
                else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
                {
                    if (++textNodeCount > MaxTextNodes)
                    {
                        return RepositorySvgValidationResult.Reject("text-node-count");
                    }

                    textCharacters += reader.Value.Length;
                    if (textCharacters > MaxTextCharacters)
                    {
                        return RepositorySvgValidationResult.Reject("text-length");
                    }

                    if (styleDepth >= 0 && reader.Depth > styleDepth && HasUnsafeCssReference(reader.Value))
                    {
                        return RepositorySvgValidationResult.Reject("external-resource");
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement &&
                    styleDepth == reader.Depth &&
                    reader.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
                {
                    styleDepth = -1;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (XmlException)
        {
            return RepositorySvgValidationResult.Reject("invalid-xml");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
        {
            return RepositorySvgValidationResult.Reject("parse-failure");
        }

        return sawSvgRoot
            ? RepositorySvgValidationResult.Success
            : RepositorySvgValidationResult.Reject("missing-root");
    }

    public static bool ArePictureBoundsSafe(double width, double height)
    {
        width = Math.Abs(width);
        height = Math.Abs(height);
        return double.IsFinite(width) &&
            double.IsFinite(height) &&
            width > 0 &&
            height > 0 &&
            width <= MaxDeclaredDimension &&
            height <= MaxDeclaredDimension &&
            width * height <= MaxDeclaredArea;
    }

    private static bool IsSafeLocalReference(string value)
    {
        ReadOnlySpan<char> reference = value.AsSpan().Trim();
        return reference.IsEmpty || reference[0] == '#';
    }

    private static bool HasUnsafeCssReference(string value)
    {
        if (value.Contains("@import", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int searchFrom = 0;
        while (searchFrom < value.Length)
        {
            int urlStart = value.IndexOf("url(", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (urlStart < 0)
            {
                return false;
            }

            int valueStart = urlStart + 4;
            int valueEnd = value.IndexOf(')', valueStart);
            if (valueEnd < 0)
            {
                return true;
            }

            string reference = value[valueStart..valueEnd].Trim().Trim('\'', '"');
            if (!IsSafeLocalReference(reference))
            {
                return true;
            }

            searchFrom = valueEnd + 1;
        }

        return false;
    }

    private static bool IsSafeViewBox(string value)
    {
        string[] parts = value.Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double width) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double height))
        {
            return false;
        }

        return ArePictureBoundsSafe(width, height);
    }

    private static bool TryReadAbsoluteLength(string value, out double pixels)
    {
        pixels = 0;
        ReadOnlySpan<char> span = value.AsSpan().Trim();
        if (span.IsEmpty || span.EndsWith("%", StringComparison.Ordinal))
        {
            return false;
        }

        int numberLength = 0;
        while (numberLength < span.Length &&
            (char.IsDigit(span[numberLength]) || span[numberLength] is '.' or '-' or '+' or 'e' or 'E'))
        {
            numberLength++;
        }

        if (numberLength == 0 || !double.TryParse(
            span[..numberLength],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double valueNumber))
        {
            return false;
        }

        string unit = span[numberLength..].Trim().ToString().ToLowerInvariant();
        double scale = unit switch
        {
            "" or "px" => 1,
            "in" => 96,
            "cm" => 96 / 2.54,
            "mm" => 96 / 25.4,
            "q" => 96 / 101.6,
            "pt" => 96 / 72,
            "pc" => 16,
            _ => 1,
        };
        pixels = valueNumber * scale;
        return true;
    }
}
