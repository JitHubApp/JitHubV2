using MarkdownRenderer;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Math;
using MarkdownRenderer.Mermaid;

internal static class AuditSelfTests
{
    internal static async Task<int> RunAsync()
    {
        var run = new Program.AuditRunResult();
        for (int index = 0; index < 500; index++)
            Program.AccumulateCase(run, new Program.AuditCaseResult
            {
                Status = "parsed-clean", DocumentSourceMatchesInput = true,
            });
        Require(run.AttemptedParseCount == 500 && run.SuccessfulParseCount == 500, "Successful parse counts");
        Require(Program.DetermineExecutionStatus(run) == "completed-clean", "Clean baseline");
        run = new Program.AuditRunResult();
        for (int index = 0; index < 499; index++)
            Program.AccumulateCase(run, new Program.AuditCaseResult
            {
                Status = "parsed-clean", DocumentSourceMatchesInput = true,
            });
        Program.AccumulateCase(run, new Program.AuditCaseResult { Status = "parse-exception" });
        Require(run.AttemptedParseCount == 500 && run.SuccessfulParseCount == 499 && run.CaseFailureCount == 1,
            "An attempted parse that throws is not a successful parse");
        Require(Program.DetermineExecutionStatus(run) == "completed-with-exceptions-or-invariant-failures",
            "Parse exceptions cannot pass");
        run.SuccessfulParseCount = 500;
        run.CaseFailureCount = 0;
        Program.AccumulateCase(run, new Program.AuditCaseResult
        {
            Status = "parsed-with-diagnostics-or-fallbacks", DocumentSourceMatchesInput = true,
            MermaidDiagramCount = 1, MermaidUnclassifiedCount = 1,
        });
        Require(run.MermaidUnclassifiedCount == 1, "Unclassified diagram aggregation");
        run.AttemptedParseCount = run.SuccessfulParseCount = 500;
        Require(Program.DetermineExecutionStatus(run) == "completed-with-diagnostics-or-fallbacks",
            "An unclassified diagram cannot pass");

        foreach (string severity in new[] { "Information", "UnrecognizedFutureSeverity" })
        {
            run = new Program.AuditRunResult { AttemptedParseCount = 499, SuccessfulParseCount = 499 };
            Program.AccumulateCase(run, new Program.AuditCaseResult
            {
                Status = "parsed-with-diagnostics-or-fallbacks", DocumentSourceMatchesInput = true,
                Diagnostics = [new Program.DiagnosticResult { Code = "MMR-INFO", Severity = severity }],
            });
            Require(run.DiagnosticCount == 1 && run.ErrorDiagnosticCount == 0 && run.WarningDiagnosticCount == 0,
                "All diagnostic severities count even without errors or warnings");
            Require(run.InformationDiagnosticCount == (severity == "Information" ? 1 : 0),
                "Information diagnostic accounting");
            Require(Program.DetermineExecutionStatus(run) == "completed-with-diagnostics-or-fallbacks",
                "A diagnostic-bearing case cannot produce a clean run");
        }

        string directory = Path.Combine(Path.GetTempPath(), "readme-audit-self-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "bounded-input.bin");
        try
        {
            await File.WriteAllBytesAsync(file, new byte[4097]);
            await RequireInvalidReadAsync(file, maximum: 4096, expected: null);
            await File.WriteAllBytesAsync(file, new byte[16]);
            await RequireInvalidReadAsync(file, maximum: 4096, expected: 15);
            byte[] accepted = await Program.ReadBoundedBytesAsync(file, 4096, 16);
            Require(accepted.Length == 16, "Exact bounded read");
            await VerifyCommonSubsetProjectionFixturesAsync().ConfigureAwait(false);
        }
        finally
        {
            File.Delete(file);
            Directory.Delete(directory); // Only this run's empty, non-recursive fixture directory.
        }
        Console.WriteLine("PASS: parse outcome accounting, every diagnostic severity, unclassified diagrams, and bounded file-read self-tests.");
        return 0;
    }

    private static async Task VerifyCommonSubsetProjectionFixturesAsync()
    {
        using var engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .UseMathematics()
            .UseMermaid()
            .Build();

        string firstSource = Program.CommonSubsetFixtureHeadingAndCode;
        MarkdownDocument first = await engine.ParseAsync(firstSource).ConfigureAwait(false);
        Require(first.Source == firstSource, "The common-subset fixture preserves source text.");
        MarkdownHeading heading = first.GetHeadings().Single();
        int expectedHeadingStart = firstSource.IndexOf("# Plain heading", StringComparison.Ordinal);
        Require(heading.Level == 1 && heading.DisplayText == "Plain heading",
            "Plain ATX heading projection values.");
        Require(heading.SourceSpan.Start == expectedHeadingStart &&
            firstSource.Substring(heading.SourceSpan.Start, heading.SourceSpan.Length) == "# Plain heading",
            "ATX heading source span uses UTF-16 source offsets after a supplementary character.");
        Program.HeadingProjection headingProjection = Program.CreateHeadingProjection(heading);
        Require(headingProjection.SourceStart == expectedHeadingStart &&
            headingProjection.SourceLength == heading.SourceSpan.Length &&
            headingProjection.DisplayTextUtf16Length == heading.DisplayText.Length &&
            headingProjection.DisplayTextSha256.Length == 64,
            "Structured heading projection is populated from the public document API.");

        MarkdownCodeBlock code = first.GetCodeBlocks().Single();
        string codeSource = firstSource.Substring(code.SourceSpan.Start, code.SourceSpan.Length);
        Require(code.Language == "csharp" &&
            codeSource.StartsWith("\u0060\u0060\u0060csharp\n", StringComparison.Ordinal) &&
            codeSource.Contains("var answer = 42;", StringComparison.Ordinal) &&
            codeSource.EndsWith("\u0060\u0060\u0060", StringComparison.Ordinal) &&
            code.SourceSpan.Start + code.SourceSpan.Length < firstSource.Length &&
            firstSource[code.SourceSpan.Start + code.SourceSpan.Length] == '\n',
            "Simple fenced code source span ends at the closing fence before its trailing line terminator.");
        Program.CodeBlockProjection codeProjection = Program.CreateCodeBlockProjection(code);
        Require(codeProjection.SourceStart == code.SourceSpan.Start &&
            codeProjection.SourceLength == code.SourceSpan.Length &&
            codeProjection.Language == "csharp" &&
            codeProjection.DisplayTextSha256.Length == 64,
            "Structured code projection is populated from the public document API.");

        string codeAtEofSource = Program.CommonSubsetFixtureCodeAtEof;
        MarkdownDocument codeAtEof = await engine.ParseAsync(codeAtEofSource).ConfigureAwait(false);
        MarkdownCodeBlock eofBlock = codeAtEof.GetCodeBlocks().Single();
        Require(eofBlock.SourceSpan.Start == 0 && eofBlock.SourceSpan.Length == codeAtEofSource.Length &&
            codeAtEofSource.Substring(eofBlock.SourceSpan.Start, eofBlock.SourceSpan.Length) == codeAtEofSource,
            "A fenced code block at EOF without a line terminator spans exactly its source bytes.");

        string secondSource = Program.CommonSubsetFixtureLinkAndImage;
        MarkdownDocument second = await engine.ParseAsync(secondSource).ConfigureAwait(false);
        MarkdownLink link = second.GetLinks().Single();
        int expectedLinkStart = secondSource.IndexOf("[plain](https://example.test/path)", StringComparison.Ordinal);
        Require(link.DisplayText == "plain" && link.Url == "https://example.test/path" &&
            link.SourceSpan.Start == expectedLinkStart &&
            secondSource.Substring(link.SourceSpan.Start, link.SourceSpan.Length) ==
                "[plain](https://example.test/path)",
            "Simple direct-link projection values and source span.");
        Program.LinkProjection linkProjection = Program.CreateLinkProjection(link);
        Require(linkProjection.SourceStart == expectedLinkStart && linkProjection.TitleSha256 is null &&
            linkProjection.TitleUtf16Length is null && linkProjection.UrlUtf16Length == link.Url.Length,
            "Structured link projection preserves optional-title absence.");

        MarkdownImage image = second.GetImages().Single();
        int expectedImageStart = secondSource.IndexOf("![diagram](images/diagram.png)", StringComparison.Ordinal);
        Require(image.AltText == "diagram" && image.Source == "images/diagram.png" &&
            image.SourceSpan.Start == expectedImageStart &&
            secondSource.Substring(image.SourceSpan.Start, image.SourceSpan.Length) ==
                "![diagram](images/diagram.png)",
            "Simple direct-image projection values and source span.");
        Program.ImageProjection imageProjection = Program.CreateImageProjection(image);
        Require(imageProjection.SourceStart == expectedImageStart && imageProjection.IsInline &&
            imageProjection.AltTextUtf16Length == image.AltText.Length,
            "Structured image projection preserves inline/alt metadata.");
    }

    private static async Task RequireInvalidReadAsync(string path, int maximum, int? expected)
    {
        try { await Program.ReadBoundedBytesAsync(path, maximum, expected); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("A size-invalid input was accepted.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
