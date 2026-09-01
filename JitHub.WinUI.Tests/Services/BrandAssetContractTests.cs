using System.Buffers.Binary;
using System.Security.Cryptography;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class BrandAssetContractTests
{
    private const string CanonicalLogoHash = "cf5aefc31527bf5cf3074c2ac001b35d70f3c72e37c40bb7165b9be0504b7296";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] IendChunkType = [(byte)'I', (byte)'E', (byte)'N', (byte)'D'];

    // SHA-256 values are pinned to revision 3d25c9ae953e5fb1392af3d844fe2be0b5596304.
    private static readonly IReadOnlyDictionary<string, string> PackageAssetHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LargeTile.scale-100.png"] = "58b038f64b70fa2f486184a3678c3e1143b62321d023687cafd332fd7af8524e",
            ["LargeTile.scale-125.png"] = "4226c0000a24a87e9a300840ccd4f062807bc00976761451f5ba5d08a35fef26",
            ["LargeTile.scale-150.png"] = "80fbea6743f9c05f4d34e12725fb57427c5b14b3f20ca50b995cff3083e44914",
            ["LargeTile.scale-200.png"] = "e75aabd23bff7ccc92c0505baec309cb021701f6f52932a94f3b14b8fa5b3edb",
            ["LargeTile.scale-400.png"] = "ec1e052205ee8a6cf98f14858466b4cd07f323b3792c095d2805e2c67c15931f",
            ["LockScreenLogo.scale-200.png"] = "a41a053b3fc3b0c109720ccd437a19725ae9163ea75990222a12b596b9c7ca76",
            ["SmallTile.scale-100.png"] = "753eb660b993caa01bd34bddfb5424cd29fa630e6adb3734a1cd5fac65292ab2",
            ["SmallTile.scale-125.png"] = "4f9692d038a000b364d10adeed5407ec95cb56898a406c9f02645dd6591ade67",
            ["SmallTile.scale-150.png"] = "efe220a1725e7e31e5eddfbf4af560bec1eb2f7080d2dd7b3d3ebdbfbd1297ba",
            ["SmallTile.scale-200.png"] = "27c84825e124a4278bcbee7f1ba726a642443984f7ebfc2caace1e535a4a6fc4",
            ["SmallTile.scale-400.png"] = "6ef1d8ea924e57f250b77670af5b544206203c53b1467c6c6f319f90ceff3823",
            ["SplashScreen.scale-100.png"] = "d13a72975cc5c9a13dadb29305488f24e418e40ecd93d13b2a04e66f3606b7d7",
            ["SplashScreen.scale-125.png"] = "bb586b72e12f6e13ce91d6d5f950f299ea1b688e9e8eed9f08ebd974b6af05f1",
            ["SplashScreen.scale-150.png"] = "12602aae4a20f70a66ee9dca25037b8071f0c7ee57d0a2a950867aca5dd96f0e",
            ["SplashScreen.scale-200.png"] = "ad35c8911970f97182ec381b16670d228935a87e639f9cd52178458d52caa41c",
            ["SplashScreen.scale-400.png"] = "fca70f04176809717e5708192867fc0d25ac964e4046f15fdebd2b75d399c3f5",
            ["Square150x150Logo.scale-100.png"] = "5988cfdd5bde12554c4e2939addbc10b62a042168eeceb59a7678882cb974dd0",
            ["Square150x150Logo.scale-125.png"] = "810418266bbadb8e3dc0cf87bcf7300c8882a16029cdf84d71c30a932c708497",
            ["Square150x150Logo.scale-150.png"] = "ffe94770defb5936475566a9fdc3cdb8badea7891a1b5cb09e05c8d4d128fb45",
            ["Square150x150Logo.scale-200.png"] = "4fe458df45a83b99fa13256c47f7b43307f783d666298af6d42d690921d2c322",
            ["Square150x150Logo.scale-400.png"] = "2e9f1f448b87fa855137acb36c8fb382487c651445786f16c1a186b90c4fdde6",
            ["Square44x44Logo.altform-lightunplated_targetsize-16.png"] = "bbb60a311c192d88a8199443fe801a730ace832313bcb78ac769e8e8a0d5e58f",
            ["Square44x44Logo.altform-lightunplated_targetsize-24.png"] = "d309c8997fa323440d7f54a430c0b82dfde2156000732821bce84ba3017e8470",
            ["Square44x44Logo.altform-lightunplated_targetsize-256.png"] = "72348e63509e64a83ed7ddfdf884a6ea26810f63ac97fdc40b6aaf51b76c1504",
            ["Square44x44Logo.altform-lightunplated_targetsize-32.png"] = "4b3287b4af7f47d3889e9febdef3807553035b45c6ca61782d1db513f9917662",
            ["Square44x44Logo.altform-lightunplated_targetsize-48.png"] = "07ae5e6bab0f69d7dec5d72394e6d57976b73025c469e7cf3fc13092eaf0114f",
            ["Square44x44Logo.altform-unplated_targetsize-16.png"] = "bbb60a311c192d88a8199443fe801a730ace832313bcb78ac769e8e8a0d5e58f",
            ["Square44x44Logo.altform-unplated_targetsize-256.png"] = "72348e63509e64a83ed7ddfdf884a6ea26810f63ac97fdc40b6aaf51b76c1504",
            ["Square44x44Logo.altform-unplated_targetsize-32.png"] = "4b3287b4af7f47d3889e9febdef3807553035b45c6ca61782d1db513f9917662",
            ["Square44x44Logo.altform-unplated_targetsize-48.png"] = "07ae5e6bab0f69d7dec5d72394e6d57976b73025c469e7cf3fc13092eaf0114f",
            ["Square44x44Logo.scale-100.png"] = "70697b3d0ac3debdef848fbb4ad6f7df839edf759d8ae9d398d8bd66b78aeff8",
            ["Square44x44Logo.scale-125.png"] = "abf46a67f7cbac85e12adfd61d19714c865be1e3fa2d5e9b372af05726f01bde",
            ["Square44x44Logo.scale-150.png"] = "b85fca4432f53568b556b45d12690c7fcfe28d7d739aadd6d8aa80d235fbd83b",
            ["Square44x44Logo.scale-200.png"] = "543739c22adfe85d5b363cd3dc07fdf73214b0597de41c29a5218922963342f9",
            ["Square44x44Logo.scale-400.png"] = "7ca41453685b07c71e96150dd048b8ab2751a472926e28cf840045559a351f70",
            ["Square44x44Logo.targetsize-16.png"] = "ec2b1513a7455e95e7e3fa8726b7e016196ff58be523b6937bc565928f6ce586",
            ["Square44x44Logo.targetsize-24.png"] = "72ea55150ad4efd516f5392915d1b22b6ea639cf86c00ef534ad7d444e7ea41d",
            ["Square44x44Logo.targetsize-24_altform-unplated.png"] = "d309c8997fa323440d7f54a430c0b82dfde2156000732821bce84ba3017e8470",
            ["Square44x44Logo.targetsize-256.png"] = "613dd0b23bcaeb42beffec6d47b0748164c362d196e7e922fc14c43216c49ca6",
            ["Square44x44Logo.targetsize-32.png"] = "9b87f3ce6cc52f4e7d58a7c33ff920e435b8447642569bfb27c4f58be39fe62d",
            ["Square44x44Logo.targetsize-48.png"] = "3440123a2ae02a6315021ccc95d788e9218ea482e2dfdf681be7deeaa58c3474",
            ["StoreLogo.scale-100.png"] = "4687f5d1f43e5caa446e8b32a02ad22d8fa5e335ffa8afc1609d6151331d693d",
            ["StoreLogo.scale-125.png"] = "6eda6dd80d4805036fc2d13b8035380a028b06580608d81a94f0b9bfcce96b41",
            ["StoreLogo.scale-150.png"] = "054116f1f87f6fddcc08d8f5cc375d4b5a2855f5ff32100d04631562b609a5e2",
            ["StoreLogo.scale-200.png"] = "5ce3c758da8e1c2583854d969c50f30db7ba1228c1396198b0d02f4109648fa7",
            ["StoreLogo.scale-400.png"] = "2236ef64269e182e213cdedf5d1e674de73c132e07aa761731df3d265326ab24",
            ["Wide310x150Logo.scale-100.png"] = "0e0e1af6707f590c38ba9cbb1253503f185ab8b83170fc2121482a2400f22802",
            ["Wide310x150Logo.scale-125.png"] = "da29b7157c3be78e7f6a18c64ee0161258af503c5e5ab98d89bf89e0635cfa65",
            ["Wide310x150Logo.scale-150.png"] = "69179b5d7f86e7e6f9fc5adff2dc6512ae1f81875540e727e35a0dea2cb81011",
            ["Wide310x150Logo.scale-200.png"] = "d13a72975cc5c9a13dadb29305488f24e418e40ecd93d13b2a04e66f3606b7d7",
            ["Wide310x150Logo.scale-400.png"] = "ad35c8911970f97182ec381b16670d228935a87e639f9cd52178458d52caa41c",
        };

    [Fact]
    public void CanonicalAppAndWebsiteAssets_MatchThePreMigrationBrand()
    {
        string root = FindRepositoryRoot();
        (string Path, string Hash)[] assets =
        [
            (Path.Combine(root, "JitHub.WinUI", "Assets", "JitHubLogo.png"), CanonicalLogoHash),
            (Path.Combine(root, "JitHub.WinUI", "Assets", "JitHubLogoTitleBar.png"), CanonicalLogoHash),
            (Path.Combine(root, "JitHub.Web", "wwwroot", "JitHubLogo.png"), CanonicalLogoHash),
            (Path.Combine(root, "JitHub.Web", "wwwroot", "favicon.png"), "e265ac0f2dda1e5dfa65b1adf330722bb3ef7789115283604d8cd19f098f1f08"),
            (Path.Combine(root, "JitHub.Web", "wwwroot", "icon-192.png"), "0dba506aaebc6526f92283e8b0112b33541605fb1b4f1a49aa15344448bac0fe"),
        ];

        Assert.All(assets, asset => AssertSha256(asset.Path, asset.Hash));
    }

    [Fact]
    public void PackageAssets_MatchTheCompletePreMigrationSet()
    {
        string assetsRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Assets");
        string[] actualAssetNames = Directory
            .EnumerateFiles(assetsRoot, "*.png", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(static name => name is not null && IsPackageAsset(name))
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expectedAssetNames = PackageAssetHashes.Keys.Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(51, PackageAssetHashes.Count);
        Assert.Equal(expectedAssetNames, actualAssetNames);
        foreach ((string fileName, string expectedHash) in PackageAssetHashes)
        {
            AssertSha256(Path.Combine(assetsRoot, fileName), expectedHash);
        }
    }

    [Fact]
    public void AppIcon_ContainsTheCanonicalPngFrameSet()
    {
        string assetsRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Assets");
        IReadOnlyList<IcoFrame> frames = ReadIcoFrames(Path.Combine(assetsRoot, "AppIcon.ico"));
        int[] expectedSizes = [16, 24, 32, 48, 64, 128, 256];

        Assert.Equal(expectedSizes, frames.Select(static frame => frame.Width).Order().ToArray());
        Assert.All(frames, frame =>
        {
            Assert.Equal(frame.Width, frame.Height);
            Assert.True(frame.Data.Length >= 24, $"The {frame.Width}px icon frame is too short to be a PNG.");
            Assert.True(frame.Data.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature),
                $"The {frame.Width}px icon frame does not have a PNG signature.");
            Assert.Equal(frame.Width, checked((int)BinaryPrimitives.ReadUInt32BigEndian(frame.Data.AsSpan(16, 4))));
            Assert.Equal(frame.Height, checked((int)BinaryPrimitives.ReadUInt32BigEndian(frame.Data.AsSpan(20, 4))));
            Assert.Equal(frame.Data.Length, GetPngLengthThroughIend(frame.Data));
        });

        foreach (int size in new[] { 16, 24, 32, 48, 256 })
        {
            IcoFrame frame = Assert.Single(frames, candidate => candidate.Width == size);
            byte[] expected = File.ReadAllBytes(Path.Combine(
                assetsRoot,
                $"Square44x44Logo.targetsize-{size}.png"));
            int canonicalPngLength = GetPngLengthThroughIend(expected);
            Assert.Equal(canonicalPngLength, frame.Data.Length);
            Assert.True(expected.AsSpan(0, canonicalPngLength).SequenceEqual(frame.Data),
                $"The embedded {size}px icon frame differs from its canonical target-size PNG through IEND.");
        }
    }

    [Fact]
    public void StoreLogoBaseAsset_EqualsTheCanonicalScale100Asset()
    {
        string assetsRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Assets");
        byte[] baseAsset = File.ReadAllBytes(Path.Combine(assetsRoot, "StoreLogo.png"));
        byte[] scale100Asset = File.ReadAllBytes(Path.Combine(assetsRoot, "StoreLogo.scale-100.png"));

        Assert.True(baseAsset.AsSpan().SequenceEqual(scale100Asset));
    }

    private static IReadOnlyList<IcoFrame> ReadIcoFrames(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);

        ushort reserved = reader.ReadUInt16();
        ushort imageType = reader.ReadUInt16();
        ushort imageCount = reader.ReadUInt16();
        if (reserved != 0 || imageType != 1)
        {
            throw new InvalidDataException("The app icon does not have a valid ICO header.");
        }

        var entries = new IcoDirectoryEntry[imageCount];
        for (int index = 0; index < imageCount; index++)
        {
            int width = reader.ReadByte();
            int height = reader.ReadByte();
            _ = reader.ReadByte();
            _ = reader.ReadByte();
            _ = reader.ReadUInt16();
            _ = reader.ReadUInt16();
            int byteCount = checked((int)reader.ReadUInt32());
            int imageOffset = checked((int)reader.ReadUInt32());
            entries[index] = new IcoDirectoryEntry(
                width == 0 ? 256 : width,
                height == 0 ? 256 : height,
                byteCount,
                imageOffset);
        }

        var frames = new List<IcoFrame>(imageCount);
        foreach (IcoDirectoryEntry entry in entries)
        {
            if (entry.ByteCount < 0 ||
                entry.ImageOffset < 0 ||
                (long)entry.ImageOffset + entry.ByteCount > stream.Length)
            {
                throw new InvalidDataException("An ICO directory entry points outside the app icon file.");
            }

            stream.Position = entry.ImageOffset;
            byte[] data = reader.ReadBytes(entry.ByteCount);
            if (data.Length != entry.ByteCount)
            {
                throw new EndOfStreamException("An ICO frame ended before its declared byte count.");
            }

            frames.Add(new IcoFrame(entry.Width, entry.Height, data));
        }

        return frames;
    }

    private static bool IsPackageAsset(string fileName) =>
        fileName.StartsWith("LargeTile.", StringComparison.Ordinal) ||
        fileName.StartsWith("LockScreenLogo.", StringComparison.Ordinal) ||
        fileName.StartsWith("SmallTile.", StringComparison.Ordinal) ||
        fileName.StartsWith("SplashScreen.", StringComparison.Ordinal) ||
        fileName.StartsWith("Square150x150Logo.", StringComparison.Ordinal) ||
        fileName.StartsWith("Square44x44Logo.", StringComparison.Ordinal) ||
        fileName.StartsWith("StoreLogo.scale-", StringComparison.Ordinal) ||
        fileName.StartsWith("Wide310x150Logo.", StringComparison.Ordinal);

    private static int GetPngLengthThroughIend(ReadOnlySpan<byte> png)
    {
        if (png.Length < PngSignature.Length ||
            !png[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            throw new InvalidDataException("The image does not have a PNG signature.");
        }

        int offset = PngSignature.Length;
        while (offset <= png.Length - 12)
        {
            int dataLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset, 4)));
            int chunkEnd = checked(offset + 12 + dataLength);
            if (chunkEnd > png.Length)
            {
                throw new InvalidDataException("A PNG chunk extends beyond the image payload.");
            }

            ReadOnlySpan<byte> chunkType = png.Slice(offset + 4, 4);
            if (chunkType.SequenceEqual(IendChunkType))
            {
                if (dataLength != 0)
                {
                    throw new InvalidDataException("The PNG IEND chunk must be empty.");
                }

                return chunkEnd;
            }

            offset = chunkEnd;
        }

        throw new InvalidDataException("The PNG payload does not contain an IEND chunk.");
    }

    private static void AssertSha256(string path, string expectedHash)
    {
        Assert.True(File.Exists(path), $"Required brand asset is missing: {path}");
        string actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        Assert.Equal(expectedHash, actualHash);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }

    private sealed record IcoDirectoryEntry(int Width, int Height, int ByteCount, int ImageOffset);

    private sealed record IcoFrame(int Width, int Height, byte[] Data);
}
