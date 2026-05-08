using System;
using System.Collections.Generic;
using System.Text;
using JitHub.Services.CodeViewer;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public class LanguageIdResolverTests
{
    private static LanguageIdResolver CreateResolver()
    {
        var extensions = new Dictionary<string, string>
        {
            [".cs"] = "csharp",
            [".py"] = "python",
            [".js"] = "javascript",
            [".ts"] = "typescript",
            [".json"] = "json",
            [".xml"] = "xml",
            [".sh"] = "bash",
        };
        var filenames = new Dictionary<string, string>
        {
            ["Makefile"] = "makefile",
            ["Dockerfile"] = "dockerfile",
        };
        var interpreters = new Dictionary<string, string>
        {
            ["python"] = "python",
            ["python3"] = "python",
            ["bash"] = "bash",
            ["sh"] = "bash",
            ["node"] = "javascript",
        };
        return new LanguageIdResolver(extensions, filenames, interpreters);
    }

    private static ReadOnlySpan<byte> Shebang(string line)
        => Encoding.UTF8.GetBytes($"#!/{line}\n");

    // ── Extension resolution ─────────────────────────────────────────────────

    [Fact]
    public void Resolve_CsExtension_ReturnsCsharp()
    {
        var r = CreateResolver();
        Assert.Equal("csharp", r.Resolve("Program.cs"));
    }

    [Fact]
    public void Resolve_PyExtension_ReturnsPython()
    {
        var r = CreateResolver();
        Assert.Equal("python", r.Resolve("script.py"));
    }

    [Fact]
    public void Resolve_JsExtension_ReturnsJavascript()
    {
        var r = CreateResolver();
        Assert.Equal("javascript", r.Resolve("app.js"));
    }

    [Fact]
    public void Resolve_TsExtension_ReturnsTypescript()
    {
        var r = CreateResolver();
        Assert.Equal("typescript", r.Resolve("index.ts"));
    }

    // ── Case insensitivity ────────────────────────────────────────────────────

    [Fact]
    public void Resolve_UpperCaseCsExtension_ReturnsCsharp()
    {
        var r = CreateResolver();
        Assert.Equal("csharp", r.Resolve("Program.CS"));
    }

    [Fact]
    public void Resolve_MixedCasePyExtension_ReturnsPython()
    {
        var r = CreateResolver();
        Assert.Equal("python", r.Resolve("script.Py"));
    }

    // ── Unknown extension fallback ────────────────────────────────────────────

    [Fact]
    public void Resolve_UnknownExtension_ReturnsPlaintext()
    {
        var r = CreateResolver();
        Assert.Equal("plaintext", r.Resolve("file.xyz"));
    }

    // ── Empty / null path ────────────────────────────────────────────────────

    [Fact]
    public void Resolve_EmptyPath_ReturnsPlaintext()
    {
        var r = CreateResolver();
        Assert.Equal("plaintext", r.Resolve(string.Empty));
    }

    [Fact]
    public void Resolve_NullPath_ReturnsPlaintext()
    {
        var r = CreateResolver();
        Assert.Equal("plaintext", r.Resolve(null!));
    }

    // ── Filename-based resolution ─────────────────────────────────────────────

    [Fact]
    public void Resolve_MakefileFilename_ReturnsMakefile()
    {
        var r = CreateResolver();
        Assert.Equal("makefile", r.Resolve("Makefile"));
    }

    [Fact]
    public void Resolve_DockerfileFilename_ReturnsDockerfile()
    {
        var r = CreateResolver();
        Assert.Equal("dockerfile", r.Resolve("Dockerfile"));
    }

    [Fact]
    public void Resolve_DockerfileInSubdir_ReturnsDockerfile()
    {
        var r = CreateResolver();
        Assert.Equal("dockerfile", r.Resolve("src/Dockerfile"));
    }

    // ── Shebang detection ─────────────────────────────────────────────────────

    [Fact]
    public void Resolve_ShebangPython_ReturnsPython()
    {
        var r = CreateResolver();
        var shebang = Encoding.UTF8.GetBytes("#!/usr/bin/python\n");
        Assert.Equal("python", r.Resolve("script", shebang));
    }

    [Fact]
    public void Resolve_ShebangEnvPython3_ReturnsPython()
    {
        var r = CreateResolver();
        var shebang = Encoding.UTF8.GetBytes("#!/usr/bin/env python3\n");
        Assert.Equal("python", r.Resolve("script", shebang));
    }

    [Fact]
    public void Resolve_ShebangBinBash_ReturnsBash()
    {
        var r = CreateResolver();
        var shebang = Encoding.UTF8.GetBytes("#!/bin/bash\n");
        Assert.Equal("bash", r.Resolve("script", shebang));
    }

    [Fact]
    public void Resolve_ShebangAbsolutePathNode_ReturnsJavascript()
    {
        var r = CreateResolver();
        var shebang = Encoding.UTF8.GetBytes("#!/usr/local/bin/node\n");
        Assert.Equal("javascript", r.Resolve("script", shebang));
    }

    [Fact]
    public void Resolve_ShebangWithArgs_StripArgs()
    {
        var r = CreateResolver();
        var shebang = Encoding.UTF8.GetBytes("#!/usr/bin/env python3 -u\n");
        Assert.Equal("python", r.Resolve("script", shebang));
    }

    [Fact]
    public void Resolve_ShebangNotAtStart_NoMatch()
    {
        var r = CreateResolver();
        var content = Encoding.UTF8.GetBytes("# comment\n#!/usr/bin/python\n");
        // file has no known extension, shebang not at start → fallback
        Assert.Equal("plaintext", r.Resolve("script", content));
    }

    [Fact]
    public void Resolve_UnknownShebangInterpreter_ReturnsPlaintext()
    {
        var r = CreateResolver();
        var shebang = Encoding.UTF8.GetBytes("#!/usr/bin/ruby\n");
        Assert.Equal("plaintext", r.Resolve("script", shebang));
    }

    // ── Extension overrides shebang when extension known ─────────────────────

    [Fact]
    public void Resolve_KnownExtensionWithShebang_ExtensionWins()
    {
        var r = CreateResolver();
        var shebang = Encoding.UTF8.GetBytes("#!/usr/bin/python\n");
        // .sh extension should resolve before shebang
        Assert.Equal("bash", r.Resolve("deploy.sh", shebang));
    }

    // ── IsKnown ───────────────────────────────────────────────────────────────

    [Fact]
    public void IsKnown_KnownExtension_ReturnsTrue()
    {
        var r = CreateResolver();
        Assert.True(r.IsKnown("Program.cs"));
    }

    [Fact]
    public void IsKnown_KnownFilename_ReturnsTrue()
    {
        var r = CreateResolver();
        Assert.True(r.IsKnown("Makefile"));
    }

    [Fact]
    public void IsKnown_UnknownExtension_ReturnsFalse()
    {
        var r = CreateResolver();
        Assert.False(r.IsKnown("file.xyz"));
    }

    [Fact]
    public void IsKnown_EmptyPath_ReturnsFalse()
    {
        var r = CreateResolver();
        Assert.False(r.IsKnown(string.Empty));
    }
}
