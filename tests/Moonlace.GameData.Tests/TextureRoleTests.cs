namespace Moonlace.GameData.Tests;

/// <summary>
/// Texture role classification from file names — including the hashed names
/// TexTools-made mods use (role suffix followed by a numeric hash), which
/// once rendered mods gray because every texture was classified as Other.
/// </summary>
public sealed class TextureRoleTests
{
    [Theory]
    // Plain game-style names.
    [InlineData("chara/equipment/e6080/texture/v01_c0201e6080_sho_n.tex", TextureRole.Normal)]
    [InlineData("chara/equipment/e6080/texture/v01_c0201e6080_sho_m.tex", TextureRole.Mask)]
    [InlineData("chara/equipment/e6080/texture/v01_c0201e6080_sho_id.tex", TextureRole.Index)]
    [InlineData("chara/equipment/e0100/texture/v01_c0101e0100_top_d.tex", TextureRole.Diffuse)]
    [InlineData("chara/equipment/e0100/texture/v01_c0101e0100_top_base.tex", TextureRole.Diffuse)]
    [InlineData("chara/equipment/e0100/texture/v01_c0101e0100_top_norm.tex", TextureRole.Normal)]
    [InlineData("chara/equipment/e0100/texture/v01_c0101e0100_top_mask.tex", TextureRole.Mask)]
    [InlineData("chara/equipment/e0100/texture/v01_c0101e0100_top_index.tex", TextureRole.Index)]
    [InlineData("chara/equipment/e0100/texture/v01_c0101e0100_top_s.tex", TextureRole.Specular)]
    [InlineData("chara/equipment/e0100/texture/v01_c0101e0100_top_spec.tex", TextureRole.Specular)]
    // TexTools hashed names: role suffix followed by a numeric hash.
    [InlineData("chara/equipment/e6080/texture/v01_c0201e6080_sho_b_base_3393220501.tex", TextureRole.Diffuse)]
    [InlineData("chara/equipment/e6080/texture/v01_c0201e6080_sho_b_norm_3393220501.tex", TextureRole.Normal)]
    [InlineData("chara/equipment/e6080/texture/v01_c0201e6080_sho_b_mask_3393220501.tex", TextureRole.Mask)]
    [InlineData("chara/equipment/e6080/texture/v01_c0201e6080_sho_e_id_3611647789.tex", TextureRole.Index)]
    [InlineData("chara/equipment/e6080/texture/v01_c0201e6080_sho_s_1234.tex", TextureRole.Specular)]
    // Unrecognized roles stay Other, hash or not.
    [InlineData("chara/common/texture/catchlight_1.tex", TextureRole.Other)]
    [InlineData("chara/equipment/e0100/texture/v01_c0101e0100_top_x.tex", TextureRole.Other)]
    [InlineData("chara/equipment/e0100/texture/v01_c0101e0100_top_glow_extra.tex", TextureRole.Other)]
    [InlineData("bgcommon/texture/1234567890.tex", TextureRole.Other)]
    public void ClassifiesRolesIncludingHashedNames(string path, TextureRole expected)
    {
        Assert.Equal(expected, TextureRoles.Classify(path));
    }

    [Fact]
    public void EditingServiceRoleLabelsMatchTheEnumNames()
    {
        Assert.Equal("Diffuse", Editing.ItemEditingService.TextureRole(
            "chara/equipment/e6080/texture/v01_c0201e6080_sho_b_base_3393220501.tex"));
        Assert.Equal("Other", Editing.ItemEditingService.TextureRole(
            "chara/common/texture/catchlight_1.tex"));
    }
}
