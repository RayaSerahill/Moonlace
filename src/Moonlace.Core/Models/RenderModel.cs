using System.Numerics;

namespace Moonlace.Core.Models;

/// <summary>
/// CPU-side, renderer-ready model data. Contains no FFXIV format knowledge —
/// the renderer consumes this and nothing else.
/// </summary>
public sealed class RenderModel
{
    public required IReadOnlyList<RenderMesh> Meshes { get; init; }

    /// <summary>Axis-aligned bounds over all mesh vertices, used for camera framing.</summary>
    public required Vector3 BoundsMin { get; init; }

    public required Vector3 BoundsMax { get; init; }
}

public sealed class RenderMesh
{
    public required RenderVertex[] Vertices { get; init; }

    public required uint[] Indices { get; init; }

    public required RenderMaterial Material { get; init; }
}

public struct RenderVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 Uv;
    public Vector4 Tangent;
    public Vector4 Color;
}

/// <summary>
/// Simplified material: a handful of optional texture maps plus a color table.
/// Approximates FFXIV's character shaders well enough to recognize the item.
/// </summary>
public sealed class RenderMaterial
{
    /// <summary>Logical game path of the source .mtrl (empty for fallback materials).</summary>
    public string GamePath { get; init; } = "";

    public string ShaderPack { get; init; } = "";

    /// <summary>Base color map (may be absent — many materials are colored purely via the color table).</summary>
    public RenderTexture? Diffuse { get; init; }

    public RenderTexture? Normal { get; init; }

    /// <summary>Mask / specular-ish map.</summary>
    public RenderTexture? Mask { get; init; }

    /// <summary>Color table row-index map ("id" texture).</summary>
    public RenderTexture? Index { get; init; }

    public RenderTexture? Specular { get; init; }

    /// <summary>Color table rows; empty when the material has none.</summary>
    public IReadOnlyList<ColorTableRow> ColorTable { get; init; } = [];
}

public struct ColorTableRow
{
    public Vector3 DiffuseColor;
    public Vector3 SpecularColor;
    public Vector3 EmissiveColor;
    public float GlossStrength;
    public float SpecularStrength;
}

/// <summary>Decoded RGBA8 pixel data ready for GPU upload.</summary>
public sealed class RenderTexture
{
    /// <summary>Cache key; the game path of the source texture.</summary>
    public required string Key { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Tightly packed RGBA8, top-left origin, Width*Height*4 bytes.</summary>
    public required byte[] Rgba { get; init; }
}
