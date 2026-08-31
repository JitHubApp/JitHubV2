using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Xml;

namespace MarkdownRenderer.Layout.Boxes;

internal readonly record struct SvgResourceBudgetResult(bool Accepted, string? Reason)
{
    public static SvgResourceBudgetResult Success => new(true, null);

    public static SvgResourceBudgetResult Reject(string reason) => new(false, reason);
}

/// <summary>Rejects SVG input that is too expensive to parse, inspect, or rasterize safely.</summary>
internal static class SvgResourceBudget
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
    public static readonly TimeSpan ValidationDeadline = TimeSpan.FromMilliseconds(750);

    public static SvgResourceBudgetResult Validate(byte[]? bytes, CancellationToken cancellationToken)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return SvgResourceBudgetResult.Reject("empty");
        }

        if (bytes.Length > MaxInputBytes)
        {
            return SvgResourceBudgetResult.Reject("input-bytes");
        }

        long started = Stopwatch.GetTimestamp();
        int elementCount = 0;
        int attributeCount = 0;
        int textNodes = 0;
        int textCharacters = 0;
        int pathCharacters = 0;
        int transformCharacters = 0;
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
                    return SvgResourceBudgetResult.Reject("validation-deadline");
                }

                if (reader.Depth > MaxDepth)
                {
                    return SvgResourceBudgetResult.Reject("element-depth");
                }

                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (elementCount == 0)
                    {
                        sawSvgRoot = reader.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase);
                    }
                    if (++elementCount > MaxElements)
                    {
                        return SvgResourceBudgetResult.Reject("element-count");
                    }

                    if (reader.HasAttributes)
                    {
                        while (reader.MoveToNextAttribute())
                        {
                            if (++attributeCount > MaxAttributes)
                            {
                                return SvgResourceBudgetResult.Reject("attribute-count");
                            }

                            string name = reader.LocalName;
                            string value = reader.Value;
                            if (name.Equals("d", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("points", StringComparison.OrdinalIgnoreCase))
                            {
                                pathCharacters += value.Length;
                                if (pathCharacters > MaxPathCharacters)
                                {
                                    return SvgResourceBudgetResult.Reject("path-complexity");
                                }
                            }
                            else if (name.Equals("transform", StringComparison.OrdinalIgnoreCase))
                            {
                                transformCharacters += value.Length;
                                if (transformCharacters > MaxTransformCharacters)
                                {
                                    return SvgResourceBudgetResult.Reject("transform-complexity");
                                }
                            }
                            else if (name.Equals("font-size", StringComparison.OrdinalIgnoreCase) &&
                                TryReadLeadingNumber(value, out double fontSize) &&
                                Math.Abs(fontSize) > MaxDeclaredFontSize)
                            {
                                return SvgResourceBudgetResult.Reject("font-size");
                            }
                        }

                        reader.MoveToElement();
                    }
                }
                else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
                {
                    if (++textNodes > MaxTextNodes)
                    {
                        return SvgResourceBudgetResult.Reject("text-node-count");
                    }

                    textCharacters += reader.Value.Length;
                    if (textCharacters > MaxTextCharacters)
                    {
                        return SvgResourceBudgetResult.Reject("text-length");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (XmlException)
        {
            return SvgResourceBudgetResult.Reject("invalid-xml");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return SvgResourceBudgetResult.Reject("parse-failure");
        }

        return !sawSvgRoot
            ? SvgResourceBudgetResult.Reject("missing-root")
            : SvgResourceBudgetResult.Success;
    }

    private static bool TryReadLeadingNumber(string? value, out double result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        ReadOnlySpan<char> span = value.AsSpan().Trim();
        int length = 0;
        while (length < span.Length &&
            (char.IsDigit(span[length]) || span[length] is '.' or '-' or '+' or 'e' or 'E'))
        {
            length++;
        }

        return length > 0 && double.TryParse(
            span[..length],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out result);
    }
}
