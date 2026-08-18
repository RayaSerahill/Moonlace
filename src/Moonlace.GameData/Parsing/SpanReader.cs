using System.Buffers.Binary;

namespace Moonlace.GameData.Parsing;

/// <summary>Minimal little-endian forward reader over a byte span.</summary>
internal ref struct SpanReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;

    public int Position { get; set; }

    public readonly int Length => _data.Length;

    public byte ReadByte() => _data[Position++];

    public ushort ReadUInt16()
    {
        var v = BinaryPrimitives.ReadUInt16LittleEndian(_data[Position..]);
        Position += 2;
        return v;
    }

    public uint ReadUInt32()
    {
        var v = BinaryPrimitives.ReadUInt32LittleEndian(_data[Position..]);
        Position += 4;
        return v;
    }

    public float ReadSingle()
    {
        var v = BinaryPrimitives.ReadSingleLittleEndian(_data[Position..]);
        Position += 4;
        return v;
    }

    public float ReadHalf()
    {
        var v = (float)BinaryPrimitives.ReadHalfLittleEndian(_data[Position..]);
        Position += 2;
        return v;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        var v = _data.Slice(Position, count);
        Position += count;
        return v;
    }

    public uint[] ReadUInt32Array(int count)
    {
        var result = new uint[count];
        for (var i = 0; i < count; i++)
            result[i] = ReadUInt32();
        return result;
    }

    public void Skip(int count) => Position += count;
}
