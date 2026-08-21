using Moonlace.GameData.Parsing;

namespace Moonlace.GameData.Interchange;

/// <summary>Per-material data attached to a model export (GLTF or FBX).</summary>
public sealed class ModelMaterialInfo
{
    /// <summary>The FFXIV material name (e.g. "/mt_w0201b0001_a.mtrl"); round-trips through Blender for re-import mapping.</summary>
    public required string Name { get; init; }

    public byte[]? BaseColorPng { get; init; }

    public byte[]? NormalPng { get; init; }
}

/// <summary>The meshes and per-mesh bone tables produced by a model import.</summary>
public sealed record ModelImportResult(IReadOnlyList<ParsedMesh> Meshes, IReadOnlyList<ushort[]> BoneTables);

/// <summary>A model file could not be imported; the message is user-facing.</summary>
public sealed class ModelImportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Mapping logic shared by the GLTF and FBX importers.</summary>
internal static class ModelImportShared
{
    /// <summary>
    /// Maps an incoming mesh onto a template material slot by material name,
    /// falling back to mesh order only when that is unambiguous.
    /// </summary>
    public static int ResolveMaterialIndex(
        string? materialName, int meshIndex, int meshCount, ParsedModel template, string label)
    {
        if (!string.IsNullOrEmpty(materialName))
        {
            for (var i = 0; i < template.MaterialNames.Count; i++)
            {
                if (string.Equals(template.MaterialNames[i], materialName, StringComparison.Ordinal))
                    return i;
            }
        }

        // No name match: fall back to order only when it is unambiguous.
        if (meshCount <= template.MaterialNames.Count)
            return Math.Min(meshIndex, template.MaterialNames.Count - 1);

        throw new ModelImportException(
            $"Cannot map \"{label}\" to an FFXIV material. Name the materials after the original ones " +
            $"({string.Join(", ", template.MaterialNames)}) — the exported model already does this.");
    }

    /// <summary>Adds a bone to a per-mesh bone table, enforcing the format's 64-entry limit.</summary>
    public static void EnsureInTable(List<ushort> boneTable, ushort boneIndex, string label)
    {
        if (boneTable.Contains(boneIndex))
            return;
        if (boneTable.Count >= 64)
            throw new ModelImportException(
                $"\"{label}\" uses more than 64 distinct bones in one mesh, which the model format cannot store.");
        boneTable.Add(boneIndex);
    }

    public static int IndexInTable(List<ushort> boneTable, ushort boneIndex) => boneTable.IndexOf(boneIndex);
}
