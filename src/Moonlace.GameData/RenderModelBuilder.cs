using System.Numerics;
using Microsoft.Extensions.Logging;
using Moonlace.Core.Interfaces;
using Moonlace.Core.Models;
using Moonlace.GameData.Parsing;
using Moonlace.GameData.Resolution;

namespace Moonlace.GameData;

/// <summary>
/// The item → renderable pipeline: resolve paths, parse the model, load its
/// materials and decode their textures, and assemble a <see cref="RenderModel"/>.
/// Each stage logs what it resolved so failures can be debugged in isolation.
/// </summary>
public sealed class RenderModelBuilder : IRenderModelLoader
{
    private readonly EffectiveAssetProvider _assets;
    private readonly AssetPathResolver _resolver;
    private readonly TextureDecoder _textures;
    private readonly ILogger<RenderModelBuilder> _logger;

    public RenderModelBuilder(
        EffectiveAssetProvider assets,
        AssetPathResolver resolver,
        TextureDecoder textures,
        ILogger<RenderModelBuilder> logger)
    {
        _assets = assets;
        _resolver = resolver;
        _textures = textures;
        _logger = logger;
    }

    public Task<RenderModel> LoadAsync(EquipmentItem item, CancellationToken ct = default)
    {
        return Task.Run(() => Load(item, ct), ct);
    }

    private RenderModel Load(EquipmentItem item, CancellationToken ct)
    {
        var resolved = _resolver.Resolve(item);
        _logger.LogInformation("Item {Id} \"{Name}\": model {Mdl}, material set v{Set:D4}",
            item.RowId, item.Name, resolved.MdlPath, resolved.MaterialSet);

        var mdlData = _assets.TryReadFile(resolved.MdlPath)
            ?? throw new AssetNotFoundException($"Model could not be read: {resolved.MdlPath}");
        ct.ThrowIfCancellationRequested();

        var parsed = MdlParser.Parse(mdlData);
        _logger.LogInformation("Parsed model: {MeshCount} meshes, materials: {Materials}",
            parsed.Meshes.Count, string.Join(", ", parsed.MaterialNames));

        // Load each distinct material once.
        var materials = new Dictionary<string, RenderMaterial>(StringComparer.Ordinal);
        foreach (var name in parsed.MaterialNames)
        {
            ct.ThrowIfCancellationRequested();
            if (!materials.ContainsKey(name))
                materials[name] = LoadMaterial(resolved, name);
        }

        var fallbackMaterial = new RenderMaterial();
        var meshes = new List<RenderMesh>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var mesh in parsed.Meshes)
        {
            ct.ThrowIfCancellationRequested();
            if (mesh.Vertices.Length == 0 || mesh.Indices.Length == 0)
                continue;

            var vertices = new RenderVertex[mesh.Vertices.Length];
            for (var i = 0; i < vertices.Length; i++)
            {
                var v = mesh.Vertices[i];
                vertices[i] = new RenderVertex
                {
                    Position = v.Position,
                    Normal = v.Normal,
                    Uv = v.Uv,
                    Tangent = v.Tangent,
                    Color = v.Color,
                };
                min = Vector3.Min(min, v.Position);
                max = Vector3.Max(max, v.Position);
            }

            meshes.Add(new RenderMesh
            {
                Vertices = vertices,
                Indices = mesh.Indices,
                Material = materials.GetValueOrDefault(mesh.MaterialName, fallbackMaterial),
            });
        }

        if (meshes.Count == 0)
            throw new AssetNotFoundException($"Model contains no displayable geometry: {resolved.MdlPath}");

        return new RenderModel { Meshes = meshes, BoundsMin = min, BoundsMax = max };
    }

    private RenderMaterial LoadMaterial(ResolvedModelInfo model, string materialName)
    {
        var mtrlPath = _resolver.ResolveMaterialPath(model, materialName);
        try
        {
            var data = _assets.TryReadFile(mtrlPath);
            if (data is null)
            {
                _logger.LogWarning("Material not found: {Path}", mtrlPath);
                return new RenderMaterial();
            }

            var parsed = MtrlParser.Parse(data);
            _logger.LogInformation("Material {Path}: shader {Shader}, textures: {Textures}",
                mtrlPath, parsed.ShaderPack, string.Join(", ", parsed.TexturePaths));

            RenderTexture? diffuse = null, normal = null, mask = null, index = null, specular = null;
            foreach (var texPath in parsed.TexturePaths)
            {
                // FFXIV texture roles are identifiable from the filename suffix.
                switch (TextureRoles.Classify(texPath))
                {
                    case TextureRole.Diffuse:
                        diffuse = _textures.Decode(texPath);
                        break;
                    case TextureRole.Normal:
                        normal = _textures.Decode(texPath);
                        break;
                    case TextureRole.Mask:
                        mask = _textures.Decode(texPath);
                        break;
                    case TextureRole.Index:
                        index = _textures.Decode(texPath);
                        break;
                    case TextureRole.Specular:
                        specular = _textures.Decode(texPath);
                        break;
                    default:
                        _logger.LogDebug("Ignoring texture with unrecognized role: {Path}", texPath);
                        break;
                }
            }

            return new RenderMaterial
            {
                GamePath = mtrlPath,
                ShaderPack = parsed.ShaderPack,
                Diffuse = diffuse,
                Normal = normal,
                Mask = mask,
                Index = index,
                Specular = specular,
                ColorTable = parsed.ColorTable
                    .Select(row => new ColorTableRow
                    {
                        DiffuseColor = row.Diffuse,
                        SpecularColor = row.Specular,
                        EmissiveColor = row.Emissive,
                        GlossStrength = row.Gloss,
                        SpecularStrength = row.SpecularStrength,
                    })
                    .ToArray(),
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load material {Path}; using fallback", mtrlPath);
            return new RenderMaterial();
        }
    }
}
