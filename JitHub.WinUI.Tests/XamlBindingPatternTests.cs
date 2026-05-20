using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace JitHub.WinUI.Tests;

public sealed class XamlBindingPatternTests
{
    [Fact]
    public void CodeBackedTemplatesDoNotUseUntypedPropertyPathBindings()
    {
        string winuiRoot = FindWinUIProjectRoot();
        string viewsRoot = Path.Combine(winuiRoot, "Views");
        string looseTemplatesRoot = Path.Combine(viewsRoot, "DataTemplates");
        List<string> violations = [];

        foreach (string path in Directory.EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(looseTemplatesRoot, StringComparison.OrdinalIgnoreCase))
            .Order())
        {
            string relativePath = Path.GetRelativePath(winuiRoot, path);
            string text = File.ReadAllText(path);

            foreach (Match template in Regex.Matches(
                text,
                "<DataTemplate(?<attrs>[^>]*)>(?<body>.*?)</DataTemplate>",
                RegexOptions.Singleline))
            {
                string attrs = template.Groups["attrs"].Value;
                if (attrs.Contains("x:DataType", StringComparison.Ordinal))
                {
                    continue;
                }

                string body = template.Groups["body"].Value;
                foreach (Match binding in Regex.Matches(body, "\\{Binding(?<expr>[^}]*)\\}"))
                {
                    string expression = binding.Groups["expr"].Value.Trim();
                    if (IsWholeObjectOrFrameworkBinding(expression))
                    {
                        continue;
                    }

                    int line = GetLineNumber(text, template.Index + body.IndexOf(binding.Value, StringComparison.Ordinal));
                    violations.Add($"{relativePath}:{line}: {binding.Value}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Untyped property-path bindings in code-backed DataTemplates are trim-fragile in Release; use x:Bind or a whole-object Binding:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void LooseResourceDictionariesDoNotUseXBind()
    {
        string winuiRoot = FindWinUIProjectRoot();
        string dataTemplatesRoot = Path.Combine(winuiRoot, "Views", "DataTemplates");

        List<string> violations = Directory
            .EnumerateFiles(dataTemplatesRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("{x:Bind", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(winuiRoot, path))
            .Order()
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Loose ResourceDictionary files should not use x:Bind; use a code-backed control instead:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ContainerStyleIsOnlyAppliedToBorders()
    {
        string winuiRoot = FindWinUIProjectRoot();
        List<string> violations = [];

        foreach (string path in Directory.EnumerateFiles(winuiRoot, "*.xaml", SearchOption.AllDirectories).Order())
        {
            string relativePath = Path.GetRelativePath(winuiRoot, path);
            string text = File.ReadAllText(path);

            foreach (Match match in Regex.Matches(
                text,
                "<(?<element>[\\w:]+)\\b(?<attrs>[^>]*\\bStyle\\s*=\\s*\"\\{(?:ThemeResource|StaticResource)\\s+Container\\}\"[^>]*)>",
                RegexOptions.Singleline))
            {
                string element = match.Groups["element"].Value;
                if (string.Equals(element, "Border", StringComparison.Ordinal) ||
                    element.EndsWith(":Border", StringComparison.Ordinal))
                {
                    continue;
                }

                int line = GetLineNumber(text, match.Index);
                violations.Add($"{relativePath}:{line}: {element}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "The shared Container style targets Border and crashes XAML parsing when applied to another element:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    private static bool IsWholeObjectOrFrameworkBinding(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return true;
        }

        return expression.StartsWith("Mode", StringComparison.Ordinal)
            || expression.StartsWith("Converter", StringComparison.Ordinal)
            || expression.Contains("ElementName=", StringComparison.Ordinal)
            || expression.Contains("RelativeSource=", StringComparison.Ordinal);
    }

    private static int GetLineNumber(string text, int index)
        => text[..Math.Max(0, index)].Count(character => character == '\n') + 1;

    private static string FindWinUIProjectRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, "JitHub.WinUI");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate JitHub.WinUI from the test output directory.");
    }
}
