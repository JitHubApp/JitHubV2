namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>A volume and file index that identifies one font file across hard links.</summary>
internal readonly struct FontFileIdentity : IEquatable<FontFileIdentity>
{
    private readonly uint _volumeSerial;
    private readonly ulong _fileIndex;

    internal FontFileIdentity(uint volumeSerial, ulong fileIndex)
    {
        _volumeSerial = volumeSerial;
        _fileIndex = fileIndex;
    }

    public bool Equals(FontFileIdentity other) =>
        _volumeSerial == other._volumeSerial && _fileIndex == other._fileIndex;

    public override bool Equals(object? obj) => obj is FontFileIdentity other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_volumeSerial, _fileIndex);
}
