using System.Security.Cryptography;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using MarkdownRenderer.Mermaid;
using System.Text;
using System.Text.Json;

namespace MarkdownRenderer.Mermaid.Tests;

public sealed class MermaidProvenanceTests
{
    [Fact]
    public void ManagedLibraryRemainsRidNeutralAnyCpuIl()
    {
        using var stream = File.OpenRead(typeof(MermaidRenderer).Assembly.Location);
        using var reader = new PEReader(stream);

        Assert.Equal(Machine.I386, reader.PEHeaders.CoffHeader.Machine);
        CorFlags flags = reader.PEHeaders.CorHeader!.Flags;
        Assert.True(flags.HasFlag(CorFlags.ILOnly));
        Assert.False(flags.HasFlag(CorFlags.Requires32Bit));
        Assert.False(flags.HasFlag(CorFlags.Prefers32Bit));
    }

    [Fact]
    public void PinnedMermanInputsMatchAuditedHashesAndExcludeElk()
    {
        string root = FindPackRoot();
        using JsonDocument provenance = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "MERMAN_PROVENANCE.json")));
        JsonElement document = provenance.RootElement;

        Assert.Equal("ac53f21ba97e5bd8da7f849fbcdffe64695171e6", document.GetProperty("revision").GetString());
        Assert.Equal("d15030d6fbc09b0e4b82409cec74a7ad3d994e24", document.GetProperty("tree").GetString());
        Assert.Equal("11.16.1", document.GetProperty("mermaidCompatibility").GetProperty("version").GetString());
        Assert.False(document.GetProperty("licensingBoundary").GetProperty("elkShipped").GetBoolean());
        JsonElement build = document.GetProperty("build");
        Assert.Equal("stable-x86_64-pc-windows-msvc", build.GetProperty("rustToolchain").GetString());
        Assert.Equal("1.96.0 (ac68faa20 2026-05-25)", build.GetProperty("rustc").GetString());
        Assert.Equal("pwsh -NoProfile -File native/Build-Native.ps1", build.GetProperty("rebuildCommand").GetString());

        AssertHash(Path.Combine(root, "MERMAN_PARITY_BASELINE.md"), "f8708ac36d7385cc4cc0ac52427699cadb951de994d3f5fd8e37510ac8c70241");
        AssertHash(Path.Combine(root, "licenses", "MERMAN-LICENSE-MIT.txt"), "645fe3acd17f0ec18b05a5cbc8071c16fc888bfa8356d9087c265df69464d893");
        AssertHash(Path.Combine(root, "licenses", "MERMAN-THIRD-PARTY-NOTICES.md"), "5226375f9543c873b80656d2a396253e7b617e0ed1e7c766d0b9bf622c1cc3ac");
        AssertHash(Path.Combine(root, "native", "Cargo.lock"), "6b0eacfb2cad795631799a9ffb4b21ac9b762194bd93e1348969eaf5e6f94ab1");
        AssertHash(Path.Combine(root, "NATIVE_RUST_DEPENDENCIES.json"), "614c72e5b522136f4edc02fb32ab534515d0e0bd65003e86fbe182173575ee07");

        using JsonDocument inventory = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "NATIVE_RUST_DEPENDENCIES.json")));
        Assert.Equal(196, inventory.RootElement.GetProperty("packageCount").GetInt32());
        foreach (JsonElement package in inventory.RootElement.GetProperty("packages").EnumerateArray())
        {
            string license = package.GetProperty("license").GetString()!;
            Assert.DoesNotContain("EPL", license, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("GPL", license, StringComparison.OrdinalIgnoreCase);
        }

        string lockFile = File.ReadAllText(Path.Combine(root, "native", "Cargo.lock"));
        Assert.Contains("rev=ac53f21ba97e5bd8da7f849fbcdffe64695171e6", lockFile, StringComparison.Ordinal);
        Assert.DoesNotContain("merman-layout-elk", lockFile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eclipse-elk", lockFile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("elkjs", lockFile, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertHash(string path, string expected)
    {
        // These audited source artifacts are UTF-8 text. Normalize Git's optional
        // CRLF checkout conversion so the provenance hash represents the pinned
        // upstream bytes rather than the workstation's checkout setting.
        string text = Encoding.UTF8.GetString(File.ReadAllBytes(path)).Replace("\r\n", "\n", StringComparison.Ordinal);
        string actual = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(expected, actual);
    }

    private static string FindPackRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer.Mermaid", "MERMAN_PROVENANCE.json");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("MarkdownRenderer.Mermaid source root was not found.");
    }
}
