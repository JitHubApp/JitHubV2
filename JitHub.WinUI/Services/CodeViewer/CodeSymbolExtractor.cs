using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using JitHub.Models.CodeViewer;

namespace JitHub.Services.CodeViewer;

public static partial class CodeSymbolExtractor
{
    private const int MaximumLineLength = 4096;
    private const int MaximumSymbols = 2000;

    public static IReadOnlyList<CodeSymbol> Extract(string? languageId, string? text)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(languageId)) return [];

        SymbolLanguage language = ResolveLanguage(languageId);
        if (language == SymbolLanguage.Unsupported) return [];

        List<CodeSymbol> result = [];
        ReadOnlySpan<char> remaining = text.AsSpan();
        int lineNumber = 1;
        while (!remaining.IsEmpty && result.Count < MaximumSymbols)
        {
            int newline = remaining.IndexOf('\n');
            ReadOnlySpan<char> line = newline < 0 ? remaining : remaining[..newline];
            if (line.Length > 0 && line[^1] == '\r') line = line[..^1];
            if (line.Length <= MaximumLineLength)
            {
                TryExtract(language, line.ToString(), lineNumber, result);
            }

            if (newline < 0) break;
            remaining = remaining[(newline + 1)..];
            lineNumber++;
        }

        return result;
    }

    public static bool Supports(string? languageId) =>
        ResolveLanguage(languageId) != SymbolLanguage.Unsupported;

    private static void TryExtract(
        SymbolLanguage language,
        string line,
        int lineNumber,
        ICollection<CodeSymbol> destination)
    {
        Match match = language switch
        {
            SymbolLanguage.CFamily => CFamilySymbolRegex().Match(line),
            SymbolLanguage.JavaScript => JavaScriptSymbolRegex().Match(line),
            SymbolLanguage.Python => PythonSymbolRegex().Match(line),
            SymbolLanguage.Go => GoSymbolRegex().Match(line),
            SymbolLanguage.Rust => RustSymbolRegex().Match(line),
            _ => Match.Empty
        };
        if (!match.Success) return;

        string kind = match.Groups["kind"].Value;
        string name = match.Groups["name"].Value;
        if (string.IsNullOrWhiteSpace(name)) return;

        destination.Add(new CodeSymbol(name, NormalizeKind(kind), lineNumber));
    }

    private static string NormalizeKind(string kind) => kind switch
    {
        "class" or "struct" or "interface" or "enum" or "record" or "trait" or "type" => "Type",
        "namespace" or "module" or "mod" => "Namespace",
        "def" or "func" or "fn" or "function" => "Function",
        "const" or "let" or "var" => "Value",
        _ => "Member"
    };

    private static SymbolLanguage ResolveLanguage(string? languageId) =>
        languageId?.Trim().ToLowerInvariant() switch
        {
            "csharp" or "c#" or "cpp" or "c" or "java" or "kotlin" => SymbolLanguage.CFamily,
            "javascript" or "typescript" or "javascriptreact" or "typescriptreact" => SymbolLanguage.JavaScript,
            "python" => SymbolLanguage.Python,
            "go" => SymbolLanguage.Go,
            "rust" => SymbolLanguage.Rust,
            _ => SymbolLanguage.Unsupported
        };

    [GeneratedRegex(@"^\s*(?:(?:public|private|protected|internal|static|sealed|abstract|partial|async|virtual|override|extern|unsafe|readonly|new)\s+)*(?:(?<kind>namespace|class|struct|interface|enum|record)\s+(?<name>[\p{L}_][\p{L}\p{N}_]*)|(?:[\p{L}_][\p{L}\p{N}_<>,.?\[\]\s:*&]+\s+)(?<name>[\p{L}_][\p{L}\p{N}_]*)\s*\([^;]*\)\s*(?:=>|\{|where|$))", RegexOptions.CultureInvariant)]
    private static partial Regex CFamilySymbolRegex();

    [GeneratedRegex(@"^\s*(?:(?:export|default|async|declare)\s+)*(?:(?<kind>class|interface|enum|type|function)\s+(?<name>[$\p{L}_][$\p{L}\p{N}_]*)|(?<kind>const|let|var)\s+(?<name>[$\p{L}_][$\p{L}\p{N}_]*)\s*=\s*(?:async\s*)?(?:\([^)]*\)|[$\p{L}_][$\p{L}\p{N}_]*)\s*=>)", RegexOptions.CultureInvariant)]
    private static partial Regex JavaScriptSymbolRegex();

    [GeneratedRegex(@"^\s*(?:(?:async)\s+)?(?<kind>def|class)\s+(?<name>[\p{L}_][\p{L}\p{N}_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex PythonSymbolRegex();

    [GeneratedRegex(@"^\s*(?:(?:pub)\s+)?(?:(?<kind>type|func)\s+)(?:\([^)]*\)\s*)?(?<name>[\p{L}_][\p{L}\p{N}_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex GoSymbolRegex();

    [GeneratedRegex(@"^\s*(?:(?:pub|async|unsafe|const)\s+)*(?<kind>fn|struct|enum|trait|mod|type|const)\s+(?<name>[\p{L}_][\p{L}\p{N}_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex RustSymbolRegex();

    private enum SymbolLanguage
    {
        Unsupported,
        CFamily,
        JavaScript,
        Python,
        Go,
        Rust
    }
}
