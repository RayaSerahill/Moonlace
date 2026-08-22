using System.Text.RegularExpressions;
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

    private static readonly Regex PartNamePattern = new(@"^mesh_(\d+)\.(\d+)", RegexOptions.Compiled);

    /// <summary>The export name for one submesh part of a mesh.</summary>
    public static string PartName(int meshIndex, int partIndex) => $"mesh_{meshIndex}.{partIndex}";

    /// <summary>
    /// Recognizes the exporters' "mesh_2.1" part naming (tolerating Blender's
    /// ".001" duplicate suffixes after the part number). Plain "mesh_2" names
    /// from part-unaware exports do not match; those meshes import whole.
    /// </summary>
    public static bool TryParsePartName(string? name, out int meshIndex, out int partIndex)
    {
        meshIndex = 0;
        partIndex = 0;
        if (name is null)
            return false;
        var match = PartNamePattern.Match(name);
        if (!match.Success)
            return false;
        meshIndex = int.Parse(match.Groups[1].Value);
        partIndex = int.Parse(match.Groups[2].Value);
        return true;
    }

    /// <summary>One imported part before its group is merged into a mesh.</summary>
    public sealed record ImportedPart(ParsedVertex[] Vertices, uint[] Indices, int PartNumber, string Label);

    /// <summary>
    /// Merges a group of imported parts (ordered by part number) into one
    /// mesh whose submesh partition mirrors the parts. Attribute masks and
    /// bone map slices are restored from the template mesh's submesh at the
    /// same part number; parts the template does not know get no attributes.
    /// A single part with an unknown part number (a part-unaware import)
    /// yields no partition at all, matching the previous behavior.
    /// </summary>
    public static ParsedMesh MergeParts(
        IReadOnlyList<ImportedPart> parts, ParsedMesh? templateMesh,
        int materialIndex, string materialName, int boneTableIndex)
    {
        if (parts.Count == 1 && parts[0].PartNumber < 0)
        {
            return new ParsedMesh
            {
                Vertices = parts[0].Vertices,
                Indices = parts[0].Indices,
                MaterialIndex = materialIndex,
                MaterialName = materialName,
                BoneTableIndex = boneTableIndex,
            };
        }

        var ordered = parts.OrderBy(p => p.PartNumber).ToArray();
        var totalVertices = ordered.Sum(p => p.Vertices.Length);
        if (totalVertices > ushort.MaxValue)
            throw new ModelImportException(
                $"The parts of \"{ordered[0].Label}\" total {totalVertices:N0} vertices; FFXIV models support " +
                "at most 65,535 per mesh. Reduce the vertex count.");

        var vertices = new ParsedVertex[totalVertices];
        var indices = new uint[ordered.Sum(p => p.Indices.Length)];
        var submeshes = new List<ParsedSubmesh>(ordered.Length);
        var vertexBase = 0;
        var indexBase = 0;
        foreach (var part in ordered)
        {
            part.Vertices.CopyTo(vertices, vertexBase);
            for (var i = 0; i < part.Indices.Length; i++)
                indices[indexBase + i] = (uint)(part.Indices[i] + vertexBase);

            var template = templateMesh is not null && part.PartNumber < templateMesh.Submeshes.Count
                ? templateMesh.Submeshes[part.PartNumber]
                : default;
            submeshes.Add(new ParsedSubmesh(
                IndexOffset: (uint)indexBase,
                IndexCount: (uint)part.Indices.Length,
                AttributeMask: template.AttributeMask,
                BoneStartIndex: template.BoneStartIndex,
                BoneCount: template.BoneCount));

            vertexBase += part.Vertices.Length;
            indexBase += part.Indices.Length;
        }

        return new ParsedMesh
        {
            Vertices = vertices,
            Indices = indices,
            MaterialIndex = materialIndex,
            MaterialName = materialName,
            BoneTableIndex = boneTableIndex,
            Submeshes = submeshes,
        };
    }
}
