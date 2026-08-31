using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services.CodeViewer;

internal enum CsvParseFailure
{
    None,
    Canceled,
    Empty,
    InputTooLarge,
    TooManyColumns,
    TooManyRows,
    UnterminatedQuotedField,
    InvalidQuote,
}

internal sealed class CsvDocument
{
    public CsvDocument(IReadOnlyList<string> headers, IReadOnlyList<CsvRow> rows)
    {
        Headers = headers;
        Rows = rows;
    }

    public IReadOnlyList<string> Headers { get; }

    public IReadOnlyList<CsvRow> Rows { get; }
}

internal sealed class CsvRow
{
    public CsvRow(int sourceIndex, IReadOnlyList<string> values)
    {
        SourceIndex = sourceIndex;
        Values = values;
    }

    public int SourceIndex { get; }

    public IReadOnlyList<string> Values { get; }
}

internal readonly record struct CsvParseResult(CsvDocument? Document, CsvParseFailure Failure)
{
    public bool Succeeded => Document is not null && Failure == CsvParseFailure.None;

    public bool WasCanceled => Failure == CsvParseFailure.Canceled;

    public static CsvParseResult Success(CsvDocument document) => new(document, CsvParseFailure.None);

    public static CsvParseResult Canceled() => new(null, CsvParseFailure.Canceled);

    public static CsvParseResult Rejected(CsvParseFailure failure) => new(null, failure);
}

internal static class CsvDocumentParser
{
    public const int MaximumInputCharacters = 128 * 1024;
    public const int MaximumColumns = 256;
    public const int MaximumDataRows = 50_000;

    public static CsvParseResult Parse(
        string? text,
        char delimiter,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return CsvParseResult.Canceled();
        }

        if (string.IsNullOrEmpty(text))
        {
            return CsvParseResult.Rejected(CsvParseFailure.Empty);
        }

        if (text.Length > MaximumInputCharacters)
        {
            return CsvParseResult.Rejected(CsvParseFailure.InputTooLarge);
        }

        List<string[]> records = [];
        List<string> fields = [];
        StringBuilder field = new();
        bool inQuotedField = false;
        bool quotedFieldClosed = false;
        bool recordHasInput = false;

        for (int index = text[0] == '\uFEFF' ? 1 : 0; index < text.Length; index++)
        {
            if ((index & 0x3ff) == 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return CsvParseResult.Canceled();
                }
            }

            char current = text[index];
            if (inQuotedField)
            {
                if (current == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotedField = false;
                        quotedFieldClosed = true;
                    }
                }
                else if (current == '\r')
                {
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        index++;
                    }

                    field.Append('\n');
                }
                else
                {
                    field.Append(current);
                }

                recordHasInput = true;
                continue;
            }

            if (quotedFieldClosed)
            {
                if (current is ' ' or '\t')
                {
                    continue;
                }

                if (current == delimiter)
                {
                    CsvParseFailure failure = AddField(fields, field);
                    if (failure != CsvParseFailure.None)
                    {
                        return CsvParseResult.Rejected(failure);
                    }

                    quotedFieldClosed = false;
                    recordHasInput = true;
                    continue;
                }

                if (current is '\r' or '\n')
                {
                    CsvParseFailure failure = CompleteRecord(records, fields, field);
                    if (failure != CsvParseFailure.None)
                    {
                        return CsvParseResult.Rejected(failure);
                    }

                    if (current == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        index++;
                    }

                    quotedFieldClosed = false;
                    recordHasInput = false;
                    continue;
                }

                return CsvParseResult.Rejected(CsvParseFailure.InvalidQuote);
            }

            if (current == delimiter)
            {
                CsvParseFailure failure = AddField(fields, field);
                if (failure != CsvParseFailure.None)
                {
                    return CsvParseResult.Rejected(failure);
                }

                recordHasInput = true;
            }
            else if (current is '\r' or '\n')
            {
                CsvParseFailure failure = CompleteRecord(records, fields, field);
                if (failure != CsvParseFailure.None)
                {
                    return CsvParseResult.Rejected(failure);
                }

                if (current == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                recordHasInput = false;
            }
            else if (current == '"')
            {
                if (field.Length != 0)
                {
                    return CsvParseResult.Rejected(CsvParseFailure.InvalidQuote);
                }

                inQuotedField = true;
                recordHasInput = true;
            }
            else
            {
                field.Append(current);
                recordHasInput = true;
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return CsvParseResult.Canceled();
        }

        if (inQuotedField)
        {
            return CsvParseResult.Rejected(CsvParseFailure.UnterminatedQuotedField);
        }

        if (recordHasInput || quotedFieldClosed || fields.Count > 0 || field.Length > 0)
        {
            CsvParseFailure failure = CompleteRecord(records, fields, field);
            if (failure != CsvParseFailure.None)
            {
                return CsvParseResult.Rejected(failure);
            }
        }

        if (records.Count == 0)
        {
            return CsvParseResult.Rejected(CsvParseFailure.Empty);
        }

        int columnCount = 0;
        for (int recordIndex = 0; recordIndex < records.Count; recordIndex++)
        {
            if ((recordIndex & 0x3ff) == 0 && cancellationToken.IsCancellationRequested)
            {
                return CsvParseResult.Canceled();
            }

            columnCount = Math.Max(columnCount, records[recordIndex].Length);
        }

        if (columnCount == 0)
        {
            return CsvParseResult.Rejected(CsvParseFailure.Empty);
        }

        string[] headerRecord = records[0];
        string[] headers = new string[columnCount];
        for (int column = 0; column < headers.Length; column++)
        {
            string? candidate = column < headerRecord.Length ? headerRecord[column] : null;
            headers[column] = string.IsNullOrWhiteSpace(candidate)
                ? $"Column {column + 1}"
                : candidate;
        }

        List<CsvRow> rows = new(Math.Max(0, records.Count - 1));
        for (int recordIndex = 1; recordIndex < records.Count; recordIndex++)
        {
            if ((recordIndex & 0x3ff) == 0 && cancellationToken.IsCancellationRequested)
            {
                return CsvParseResult.Canceled();
            }

            string[] source = records[recordIndex];
            string[] values = new string[columnCount];
            Array.Copy(source, values, source.Length);
            Array.Fill(values, string.Empty, source.Length, values.Length - source.Length);
            rows.Add(new CsvRow(recordIndex - 1, values));
        }

        return CsvParseResult.Success(new CsvDocument(headers, rows));
    }

    public static Task<CsvParseResult> ParseAsync(
        string? text,
        char delimiter,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(CsvParseResult.Canceled());
        }

        // Preview refresh cancellation is represented by CsvParseResult so normal
        // view lifecycle changes never surface first-chance exceptions.
        return Task.Run(() => Parse(text, delimiter, cancellationToken));
    }

    private static CsvParseFailure AddField(List<string> fields, StringBuilder field)
    {
        if (fields.Count >= MaximumColumns)
        {
            return CsvParseFailure.TooManyColumns;
        }

        fields.Add(field.ToString());
        field.Clear();
        return CsvParseFailure.None;
    }

    private static CsvParseFailure CompleteRecord(
        List<string[]> records,
        List<string> fields,
        StringBuilder field)
    {
        CsvParseFailure failure = AddField(fields, field);
        if (failure != CsvParseFailure.None)
        {
            return failure;
        }

        if (records.Count > MaximumDataRows)
        {
            return CsvParseFailure.TooManyRows;
        }

        records.Add(fields.ToArray());
        fields.Clear();
        return CsvParseFailure.None;
    }
}
