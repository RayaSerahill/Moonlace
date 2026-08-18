using System.Buffers.Binary;
using System.Text;

namespace Moonlace.GameData.Parsing;

/// <summary>
/// Full structural read/write of a .mtrl file — every section, not just the
/// parts the viewer needs. Exists for transformations that resize sections
/// (the Dawntrail upgrade adds a texture+sampler and grows the color set),
/// which byte-patching cannot do. Layout follows Lumina's MtrlStructs,
/// cross-verified against real game files by the tests.
/// </summary>
public sealed class MtrlDocument
{
    public uint Version;
    public string ShaderPack = "";
    public List<MtrlTextureRef> Textures = [];
    public List<MtrlNamedSet> UvSets = [];
    public List<MtrlNamedSet> ColorSets = [];
    public byte[] AdditionalData = [];

    /// <summary>The color table (+ optional dye block): 512/544 legacy, 2048/2176 Dawntrail.</summary>
    public byte[] DataSet = [];

    public List<MtrlShaderKey> ShaderKeys = [];
    public List<MtrlConstant> Constants = [];
    public List<MtrlSampler> Samplers = [];
    public ushort Flags1;
    public ushort Flags2;
    public byte[] ShaderValues = [];

    public sealed class MtrlTextureRef
    {
        public string Path = "";
        public ushort Flags;
    }

    public sealed class MtrlNamedSet
    {
        public string Name = "";
        public byte Index;
        public byte Unknown;
    }

    public struct MtrlShaderKey
    {
        public uint Category;
        public uint Value;
    }

    public struct MtrlConstant
    {
        public uint Id;
        public ushort Offset;
        public ushort Size;
    }

    public sealed class MtrlSampler
    {
        public uint SamplerId;
        public uint Settings;
        public byte TextureIndex;
        public byte Padding0;
        public byte Padding1;
        public byte Padding2;
    }

    public static MtrlDocument Parse(byte[] data)
    {
        var doc = new MtrlDocument();
        var r = new SpanReader(data);

        doc.Version = r.ReadUInt32();
        var fileAndDataSetSize = r.ReadUInt32();
        var dataSetSize = (int)(fileAndDataSetSize >> 16);
        int stringTableSize = r.ReadUInt16();
        int shaderNameOffset = r.ReadUInt16();
        int textureCount = r.ReadByte();
        int uvSetCount = r.ReadByte();
        int colorSetCount = r.ReadByte();
        int additionalDataSize = r.ReadByte();

        var textureOffsets = new (int Offset, ushort Flags)[textureCount];
        for (var i = 0; i < textureCount; i++)
        {
            var entry = r.ReadUInt32();
            textureOffsets[i] = ((ushort)entry, (ushort)(entry >> 16));
        }

        var uvSetOffsets = new (int Offset, byte Index, byte Unknown)[uvSetCount];
        for (var i = 0; i < uvSetCount; i++)
            uvSetOffsets[i] = (r.ReadUInt16(), r.ReadByte(), r.ReadByte());

        var colorSetOffsets = new (int Offset, byte Index, byte Unknown)[colorSetCount];
        for (var i = 0; i < colorSetCount; i++)
            colorSetOffsets[i] = (r.ReadUInt16(), r.ReadByte(), r.ReadByte());

        var strings = r.ReadBytes(stringTableSize).ToArray();
        doc.AdditionalData = r.ReadBytes(additionalDataSize).ToArray();
        doc.DataSet = r.ReadBytes(dataSetSize).ToArray();

        int shaderValuesSize = r.ReadUInt16();
        int keyCount = r.ReadUInt16();
        int constantCount = r.ReadUInt16();
        int samplerCount = r.ReadUInt16();
        doc.Flags1 = r.ReadUInt16();
        doc.Flags2 = r.ReadUInt16();

        for (var i = 0; i < keyCount; i++)
            doc.ShaderKeys.Add(new MtrlShaderKey { Category = r.ReadUInt32(), Value = r.ReadUInt32() });
        for (var i = 0; i < constantCount; i++)
            doc.Constants.Add(new MtrlConstant { Id = r.ReadUInt32(), Offset = r.ReadUInt16(), Size = r.ReadUInt16() });
        for (var i = 0; i < samplerCount; i++)
        {
            doc.Samplers.Add(new MtrlSampler
            {
                SamplerId = r.ReadUInt32(),
                Settings = r.ReadUInt32(),
                TextureIndex = r.ReadByte(),
                Padding0 = r.ReadByte(),
                Padding1 = r.ReadByte(),
                Padding2 = r.ReadByte(),
            });
        }

        doc.ShaderValues = r.ReadBytes(shaderValuesSize).ToArray();

        doc.ShaderPack = ReadCString(strings, shaderNameOffset);
        foreach (var (offset, flags) in textureOffsets)
            doc.Textures.Add(new MtrlTextureRef { Path = ReadCString(strings, offset), Flags = flags });
        foreach (var (offset, index, unknown) in uvSetOffsets)
            doc.UvSets.Add(new MtrlNamedSet { Name = ReadCString(strings, offset), Index = index, Unknown = unknown });
        foreach (var (offset, index, unknown) in colorSetOffsets)
            doc.ColorSets.Add(new MtrlNamedSet { Name = ReadCString(strings, offset), Index = index, Unknown = unknown });

        return doc;
    }

    public byte[] Write()
    {
        // String table: textures, uv sets, color sets, shader — same order the
        // existing writer uses, deduplicated.
        var table = new MemoryStream();
        var offsetOf = new Dictionary<string, ushort>(StringComparer.Ordinal);
        ushort Put(string value)
        {
            if (offsetOf.TryGetValue(value, out var existing))
                return existing;
            var offset = checked((ushort)table.Position);
            var bytes = Encoding.UTF8.GetBytes(value);
            table.Write(bytes);
            table.WriteByte(0);
            offsetOf[value] = offset;
            return offset;
        }

        var textureOffsets = Textures.Select(t => Put(t.Path)).ToArray();
        var uvOffsets = UvSets.Select(s => Put(s.Name)).ToArray();
        var colorOffsets = ColorSets.Select(s => Put(s.Name)).ToArray();
        var shaderOffset = Put(ShaderPack);
        while (table.Position % 4 != 0)
            table.WriteByte(0);
        var strings = table.ToArray();

        var result = new MemoryStream();
        void U32(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            result.Write(b);
        }

        void U16(ushort value)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, value);
            result.Write(b);
        }

        var fileSize = 16
            + Textures.Count * 4 + UvSets.Count * 4 + ColorSets.Count * 4
            + strings.Length + AdditionalData.Length + DataSet.Length
            + 12 // section header: values size, 3 counts, 2 flag words
            + ShaderKeys.Count * 8 + Constants.Count * 8 + Samplers.Count * 12
            + ShaderValues.Length;

        U32(Version);
        U32((uint)((ushort)fileSize | ((uint)DataSet.Length << 16)));
        U16(checked((ushort)strings.Length));
        U16(shaderOffset);
        result.WriteByte(checked((byte)Textures.Count));
        result.WriteByte(checked((byte)UvSets.Count));
        result.WriteByte(checked((byte)ColorSets.Count));
        result.WriteByte(checked((byte)AdditionalData.Length));

        for (var i = 0; i < Textures.Count; i++)
            U32((uint)(textureOffsets[i] | ((uint)Textures[i].Flags << 16)));
        for (var i = 0; i < UvSets.Count; i++)
        {
            U16(uvOffsets[i]);
            result.WriteByte(UvSets[i].Index);
            result.WriteByte(UvSets[i].Unknown);
        }

        for (var i = 0; i < ColorSets.Count; i++)
        {
            U16(colorOffsets[i]);
            result.WriteByte(ColorSets[i].Index);
            result.WriteByte(ColorSets[i].Unknown);
        }

        result.Write(strings);
        result.Write(AdditionalData);
        result.Write(DataSet);

        U16(checked((ushort)ShaderValues.Length));
        U16(checked((ushort)ShaderKeys.Count));
        U16(checked((ushort)Constants.Count));
        U16(checked((ushort)Samplers.Count));
        U16(Flags1);
        U16(Flags2);

        foreach (var key in ShaderKeys)
        {
            U32(key.Category);
            U32(key.Value);
        }

        foreach (var constant in Constants)
        {
            U32(constant.Id);
            U16(constant.Offset);
            U16(constant.Size);
        }

        foreach (var sampler in Samplers)
        {
            U32(sampler.SamplerId);
            U32(sampler.Settings);
            result.WriteByte(sampler.TextureIndex);
            result.WriteByte(sampler.Padding0);
            result.WriteByte(sampler.Padding1);
            result.WriteByte(sampler.Padding2);
        }

        result.Write(ShaderValues);
        return result.ToArray();
    }

    private static string ReadCString(byte[] strings, int offset)
    {
        if (offset < 0 || offset >= strings.Length)
            return "";
        var end = Array.IndexOf(strings, (byte)0, offset);
        if (end < 0)
            end = strings.Length;
        return Encoding.UTF8.GetString(strings, offset, end - offset);
    }
}
