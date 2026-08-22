using System;
using System.Linq;
using System.Threading;
using JitHub.Services.CodeViewer;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class CsvDocumentParserTests
{
    [Fact]
    public void Parse_BasicDocument_PreservesHeadersAndRows()
    {
        CsvParseResult result = CsvDocumentParser.Parse("name,count\r\nalpha,1\r\nbeta,2", ',');

        Assert.True(result.Succeeded);
        Assert.Equal(["name", "count"], result.Document!.Headers);
        Assert.Equal(2, result.Document.Rows.Count);
        Assert.Equal(["alpha", "1"], result.Document.Rows[0].Values);
        Assert.Equal(["beta", "2"], result.Document.Rows[1].Values);
    }

    [Fact]
    public void Parse_QuotedFields_SupportsDelimitersNewlinesAndEscapedQuotes()
    {
        CsvParseResult result = CsvDocumentParser.Parse(
            "name,description\n\"alpha,beta\",\"line one\r\nline \"\"two\"\"\"",
            ',');

        Assert.True(result.Succeeded);
        Assert.Equal("alpha,beta", result.Document!.Rows[0].Values[0]);
        Assert.Equal("line one\nline \"two\"", result.Document.Rows[0].Values[1]);
    }

    [Fact]
    public void Parse_TsvBomAndMixedLineEndings_PreservesTrailingEmptyFields()
    {
        CsvParseResult result = CsvDocumentParser.Parse(
            "\uFEFFname\tvalue\textra\rfirst\t1\t\nsecond\t2\t",
            '\t');

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Document!.Headers.Count);
        Assert.Equal(["first", "1", ""], result.Document.Rows[0].Values);
        Assert.Equal(["second", "2", ""], result.Document.Rows[1].Values);
    }

    [Fact]
    public void Parse_RaggedRows_UsesWidestRecordAndPadsMissingValues()
    {
        CsvParseResult result = CsvDocumentParser.Parse("name\na,b,c\nz", ',');

        Assert.True(result.Succeeded);
        Assert.Equal(["name", "Column 2", "Column 3"], result.Document!.Headers);
        Assert.Equal(["a", "b", "c"], result.Document.Rows[0].Values);
        Assert.Equal(["z", "", ""], result.Document.Rows[1].Values);
    }

    [Fact]
    public void Parse_BlankHeaders_ProvidesAccessibleColumnNames()
    {
        CsvParseResult result = CsvDocumentParser.Parse(",value,\n1,2,3", ',');

        Assert.True(result.Succeeded);
        Assert.Equal(["Column 1", "value", "Column 3"], result.Document!.Headers);
    }

    [Theory]
    [InlineData("name\n\"unfinished", (int)CsvParseFailure.UnterminatedQuotedField)]
    [InlineData("name\na\"b", (int)CsvParseFailure.InvalidQuote)]
    [InlineData("name\n\"a\"suffix", (int)CsvParseFailure.InvalidQuote)]
    public void Parse_MalformedInput_IsRejected(string text, int expectedValue)
    {
        CsvParseResult result = CsvDocumentParser.Parse(text, ',');

        Assert.False(result.Succeeded);
        Assert.Equal((CsvParseFailure)expectedValue, result.Failure);
    }

    [Fact]
    public void Parse_TrailingNewline_DoesNotCreatePhantomRow()
    {
        CsvParseResult result = CsvDocumentParser.Parse("name,value\na,1\n", ',');

        Assert.True(result.Succeeded);
        Assert.Single(result.Document!.Rows);
    }

    [Fact]
    public void Parse_ColumnLimit_IsEnforcedWithoutPartialOutput()
    {
        string text = string.Join(',', Enumerable.Repeat("h", CsvDocumentParser.MaximumColumns + 1));

        CsvParseResult result = CsvDocumentParser.Parse(text, ',');

        Assert.False(result.Succeeded);
        Assert.Equal(CsvParseFailure.TooManyColumns, result.Failure);
        Assert.Null(result.Document);
    }

    [Fact]
    public void Parse_RowLimit_IsEnforcedWithoutPartialOutput()
    {
        string text = "header\n" + string.Concat(Enumerable.Repeat("x\n", CsvDocumentParser.MaximumDataRows + 1));

        CsvParseResult result = CsvDocumentParser.Parse(text, ',');

        Assert.False(result.Succeeded);
        Assert.Equal(CsvParseFailure.TooManyRows, result.Failure);
        Assert.Null(result.Document);
    }

    [Fact]
    public void Parse_InputLimit_IsEnforced()
    {
        string text = new('x', CsvDocumentParser.MaximumInputCharacters + 1);

        CsvParseResult result = CsvDocumentParser.Parse(text, ',');

        Assert.False(result.Succeeded);
        Assert.Equal(CsvParseFailure.InputTooLarge, result.Failure);
    }

    [Fact]
    public void Parse_Cancellation_IsObserved()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            CsvDocumentParser.Parse("header\nvalue", ',', cancellation.Token));
    }
}
