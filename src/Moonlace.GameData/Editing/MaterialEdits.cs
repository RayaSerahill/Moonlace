using Moonlace.GameData.Parsing;

namespace Moonlace.GameData.Editing;

/// <summary>
/// Structural material edits on top of <see cref="MtrlDocument"/>: adding a
/// diffuse texture slot and switching the shader pack. The constants come
/// from scanning the game's own materials (5,380 equipment mtrls): every
/// diffuse texture is bound through sampler 0x115306BE, and every
/// character/characterlegacy material that has one also sets the texture
/// mode key 0xB616DC5A to 0x600EF9DF, without which the shader ignores it.
/// </summary>
public static class MaterialEdits
{
    /// <summary>g_SamplerDiffuse.</summary>
    public const uint SamplerDiffuseId = 0x115306BE;

    /// <summary>The sampler settings the game uses most for diffuse textures.</summary>
    public const uint DiffuseSamplerSettings = 0x000F8340;

    /// <summary>Texture mode shader key; <see cref="TextureModeWithDiffuse"/> makes the shader read the diffuse map.</summary>
    public const uint TextureModeKey = 0xB616DC5A;

    public const uint TextureModeWithDiffuse = 0x600EF9DF;

    /// <summary>Shader packs that take the texture mode key (verified against game data).</summary>
    private static readonly HashSet<string> TextureModeShaders = new(StringComparer.Ordinal)
    {
        "character.shpk",
        "characterlegacy.shpk",
    };

    /// <summary>Shader packs offered in the Material tab; any other name can still be typed.</summary>
    public static IReadOnlyList<string> KnownShaders { get; } =
    [
        "character.shpk",
        "characterlegacy.shpk",
        "characterglass.shpk",
        "charactertransparency.shpk",
        "characterstockings.shpk",
        "characterinc.shpk",
        "characterscroll.shpk",
        "skin.shpk",
        "hair.shpk",
        "iris.shpk",
    ];

    public static bool HasDiffuseSlot(MtrlDocument doc) =>
        doc.Samplers.Any(s => s.SamplerId == SamplerDiffuseId);

    /// <summary>
    /// Adds a diffuse texture slot pointing at <paramref name="texturePath"/>
    /// (which may not exist yet: import an image to it afterwards). Throws
    /// when the material already has one.
    /// </summary>
    public static byte[] AddDiffuseSlot(byte[] mtrl, string texturePath)
    {
        var doc = MtrlDocument.Parse(mtrl);
        if (HasDiffuseSlot(doc))
            throw new InvalidOperationException("This material already has a diffuse slot.");
        if (doc.Textures.Count >= byte.MaxValue)
            throw new InvalidOperationException("This material cannot hold another texture.");

        doc.Textures.Add(new MtrlDocument.MtrlTextureRef { Path = texturePath });
        doc.Samplers.Add(new MtrlDocument.MtrlSampler
        {
            SamplerId = SamplerDiffuseId,
            Settings = DiffuseSamplerSettings,
            TextureIndex = (byte)(doc.Textures.Count - 1),
        });

        if (TextureModeShaders.Contains(doc.ShaderPack))
        {
            var index = doc.ShaderKeys.FindIndex(k => k.Category == TextureModeKey);
            var key = new MtrlDocument.MtrlShaderKey { Category = TextureModeKey, Value = TextureModeWithDiffuse };
            if (index >= 0)
                doc.ShaderKeys[index] = key;
            else
                doc.ShaderKeys.Add(key);
        }

        return doc.Write();
    }

    /// <summary>
    /// Switches the material's shader pack. Keys, constants, samplers and
    /// textures stay as they are, so the user decides whether they fit the
    /// new shader. One conversion is known to need more: characterlegacy →
    /// character renders the mesh pitch black unless every color table row
    /// has field 3 = 1, field 7 = 0 and field 11 = 1 (xivmodding.com
    /// "Updating a mod to DT shaders"; TexTools calls them the diffuse,
    /// specular and emissive "unknown" values), so that switch sets them.
    /// </summary>
    public static byte[] SetShader(byte[] mtrl, string shaderPack)
    {
        var doc = MtrlDocument.Parse(mtrl);
        if (doc.ShaderPack == "characterlegacy.shpk" && shaderPack == "character.shpk")
            ApplyCharacterShaderRowFields(doc);
        doc.ShaderPack = shaderPack;
        return doc.Write();
    }

    /// <summary>Rows × bytes of a Dawntrail color table (32 rows of 32 halfs); legacy 16-row tables are left alone.</summary>
    private const int DawntrailRows = 32;

    private const int DawntrailRowBytes = 64;

    /// <summary>
    /// Sets field 3 = 1, field 7 = 0 and field 11 = 1 on every row of a
    /// Dawntrail color table, what character.shpk needs to not render black.
    /// Returns false (and changes nothing) for materials without one.
    /// </summary>
    public static bool ApplyCharacterShaderRowFields(MtrlDocument doc)
    {
        if (doc.DataSet.Length < DawntrailRows * DawntrailRowBytes)
            return false;
        for (var row = 0; row < DawntrailRows; row++)
        {
            var at = row * DawntrailRowBytes;
            WriteHalf(doc.DataSet, at + 3 * 2, 1f);
            WriteHalf(doc.DataSet, at + 7 * 2, 0f);
            WriteHalf(doc.DataSet, at + 11 * 2, 1f);
        }

        return true;
    }

    private static void WriteHalf(byte[] buffer, int offset, float value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteHalfLittleEndian(buffer.AsSpan(offset), (Half)value);

    /// <summary>
    /// A sensible path for a new diffuse texture: the normal map's path with
    /// its role swapped ("_n" → "_d", "_norm" → "_base", any TexTools hash
    /// dropped), else one named after the material next to its first texture.
    /// </summary>
    public static string SuggestDiffusePath(IReadOnlyList<string> texturePaths, string mtrlPath)
    {
        foreach (var path in texturePaths)
        {
            if (TextureRoles.Classify(path) != TextureRole.Normal)
                continue;
            var directory = path[..(path.LastIndexOf('/') + 1)];
            var segments = Path.GetFileNameWithoutExtension(path).Split('_').ToList();
            while (segments.Count > 1 && segments[^1].All(char.IsAsciiDigit))
                segments.RemoveAt(segments.Count - 1);
            segments[^1] = segments[^1] == "norm" ? "base" : "d";
            return directory + string.Join('_', segments) + ".tex";
        }

        var stem = Path.GetFileNameWithoutExtension(mtrlPath);
        if (stem.StartsWith("mt_", StringComparison.Ordinal))
            stem = stem[3..];
        var dir = texturePaths.FirstOrDefault(p => p.Contains('/')) is { } first
            ? first[..(first.LastIndexOf('/') + 1)]
            : mtrlPath[..(mtrlPath.LastIndexOf("/material/", StringComparison.Ordinal) + 1)] + "texture/";
        return $"{dir}{stem}_d.tex";
    }
}
