using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class WorkerImportBoundaryTests
{
    [Fact]
    public void DeployedWorkerImportsOnlyAuditedHeadlessSystemLibraries()
    {
        // A direct COM import brought USER32/GDI32 into the loader before main,
        // causing STATUS_DLL_INIT_FAILED under the required Win32k lockdown.
        // Delay-loading the same dependency is not an acceptable workaround.
        // New system dependencies require explicit transitive-import review,
        // followed by the actual restricted-worker tests on every target RID.
        string path = Path.Combine(AppContext.BaseDirectory, "MarkdownRenderer.Svg.Resvg.Worker.exe");
        using var stream = File.OpenRead(path);
        using var image = new PEReader(stream);
        PEHeader header = Assert.IsType<PEHeader>(image.PEHeaders.PEHeader);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "KERNEL32.dll", "ntdll.dll", "bcryptprimitives.dll", "api-ms-win-core-synch-l1-2-0.dll",
        };
        string[] imports = ReadImports(image, header.ImportTableDirectory, delayed: false)
            .Concat(ReadImports(image, header.DelayImportTableDirectory, delayed: true)).ToArray();
        Assert.NotEmpty(imports);
        Assert.All(imports, name => Assert.True(allowed.Contains(name),
            $"The restricted worker imports an unaudited system library: {name}"));
    }

    private static IEnumerable<string> ReadImports(PEReader image, DirectoryEntry directory, bool delayed)
    {
        if (directory.RelativeVirtualAddress == 0)
        {
            Assert.Equal(0, directory.Size);
            yield break;
        }

        int descriptorSize = delayed ? 32 : 20;
        Assert.True(directory.Size >= descriptorSize, "The import directory is truncated.");
        byte[] descriptors = image.GetSectionData(directory.RelativeVirtualAddress)
            .GetContent(0, directory.Size).ToArray();
        for (int offset = 0; offset <= descriptors.Length - descriptorSize; offset += descriptorSize)
        {
            if (descriptors.AsSpan(offset, descriptorSize).IndexOfAnyExcept((byte)0) == -1)
                yield break;
            if (delayed)
                Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(descriptors.AsSpan(offset)));
            int nameRva = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                descriptors.AsSpan(offset + (delayed ? 4 : 12))));
            Assert.True(nameRva > 0, "An import has no library name.");
            var nameSection = image.GetSectionData(nameRva);
            byte[] nameBytes = nameSection.GetContent(0, Math.Min(260, nameSection.Length)).ToArray();
            int terminator = Array.IndexOf(nameBytes, (byte)0);
            Assert.True(terminator > 0, "An import library name is missing or too long.");
            yield return Encoding.ASCII.GetString(nameBytes, 0, terminator);
        }
        Assert.Fail("The import directory has no terminating descriptor.");
    }
}
