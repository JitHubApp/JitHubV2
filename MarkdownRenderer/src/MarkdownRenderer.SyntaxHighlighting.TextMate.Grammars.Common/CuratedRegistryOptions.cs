using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Themes.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common;

/// <summary>
/// Registry over the curated resources embedded in this assembly. Keeping this
/// adapter in the Common pack prevents its resource payload from leaking into the
/// lean integration package.
/// </summary>
internal sealed class CuratedRegistryOptions : IRegistryOptions
{
    private const string ResourcePrefix = "TextMateSharp.Grammars.Resources.";

    private static readonly IReadOnlyDictionary<string, string> LanguageScopes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["c"] = "source.c",
            ["cpp"] = "source.cpp",
            ["csharp"] = "source.cs",
            ["css"] = "source.css",
            ["diff"] = "source.diff",
            ["dockerfile"] = "source.dockerfile",
            ["fsharp"] = "source.fsharp",
            ["go"] = "source.go",
            ["hlsl"] = "source.hlsl",
            ["html"] = "text.html.derivative",
            ["java"] = "source.java",
            ["javascript"] = "source.js",
            ["javascriptreact"] = "source.js.jsx",
            ["json"] = "source.json",
            ["jsonc"] = "source.json.comments",
            ["lua"] = "source.lua",
            ["markdown"] = "text.html.markdown",
            ["objectivec"] = "source.objc",
            ["objectivecpp"] = "source.objcpp",
            ["php"] = "source.php",
            ["powershell"] = "source.powershell",
            ["python"] = "source.python",
            ["ruby"] = "source.ruby",
            ["rust"] = "source.rust",
            ["shaderlab"] = "source.shaderlab",
            ["shellscript"] = "source.shell",
            ["sql"] = "source.sql",
            ["swift"] = "source.swift",
            ["typescript"] = "source.ts",
            ["typescriptreact"] = "source.tsx",
            ["vb"] = "source.asp.vb.net",
            ["xml"] = "text.xml",
            ["xsl"] = "text.xml.xsl",
            ["yaml"] = "source.yaml",
            ["cuda-cpp"] = "source.cuda-cpp",
        };

    private static readonly IReadOnlyDictionary<string, string> GrammarResources =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source.c"] = ResourcePrefix + "Grammars.cpp.syntaxes.c.tmLanguage.json",
            ["source.cpp"] = ResourcePrefix + "Grammars.cpp.syntaxes.cpp.tmLanguage.json",
            ["source.cpp.embedded.macro"] = ResourcePrefix + "Grammars.cpp.syntaxes.cpp.embedded.macro.tmLanguage.json",
            ["source.c.platform"] = ResourcePrefix + "Grammars.cpp.syntaxes.platform.tmLanguage.json",
            ["source.cuda-cpp"] = ResourcePrefix + "Grammars.cpp.syntaxes.cuda-cpp.tmLanguage.json",
            ["source.cs"] = ResourcePrefix + "Grammars.csharp.syntaxes.csharp.tmLanguage.json",
            ["source.css"] = ResourcePrefix + "Grammars.css.syntaxes.css.tmLanguage.json",
            ["source.diff"] = ResourcePrefix + "Grammars.diff.syntaxes.diff.tmLanguage.json",
            ["source.dockerfile"] = ResourcePrefix + "Grammars.docker.syntaxes.docker.tmLanguage.json",
            ["source.fsharp"] = ResourcePrefix + "Grammars.fsharp.syntaxes.fsharp.tmLanguage.json",
            ["source.go"] = ResourcePrefix + "Grammars.go.syntaxes.go.tmLanguage.json",
            ["source.hlsl"] = ResourcePrefix + "Grammars.hlsl.syntaxes.hlsl.tmLanguage.json",
            ["text.html.basic"] = ResourcePrefix + "Grammars.html.syntaxes.html.tmLanguage.json",
            ["text.html.derivative"] = ResourcePrefix + "Grammars.html.syntaxes.html-derivative.tmLanguage.json",
            ["source.java"] = ResourcePrefix + "Grammars.java.syntaxes.java.tmLanguage.json",
            ["source.js"] = ResourcePrefix + "Grammars.javascript.syntaxes.JavaScript.tmLanguage.json",
            ["source.js.jsx"] = ResourcePrefix + "Grammars.javascript.syntaxes.JavaScriptReact.tmLanguage.json",
            ["source.json"] = ResourcePrefix + "Grammars.json.syntaxes.JSON.tmLanguage.json",
            ["source.json.comments"] = ResourcePrefix + "Grammars.json.syntaxes.JSONC.tmLanguage.json",
            ["source.lua"] = ResourcePrefix + "Grammars.lua.syntaxes.lua.tmLanguage.json",
            ["text.html.markdown"] = ResourcePrefix + "Grammars.markdownbasics.syntaxes.markdown.tmLanguage.json",
            ["source.objc"] = ResourcePrefix + "Grammars.objectivec.syntaxes.objective-c.tmLanguage.json",
            ["source.objcpp"] = ResourcePrefix + "Grammars.objectivec.syntaxes.objective-c++.tmLanguage.json",
            ["text.html.php"] = ResourcePrefix + "Grammars.php.syntaxes.html.tmLanguage.json",
            ["source.php"] = ResourcePrefix + "Grammars.php.syntaxes.php.tmLanguage.json",
            ["source.powershell"] = ResourcePrefix + "Grammars.powershell.syntaxes.powershell.tmLanguage.json",
            ["source.python"] = ResourcePrefix + "Grammars.python.syntaxes.MagicPython.tmLanguage.json",
            ["source.regexp.python"] = ResourcePrefix + "Grammars.python.syntaxes.MagicRegExp.tmLanguage.json",
            ["source.ruby"] = ResourcePrefix + "Grammars.ruby.syntaxes.ruby.tmLanguage.json",
            ["source.rust"] = ResourcePrefix + "Grammars.rust.syntaxes.rust.tmLanguage.json",
            ["source.shaderlab"] = ResourcePrefix + "Grammars.shaderlab.syntaxes.shaderlab.tmLanguage.json",
            ["source.shell"] = ResourcePrefix + "Grammars.shellscript.syntaxes.shell-unix-bash.tmLanguage.json",
            ["source.sql"] = ResourcePrefix + "Grammars.sql.syntaxes.sql.tmLanguage.json",
            ["source.swift"] = ResourcePrefix + "Grammars.swift.syntaxes.swift.tmLanguage.json",
            ["source.ts"] = ResourcePrefix + "Grammars.typescriptbasics.syntaxes.TypeScript.tmLanguage.json",
            ["source.tsx"] = ResourcePrefix + "Grammars.typescriptbasics.syntaxes.TypeScriptReact.tmLanguage.json",
            ["documentation.injection.ts"] = ResourcePrefix + "Grammars.typescriptbasics.syntaxes.jsdoc.ts.injection.tmLanguage.json",
            ["documentation.injection.js.jsx"] = ResourcePrefix + "Grammars.typescriptbasics.syntaxes.jsdoc.js.injection.tmLanguage.json",
            ["source.asp.vb.net"] = ResourcePrefix + "Grammars.vb.syntaxes.asp-vb-net.tmlanguage.json",
            ["text.xml"] = ResourcePrefix + "Grammars.xml.syntaxes.xml.tmLanguage.json",
            ["text.xml.xsl"] = ResourcePrefix + "Grammars.xml.syntaxes.xsl.tmLanguage.json",
            ["source.yaml"] = ResourcePrefix + "Grammars.yaml.syntaxes.yaml.tmLanguage.json",
        };

    private static readonly ICollection<string> TypeScriptInjections =
        ["documentation.injection.ts"];

    private static readonly ICollection<string> JavaScriptInjections =
        ["documentation.injection.js.jsx"];

    private static readonly ICollection<string> NoInjections = Array.Empty<string>();

    private readonly string _themeResource;
    private readonly Dictionary<string, IRawGrammar> _grammarCache = new(StringComparer.Ordinal);
    private IRawTheme? _theme;

    public CuratedRegistryOptions(TextMateGrammarThemeVariant variant)
    {
        _themeResource = variant == TextMateGrammarThemeVariant.Light
            ? ResourcePrefix + "Themes.light_vs.json"
            : ResourcePrefix + "Themes.dark_vs.json";
    }

    public string? ResolveScope(string languageId) =>
        LanguageScopes.TryGetValue(languageId, out var scope) ? scope : null;

    public IRawTheme GetTheme(string scopeName) => GetDefaultTheme();

    public IRawGrammar GetGrammar(string scopeName)
    {
        if (_grammarCache.TryGetValue(scopeName, out var cached))
            return cached;
        if (!GrammarResources.TryGetValue(scopeName, out var resourceName))
            return null!;

        using var reader = OpenResource(resourceName);
        var grammar = GrammarReader.ReadGrammarSync(reader);
        _grammarCache[scopeName] = grammar;
        return grammar;
    }

    public ICollection<string> GetInjections(string scopeName) => scopeName switch
    {
        "source.ts" or "source.tsx" => TypeScriptInjections,
        "source.js" or "source.js.jsx" => JavaScriptInjections,
        _ => NoInjections,
    };

    public IRawTheme GetDefaultTheme()
    {
        if (_theme is not null)
            return _theme;

        using var reader = OpenResource(_themeResource);
        _theme = ThemeReader.ReadThemeSync(reader);
        return _theme;
    }

    private static StreamReader OpenResource(string resourceName)
    {
        var stream = typeof(CuratedRegistryOptions).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new InvalidDataException(
                $"The curated TextMate resource '{resourceName}' is missing from " +
                $"{Assembly.GetExecutingAssembly().GetName().Name}.");
        }

        return new StreamReader(stream);
    }
}
