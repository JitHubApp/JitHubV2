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
        AssertHash(Path.Combine(root, "native", "Cargo.lock"), "5c3f2838ade17638d30f8a5e7e7c3b4cd5456682d3dafe7a41468b094e55ef62");
        AssertHash(Path.Combine(root, "NATIVE_RUST_DEPENDENCIES.json"), "4282f1b37437f5beb538723becbccd3a295230ff0273edacf06bd9d080eed64d");

        const string manifestHash = "be5cb0eef0bc11be1ae1be4c1f4db645f49a4db93cef28cde00743a8f4a0c639";
        const string patchHash = "0462b167a27507aa4fc8f93c9ec19054afb84589ab50ee354551f5139137c21a";
        const string entityPatchHash = "653b355a18561801310361c64a6ba5d7e6ede9b715d4067fff68a3c5fc957fb1";
        const string markdownPatchHash = "514fe9600c258e904f38b06e0a328b5c4aa57fd3c8a3b7da5264ab26ba23a569";
        const string whitespacePatchHash = "ec5b494b7c51adbe47cb233ef6d7becdd146dc14286734b08c01807dfa05dec4";
        const string originPatchHash = "89431192103991613400a47b722e352a8f9e8faad30949469ae74641e3eb2ec5";
        const string patchSetHash = "f32de3178414b1fcaf5f95b4d66a30bd020bb56631d0d1e59b1a11c4812613bb";
        JsonElement patches = document.GetProperty("sourcePatches");
        Assert.Equal(manifestHash, patches.GetProperty("manifestSha256").GetString());
        Assert.Equal(patchSetHash, patches.GetProperty("patchSetSha256").GetString());
        string patchRoot = Path.Combine(root, "native", "patches");
        AssertRawHash(Path.Combine(patchRoot, "MERMAN_SOURCE_PATCHES.json"), manifestHash);
        AssertRawHash(Path.Combine(patchRoot, "0001-final-node-source-provenance.patch"), patchHash);
        AssertRawHash(Path.Combine(patchRoot, "0002-flowchart-svg-text-entities.patch"), entityPatchHash);
        AssertRawHash(Path.Combine(patchRoot, "0003-final-markdown-text-and-self-loop-labels.patch"), markdownPatchHash);
        AssertRawHash(Path.Combine(patchRoot, "0004-final-markdown-entity-whitespace.patch"), whitespacePatchHash);
        AssertRawHash(Path.Combine(patchRoot, "0005-generated-entity-origin-projection.patch"), originPatchHash);
        using JsonDocument patchManifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(patchRoot, "MERMAN_SOURCE_PATCHES.json")));
        Assert.Equal(document.GetProperty("revision").GetString(), patchManifest.RootElement.GetProperty("baseCommit").GetString());
        Assert.Equal(document.GetProperty("tree").GetString(), patchManifest.RootElement.GetProperty("baseTree").GetString());
        Assert.Equal(2, patchManifest.RootElement.GetProperty("schemaVersion").GetInt32());
        JsonElement[] changedFiles = patchManifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(132, changedFiles.Length);
        Assert.Equal(129, changedFiles.Count(file => file.GetProperty("changeKind").GetString() == "modify"));
        Assert.Equal(3, changedFiles.Count(file => file.GetProperty("changeKind").GetString() == "add"));
        Assert.All(changedFiles.Where(file => file.GetProperty("changeKind").GetString() == "add"),
            file => Assert.Equal(JsonValueKind.Null, file.GetProperty("upstreamSha256").ValueKind));
        JsonElement[] sourcePatches = patchManifest.RootElement.GetProperty("patches").EnumerateArray().ToArray();
        Assert.Equal(new[] { "0001-final-node-source-provenance.patch", "0002-flowchart-svg-text-entities.patch", "0003-final-markdown-text-and-self-loop-labels.patch", "0004-final-markdown-entity-whitespace.patch", "0005-generated-entity-origin-projection.patch" },
            sourcePatches.Select(item => item.GetProperty("path").GetString()));
        Assert.Equal(new[] { patchHash, entityPatchHash, markdownPatchHash, whitespacePatchHash, originPatchHash },
            sourcePatches.Select(item => item.GetProperty("sha256").GetString()));
        string patchSetIdentity = string.Concat(sourcePatches.Select(item =>
            $"{item.GetProperty("path").GetString()}\n{item.GetProperty("sha256").GetString()}\n"));
        Assert.Equal(patchSetHash, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(patchSetIdentity))));

        using JsonDocument inventory = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "NATIVE_RUST_DEPENDENCIES.json")));
        Assert.Equal(196, inventory.RootElement.GetProperty("packageCount").GetInt32());
        string[] preparedPackages = ["dugong", "dugong-graphlib", "manatee", "merman-core", "merman-render", "roughr-merman"];
        Assert.Equal(preparedPackages, inventory.RootElement.GetProperty("pathSourceProvenance").GetProperty("packages")
            .EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal(preparedPackages.Append("markdown-renderer-mermaid-native").Order(),
            inventory.RootElement.GetProperty("packages").EnumerateArray()
                .Where(item => item.GetProperty("source").ValueKind == JsonValueKind.Null)
                .Select(item => item.GetProperty("name").GetString()).Order());
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

    private static void AssertRawHash(string path, string expected)
    {
        Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
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
