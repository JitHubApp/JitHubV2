using System.Buffers.Binary;
using MarkdownRenderer.Mermaid.Internal.Scenes;

namespace MarkdownRenderer.Mermaid.Tests.TestSupport;

/// <summary>Builds minimal MMIR buffers for decoder tests.</summary>
internal static class MmirBuilder
{
    internal readonly record struct Section(uint Kind, int Count, int Stride, byte[] Payload);

    internal static Section Viewport
    {
        get
        {
            var payload = new byte[16];
            WriteSingle(payload, 0, 0);
            WriteSingle(payload, 4, 0);
            WriteSingle(payload, 8, 640);
            WriteSingle(payload, 12, 480);
            return new Section(1, 1, 16, payload);
        }
    }

    internal static Section EmptyStrings => new(2, 0, 0, []);

    internal static Section Strings(params string[] values)
    {
        using var stream = new MemoryStream();
        var lengthBuffer = new byte[sizeof(uint)];
        foreach (string value in values)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteUInt32LittleEndian(lengthBuffer, checked((uint)bytes.Length));
            stream.Write(lengthBuffer);
            stream.Write(bytes);
            while ((stream.Length & 3) != 0)
                stream.WriteByte(0);
        }
        return new Section(2, values.Length, 0, stream.ToArray());
    }

    internal static Section Geometry(params float[] values)
    {
        var payload = new byte[checked(values.Length * sizeof(float))];
        for (int i = 0; i < values.Length; i++)
            WriteSingle(payload, i * sizeof(float), values[i]);
        return new Section(4, values.Length, sizeof(float), payload);
    }

    internal static byte[] TextCommand(uint fontFamilyIndex, MermaidTextFlags textFlags)
    {
        var command = new byte[32];
        BinaryPrimitives.WriteUInt16LittleEndian(command.AsSpan(0), (ushort)MermaidDrawOpcode.Text);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(4), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(8), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(12), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(20), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(24), fontFamilyIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(command.AsSpan(28), (uint)textFlags);
        return command;
    }

    internal static byte[] CreateMinimal(params Section[] additionalSections) =>
        Create([Viewport, EmptyStrings, new Section(5, 0, 32, []), .. additionalSections]);

    internal static byte[] Create(params Section[] sections)
        => Create(0, sections);

    internal static byte[] Create(ushort minor, params Section[] sections)
    {
        const int headerSize = 32;
        const int descriptorSize = 20;
        int directorySize = checked(sections.Length * descriptorSize);
        int cursor = Align4(headerSize + directorySize);
        var offsets = new int[sections.Length];
        for (int i = 0; i < sections.Length; i++)
        {
            offsets[i] = cursor;
            cursor = Align4(checked(cursor + sections[i].Payload.Length));
        }

        var result = new byte[cursor];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0), 0x52494D4D);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), minor);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), checked((uint)result.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20), checked((uint)sections.Length));

        for (int i = 0; i < sections.Length; i++)
        {
            int descriptor = headerSize + (i * descriptorSize);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(descriptor), sections[i].Kind);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(descriptor + 4), checked((uint)offsets[i]));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(descriptor + 8), checked((uint)sections[i].Payload.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(descriptor + 12), checked((uint)sections[i].Count));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(descriptor + 16), checked((uint)sections[i].Stride));
            sections[i].Payload.CopyTo(result, offsets[i]);
        }

        return result;
    }

    internal static int GetSectionOffset(byte[] buffer, uint kind)
    {
        int descriptor = GetDescriptorOffset(buffer, kind);
        return checked((int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(descriptor + 4)));
    }

    internal static int GetDescriptorOffset(byte[] buffer, uint kind)
    {
        int directoryOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16)));
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(20)));
        for (int i = 0; i < count; i++)
        {
            int descriptor = directoryOffset + (i * 20);
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(descriptor)) == kind)
            {
                return descriptor;
            }
        }

        throw new InvalidOperationException("Section was not found.");
    }

    private static int Align4(int value) => checked((value + 3) & ~3);

    private static void WriteSingle(byte[] target, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(offset), BitConverter.SingleToInt32Bits(value));
}
