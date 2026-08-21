namespace Moonlace.GameData;

/// <summary>What a texture contributes to a material, classified from its file name.</summary>
public enum TextureRole
{
    Diffuse,
    Normal,
    Mask,
    Index,
    Specular,
    Other,
}

/// <summary>
/// Classifies a texture path's role from its file-name suffix. FFXIV names
/// textures with a role suffix ("_d"/"_base" diffuse, "_n"/"_norm" normal,
/// "_m"/"_mask" mask, "_id"/"_index" color-table index, "_s"/"_spec"
/// specular). TexTools-made mods append a numeric hash after the role
/// (e.g. "v01_c0201e6080_sho_b_base_3393220501.tex"), so classification
/// skips trailing all-digit segments before matching.
/// </summary>
public static class TextureRoles
{
    public static TextureRole Classify(string texPath)
    {
        var stem = Path.GetFileNameWithoutExtension(texPath);
        var segments = stem.Split('_');
        for (var i = segments.Length - 1; i > 0; i--)
        {
            var segment = segments[i];
            switch (segment)
            {
                case "d" or "base":
                    return TextureRole.Diffuse;
                case "n" or "norm":
                    return TextureRole.Normal;
                case "m" or "mask":
                    return TextureRole.Mask;
                case "id" or "index":
                    return TextureRole.Index;
                case "s" or "spec":
                    return TextureRole.Specular;
            }

            // A trailing hash segment is all digits; anything else ends the search.
            if (segment.Length == 0 || !segment.All(char.IsAsciiDigit))
                break;
        }

        return TextureRole.Other;
    }
}
