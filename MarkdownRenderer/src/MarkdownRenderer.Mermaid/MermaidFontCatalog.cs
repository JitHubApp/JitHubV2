using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace MarkdownRenderer.Mermaid;

/// <summary>
/// An immutable ordered catalog of DirectWrite family names used for native Mermaid label
/// measurement. The catalog is transferred once when a renderer creates its native engine.
/// </summary>
public sealed class MermaidFontCatalog
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ReadOnlyCollection<string> _families;

    public MermaidFontCatalog(IEnumerable<string> families)
    {
        ArgumentNullException.ThrowIfNull(families);
        string[] values = families.Select(static family =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(family);
            string normalized = family.Trim();
            if (normalized.Length > 256 || normalized.Any(char.IsControl))
            {
                throw new ArgumentException("A Mermaid font family is invalid.", nameof(families));
            }

            return normalized;
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (values is not { Length: > 0 and <= 64 })
        {
            throw new ArgumentException("A Mermaid font catalog must contain between 1 and 64 families.", nameof(families));
        }

        _families = Array.AsReadOnly(values);
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(Serialize()));
    }

    public static MermaidFontCatalog Default { get; } = new(["Segoe UI"]);

    public IReadOnlyList<string> Families => _families;

    /// <summary>A stable lowercase SHA-256 fingerprint suitable for scene-cache keys.</summary>
    public string Fingerprint { get; }

    internal byte[] Serialize()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true);
        writer.Write("FNTC"u8);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write(checked((uint)_families.Count));
        foreach (string family in _families)
        {
            byte[] bytes = StrictUtf8.GetBytes(family);
            writer.Write(checked((uint)bytes.Length));
            writer.Write(bytes);
            while ((stream.Length & 3) != 0)
            {
                writer.Write((byte)0);
            }
        }

        return stream.ToArray();
    }
}
