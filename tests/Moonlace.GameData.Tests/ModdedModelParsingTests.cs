using Moonlace.GameData.Parsing;

namespace Moonlace.GameData.Tests;

/// <summary>
/// Parsing of TexTools-written models, which differ from game-shipped ones:
/// they declare second vertex channels (UV2, vertex color 2) as separate
/// elements of the same usage with usage index 1 instead of packing UV2 into
/// the Z/W of a Half4/Single4 element.
/// </summary>
public sealed class ModdedModelParsingTests
{
    private const string ModdedModel =
        "/mnt/games/penumbra_mods_dt/Marshmallow Shoes Only/items/chara/equipment/e6080/model/c0201e6080_sho.mdl";

    [SkippableFact]
    public void SecondUvChannelDoesNotOverwriteTheFirst()
    {
        Skip.IfNot(File.Exists(ModdedModel));

        var parsed = MdlParser.Parse(File.ReadAllBytes(ModdedModel));

        // This model declares Uv (Single4, usage index 0) and a second
        // Uv (Single2, usage index 1) whose value is a constant. Before the
        // usage-index check, the constant overwrote every real UV and the
        // whole model sampled one texel of each texture. Small meshes may
        // legitimately map to a tiny UV patch (flat placeholder textures),
        // so only the main mesh's UV island spread is asserted.
        var mesh = parsed.Meshes.MaxBy(m => m.Vertices.Length)!;
        var us = mesh.Vertices.Select(v => v.Uv.X).ToArray();
        var vs = mesh.Vertices.Select(v => v.Uv.Y).ToArray();
        Assert.True(us.Max() - us.Min() > 0.1f, $"{mesh.MaterialName}: U coordinates are constant");
        Assert.True(vs.Max() - vs.Min() > 0.1f, $"{mesh.MaterialName}: V coordinates are constant");
    }
}
