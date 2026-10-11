using System.Diagnostics;
using System.Xml.Linq;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class FilterPublicXmlDocumentationTests
{
    [Fact]
    public void Filter_removes_non_public_members_but_keeps_publicly_visible_nested_surface()
    {
        string[] inputIds =
        [
            "T:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture",
            "F:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.PublicField",
            "F:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.ProtectedField",
            "F:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.ProtectedInternalField",
            "F:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.InternalField",
            "F:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture._privateField",
            "M:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.PublicMethod",
            "M:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.ProtectedMethod",
            "M:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.ProtectedInternalMethod",
            "M:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.InternalMethod",
            "M:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.PrivateMethod",
            "P:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.PublicProperty",
            "P:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.ProtectedProperty",
            "P:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.ProtectedInternalProperty",
            "P:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.InternalProperty",
            "P:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.PrivateProperty",
            "E:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.PublicEvent",
            "E:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.ProtectedEvent",
            "E:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.ProtectedInternalEvent",
            "E:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.InternalEvent",
            "E:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.PrivateEvent",
            "T:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.PublicNested",
            "T:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.ProtectedNested",
            "T:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.ProtectedInternalNested",
            "T:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.InternalNested",
            "T:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.PrivateNested",
            "M:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.PublicNested.VisibleMethod",
            "M:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.InternalNested.HiddenMethod",
            "M:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.PrivateNested.HiddenMethod",
        ];

        IReadOnlyList<string> retained = RunFilter(inputIds);

        string[] expected =
        [
            inputIds[0], inputIds[1], inputIds[2], inputIds[3], inputIds[6], inputIds[7], inputIds[8],
            inputIds[11], inputIds[12], inputIds[13], inputIds[16], inputIds[17], inputIds[18],
            inputIds[21], inputIds[22], inputIds[23], inputIds[26],
        ];
        Assert.Equal(expected, retained);
    }

    [Fact]
    public void Filter_uses_exact_overload_and_generic_signature_ids()
    {
        string[] inputIds =
        [
            "M:MarkdownRenderer.Tests.PublicXmlDocumentationSignatureFixture.Overloaded(System.Int32)",
            "M:MarkdownRenderer.Tests.PublicXmlDocumentationSignatureFixture.Overloaded(System.String)",
            "M:MarkdownRenderer.Tests.PublicXmlDocumentationSignatureFixture.Generic``1(System.Collections.Generic.IEnumerable{``0})",
            "M:MarkdownRenderer.Tests.PublicXmlDocumentationSignatureFixture.Generic``1(System.String)",
            "M:MarkdownRenderer.Tests.PublicXmlDocumentationSignatureFixture.NestedGeneric(System.Collections.Generic.Dictionary{System.String,System.Collections.Generic.List{System.Int32}})",
            "M:MarkdownRenderer.Tests.PublicXmlDocumentationNestedTypeFixture`1.AcceptNested(MarkdownRenderer.Tests.PublicXmlDocumentationNestedTypeFixture{`0}.Inner{System.String})",
            "M:MarkdownRenderer.Tests.PublicXmlDocumentationNestedTypeFixture`1.AcceptNonGenericNested(MarkdownRenderer.Tests.PublicXmlDocumentationNestedTypeFixture{`0}.NonGenericInner)",
            "M:MarkdownRenderer.Tests.PublicXmlDocumentationSignatureFixture.RefValue(System.Int32@)",
            "M:MarkdownRenderer.Tests.PublicXmlDocumentationSignatureFixture.Rectangular(System.Int32[0:,0:])",
            "P:MarkdownRenderer.Tests.PublicXmlDocumentationSignatureFixture.Item(System.Int32,System.String)",
        ];

        IReadOnlyList<string> retained = RunFilter(inputIds);

        string[] expected = [inputIds[0], inputIds[2], inputIds[4], inputIds[5], inputIds[6], inputIds[7], inputIds[8], inputIds[9]];
        Assert.Equal(expected, retained);
    }

    [Fact]
    public void Filter_fails_closed_for_an_xml_id_without_a_metadata_definition_and_preserves_input()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string xmlPath = Path.Combine(directory, "test.xml");
            string original = BuildXml(
                "M:MarkdownRenderer.Tests.PublicXmlDocumentationVisibilityFixture.DoesNotExist");
            File.WriteAllText(xmlPath, original, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            ProcessResult result = RunFilterProcess(xmlPath);

            Assert.NotEqual(0, result.ExitCode);
            // ConciseView inserts a gutter when its error text wraps. Assert
            // the semantic error, not the host's console width or formatting.
            string errorText = System.Text.RegularExpressions.Regex.Replace(
                result.Output, @"(?m)^\s*\|\s?", string.Empty);
            Assert.Contains("DoesNotExist", errorText, StringComparison.Ordinal);
            Assert.Matches(@"has\s+no\s+exact\s+metadata\s+definition", errorText);
            Assert.Equal(original, File.ReadAllText(xmlPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static IReadOnlyList<string> RunFilter(params string[] memberIds)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string xmlPath = Path.Combine(directory, "test.xml");
            File.WriteAllText(
                xmlPath,
                BuildXml(memberIds),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            ProcessResult result = RunFilterProcess(xmlPath);
            Assert.True(result.ExitCode == 0, result.Output);

            XDocument filtered = XDocument.Load(xmlPath);
            return filtered
                .Root!
                .Element("members")!
                .Elements("member")
                .Select(static member => (string)member.Attribute("name")!)
                .ToArray();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProcessResult RunFilterProcess(string xmlPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(FindFilterScript());
        startInfo.ArgumentList.Add("-AssemblyPath");
        startInfo.ArgumentList.Add(typeof(PublicXmlDocumentationVisibilityFixture).Assembly.Location);
        startInfo.ArgumentList.Add("-XmlPath");
        startInfo.ArgumentList.Add(xmlPath);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("PowerShell did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(45)))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("The public XML documentation filter exceeded 45 seconds.");
        }

        return new ProcessResult(process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    private static string FindFilterScript()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string direct = Path.Combine(directory.FullName, "eng", "Filter-PublicXmlDocumentation.ps1");
            if (File.Exists(direct))
                return direct;

            string nested = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "eng",
                "Filter-PublicXmlDocumentation.ps1");
            if (File.Exists(nested))
                return nested;
        }

        throw new FileNotFoundException(
            "Could not locate Filter-PublicXmlDocumentation.ps1 from the test output.");
    }

    private static string BuildXml(params string[] memberIds)
    {
        var members = new XElement(
            "members",
            memberIds.Select(static memberId =>
                new XElement("member", new XAttribute("name", memberId), new XElement("summary", "fixture"))));
        return new XDocument(new XElement("doc", members)).ToString(SaveOptions.DisableFormatting);
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"markdown-public-xml-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private readonly record struct ProcessResult(int ExitCode, string Output);
}

public class PublicXmlDocumentationVisibilityFixture
{
    public int PublicField = 1;
    protected int ProtectedField = 2;
    protected internal int ProtectedInternalField = 3;
    internal int InternalField = 4;
    private int _privateField = 5;

    public int ReadFields() => PublicField + ProtectedField + ProtectedInternalField + InternalField + _privateField;

    public void PublicMethod() { }
    protected void ProtectedMethod() { }
    protected internal void ProtectedInternalMethod() { }
    internal void InternalMethod() { }
    private void PrivateMethod() { }

    public int PublicProperty { get; private set; }
    protected int ProtectedProperty { get; set; }
    protected internal int ProtectedInternalProperty { get; set; }
    internal int InternalProperty { get; set; }
    private int PrivateProperty { get; set; }

    public event EventHandler? PublicEvent { add { } remove { } }
    protected event EventHandler? ProtectedEvent { add { } remove { } }
    protected internal event EventHandler? ProtectedInternalEvent { add { } remove { } }
    internal event EventHandler? InternalEvent { add { } remove { } }
    private event EventHandler? PrivateEvent { add { } remove { } }

    public int this[int index, string key] => index + key.Length;

    public class PublicNested
    {
        public void VisibleMethod() { }
    }

    protected class ProtectedNested { }
    protected internal class ProtectedInternalNested { }

    internal class InternalNested
    {
        public void HiddenMethod() { }
    }

    private class PrivateNested
    {
        public void HiddenMethod() { }
    }
}

public class PublicXmlDocumentationSignatureFixture
{
    public void Overloaded(int value) { }
    private void Overloaded(string value) { }

    public void Generic<T>(IEnumerable<T> values) { }
    private void Generic<T>(string value) { }

    public void NestedGeneric(Dictionary<string, List<int>> values) { }
    public void RefValue(ref int value) { }
    public void Rectangular(int[,] values) { }

    public int this[int index, string key] => index + key.Length;
}

public class PublicXmlDocumentationNestedTypeFixture<T>
{
    public class Inner<U> { }

    public class NonGenericInner { }

    public void AcceptNested(Inner<string> value) { }

    public void AcceptNonGenericNested(NonGenericInner value) { }
}
