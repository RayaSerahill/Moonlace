using Microsoft.Extensions.Logging;
using Moonlace.Core.Models;
using Moonlace.Core.Session;
using Moonlace.GameData.Export;
using Moonlace.GameData.Interchange;
using Moonlace.GameData.Parsing;
using Moonlace.GameData.Resolution;

namespace Moonlace.GameData.Editing;

public sealed class EditableTexture
{
    public required string GamePath { get; init; }

    public required string Role { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required bool Modified { get; init; }
}

public sealed class EditableMaterial
{
    public required string GamePath { get; init; }

    public required string Name { get; init; }

    public required string ShaderPack { get; init; }

    public required bool Modified { get; init; }

    public required MaterialColorRow[] ColorTable { get; init; }

    public required IReadOnlyList<EditableTexture> Textures { get; init; }
}

public sealed class EditableMesh
{
    public required int Index { get; init; }

    public required int MaterialIndex { get; init; }

    public required int VertexCount { get; init; }

    public required int TriangleCount { get; init; }
}

public sealed class EditableItemInfo
{
    public required string ModelPath { get; init; }

    public required bool ModelModified { get; init; }

    /// <summary>Material names as stored in the model, indexable by <see cref="EditableMesh.MaterialIndex"/>.</summary>
    public required IReadOnlyList<string> MaterialNames { get; init; }

    public required IReadOnlyList<EditableMesh> Meshes { get; init; }

    public required IReadOnlyList<EditableMaterial> Materials { get; init; }
}

/// <summary>
/// High-level editing operations for the currently selected item. Reads go
/// through <see cref="EffectiveAssetProvider"/> (session copy wins), writes go
/// to the session — or straight into the linked Penumbra mod folder while a
/// live-edit link is active. The FFXIV installation is never written to.
/// </summary>
public sealed class ItemEditingService
{
    private readonly EffectiveAssetProvider _assets;
    private readonly AssetPathResolver _resolver;
    private readonly TextureDecoder _textures;
    private readonly ISessionService _session;
    private readonly Moonlace.Core.Penumbra.IPenumbraLinkService _link;
    private readonly ILogger<ItemEditingService> _logger;

    public ItemEditingService(
        EffectiveAssetProvider assets,
        AssetPathResolver resolver,
        TextureDecoder textures,
        ISessionService session,
        Moonlace.Core.Penumbra.IPenumbraLinkService link,
        ILogger<ItemEditingService> logger)
    {
        _assets = assets;
        _resolver = resolver;
        _textures = textures;
        _session = session;
        _link = link;
        _logger = logger;
    }

    /// <summary>The single write path for edited assets: linked Penumbra mod when live editing, session otherwise.</summary>
    private void Store(string gamePath, SessionAssetKind kind, byte[] data)
    {
        if (_link.IsLinked)
            _link.WriteAsset(gamePath, data);
        else
            _session.StoreAsset(gamePath, kind, data);
    }

    /// <summary>Everything the Material/Texture tabs show for an item, using effective assets.</summary>
    public Task<EditableItemInfo> GetItemInfoAsync(EquipmentItem item, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var resolved = _resolver.Resolve(item);
            var model = ParseEffectiveModel(resolved);

            var materials = new List<EditableMaterial>();
            foreach (var name in model.MaterialNames.Distinct(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var mtrlPath = _resolver.ResolveMaterialPath(resolved, name);
                var bytes = _assets.TryReadFile(mtrlPath);
                if (bytes is null)
                    continue;

                var parsed = MtrlParser.Parse(bytes);
                var textures = parsed.TexturePaths
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Select(texPath =>
                    {
                        var decoded = _textures.Decode(texPath);
                        return new EditableTexture
                        {
                            GamePath = texPath,
                            Role = TextureRole(texPath),
                            Width = decoded?.Width ?? 0,
                            Height = decoded?.Height ?? 0,
                            Modified = _assets.IsModified(texPath),
                        };
                    })
                    .ToArray();

                materials.Add(new EditableMaterial
                {
                    GamePath = mtrlPath,
                    Name = name,
                    ShaderPack = parsed.ShaderPack,
                    Modified = _assets.IsModified(mtrlPath),
                    ColorTable = [.. parsed.ColorTable],
                    Textures = textures,
                });
            }

            return new EditableItemInfo
            {
                ModelPath = resolved.MdlPath,
                ModelModified = _assets.IsModified(resolved.MdlPath),
                MaterialNames = [.. model.MaterialNames],
                Meshes = model.Meshes
                    .Select((mesh, index) => new EditableMesh
                    {
                        Index = index,
                        MaterialIndex = mesh.MaterialIndex,
                        VertexCount = mesh.Vertices.Length,
                        TriangleCount = mesh.Indices.Length / 3,
                    })
                    .ToArray(),
                Materials = materials,
            };
        }, ct);
    }

    /// <summary>
    /// Reassigns which material each mesh uses (one material index per mesh,
    /// in mesh order) and stores the rewritten model in the session.
    /// </summary>
    public Task SetMeshMaterialsAsync(EquipmentItem item, IReadOnlyList<int> materialIndices, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var resolved = _resolver.Resolve(item);
            var template = ParseEffectiveModel(resolved);
            if (materialIndices.Count != template.Meshes.Count)
                throw new ArgumentException(
                    $"The model has {template.Meshes.Count} meshes, {materialIndices.Count} assignments given.");

            var meshes = template.Meshes
                .Select((mesh, i) =>
                {
                    var materialIndex = materialIndices[i];
                    if (materialIndex < 0 || materialIndex >= template.MaterialNames.Count)
                        throw new ArgumentException($"Mesh {i}: material index {materialIndex} is out of range.");
                    return new ParsedMesh
                    {
                        Vertices = mesh.Vertices,
                        Indices = mesh.Indices,
                        MaterialIndex = materialIndex,
                        MaterialName = template.MaterialNames[materialIndex],
                        BoneTableIndex = mesh.BoneTableIndex,
                    };
                })
                .ToArray();

            var written = MdlWriter.Write(template, meshes, template.BoneTables);
            Store(resolved.MdlPath, SessionAssetKind.Model, written);
            _logger.LogInformation("Reassigned mesh materials for {Path}: [{Assignments}]",
                resolved.MdlPath, string.Join(", ", materialIndices));
        }, ct);
    }

    /// <summary>
    /// Replaces the texture paths a material references (one per existing
    /// slot, in order) and stores the rewritten material in the session.
    /// Every new path must resolve to an existing texture.
    /// </summary>
    public Task SetMaterialTexturesAsync(string mtrlPath, IReadOnlyList<string> texturePaths, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            foreach (var path in texturePaths)
            {
                if (_assets.TryReadFile(path) is null)
                    throw new InvalidDataException(
                        $"Texture not found in the game data: \"{path}\". Check the path for typos.");
            }

            var bytes = _assets.TryReadFile(mtrlPath)
                ?? throw new InvalidDataException($"Material could not be read: {mtrlPath}");
            var rewritten = MtrlWriter.ReplaceTexturePaths(bytes, texturePaths);

            // Self-check before storing: the rewrite must parse back cleanly.
            var check = MtrlParser.Parse(rewritten);
            if (!check.TexturePaths.SequenceEqual(texturePaths, StringComparer.Ordinal))
                throw new InvalidDataException("Internal error: the rewritten material failed verification.");

            Store(mtrlPath, SessionAssetKind.Material, rewritten);
            _logger.LogInformation("Reassigned textures for {Path}: {Textures}",
                mtrlPath, string.Join(", ", texturePaths));
        }, ct);
    }

    /// <summary>
    /// Creates a new model version (race/gender variant) for an equipment or
    /// accessory item by copying the source version onto the target race's
    /// paths: the model (material names re-race-coded in its string table),
    /// the item's own materials, and their race-coded textures. Everything is
    /// stored through the normal edit path — the session (so PMP export
    /// includes it) or the linked Penumbra mod (registered in its meta JSON)
    /// — so the new version is editable without affecting the others.
    /// Character materials (skin, …) are repointed to the target race's own
    /// game files when they exist. Returns the number of files stored.
    /// </summary>
    public Task<int> CreateModelVersionAsync(EquipmentItem item, string sourceRaceCode, string targetRaceCode, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (item.IsWeapon || item.IsBodyPart)
                throw new InvalidOperationException("Only equipment and accessories have race-specific model versions.");
            if (sourceRaceCode == targetRaceCode)
                throw new ArgumentException("The source and target versions are the same.");

            var targetMdlPath = _resolver.GetEquipmentModelPath(item, targetRaceCode);
            if (_assets.FileExists(targetMdlPath))
                throw new InvalidOperationException($"A c{targetRaceCode} model version already exists for this item.");

            var source = _resolver.ResolveForRace(item, sourceRaceCode);
            var mdlBytes = _assets.TryReadFile(source.MdlPath)
                ?? throw new AssetNotFoundException($"Source model could not be read: {source.MdlPath}");

            var sourceToken = $"c{sourceRaceCode}";
            var targetToken = $"c{targetRaceCode}";
            // "c0101e0602" — marks paths belonging to this item's own asset set.
            var sourceSetToken = sourceToken + AssetPathResolver.SetCode(item);
            var targetSetToken = targetToken + AssetPathResolver.SetCode(item);

            var parsed = MdlParser.Parse(mdlBytes);
            var filesStored = 0;
            var renames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in parsed.MaterialNames.Distinct(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (!name.Contains(sourceToken, StringComparison.Ordinal))
                    continue; // not race-coded (shared/absolute) — keep as-is

                var renamed = name.Replace(sourceToken, targetToken, StringComparison.Ordinal);
                if (name.Contains(sourceSetToken, StringComparison.Ordinal))
                {
                    // The item's own material: duplicate it (and its race-coded
                    // textures) so the new version is editable in isolation.
                    filesStored += CopyMaterialForRace(source, name, renamed, sourceSetToken, targetSetToken);
                    renames[name] = renamed;
                }
                else
                {
                    // Character material (skin, hair, …): repoint to the closest
                    // race the game ships materials for — the target race, else
                    // the gender's base race (skin materials only exist for the
                    // base bodies; e.g. Miqo'te ♀ uses the c0201 skin).
                    var chosen = new[] { targetRaceCode, AssetPathResolver.GenderBaseRace(targetRaceCode) }
                        .Distinct()
                        .Where(code => code != sourceRaceCode)
                        .Select(code => name.Replace(sourceToken, $"c{code}", StringComparison.Ordinal))
                        .FirstOrDefault(candidate => _assets.FileExists(_resolver.ResolveMaterialPath(source, candidate)));
                    if (chosen is not null)
                        renames[name] = chosen;
                    else
                        _logger.LogWarning("No c{Race} equivalent for {Name}; the new version keeps it as-is",
                            targetRaceCode, name);
                }
            }

            var patchedMdl = renames.Count == 0
                ? mdlBytes
                : MdlMaterialRenamer.RenameMaterials(mdlBytes, n => renames.GetValueOrDefault(n));
            Store(targetMdlPath, SessionAssetKind.Model, patchedMdl);
            filesStored++;

            _logger.LogInformation("Created model version c{Target} from c{Source} for {Item} ({Files} files)",
                targetRaceCode, sourceRaceCode, item.Name, filesStored);
            return filesStored;
        }, ct);
    }

    /// <summary>Copies one item-owned material (and its race-coded textures) onto the target race's paths.</summary>
    private int CopyMaterialForRace(ResolvedModelInfo source, string name, string renamedName, string sourceSetToken, string targetSetToken)
    {
        var sourcePath = _resolver.ResolveMaterialPath(source, name);
        var bytes = _assets.TryReadFile(sourcePath)
            ?? throw new AssetNotFoundException($"Material could not be read: {sourcePath}");
        var stored = 0;

        var parsed = MtrlParser.Parse(bytes);
        var newTexturePaths = new List<string>(parsed.TexturePaths.Count);
        foreach (var texPath in parsed.TexturePaths)
        {
            if (string.IsNullOrEmpty(texPath) || !texPath.Contains(sourceSetToken, StringComparison.Ordinal))
            {
                newTexturePaths.Add(texPath); // shared texture — referenced, not copied
                continue;
            }

            var texBytes = _assets.TryReadFile(texPath);
            if (texBytes is null)
            {
                _logger.LogWarning("Texture could not be read: {Path}; the new version keeps the shared path", texPath);
                newTexturePaths.Add(texPath);
                continue;
            }

            var renamedTex = texPath.Replace(sourceSetToken, targetSetToken, StringComparison.Ordinal);
            if (!_assets.FileExists(renamedTex))
            {
                Store(renamedTex, SessionAssetKind.Texture, texBytes);
                stored++;
            }

            newTexturePaths.Add(renamedTex);
        }

        var rewritten = newTexturePaths.SequenceEqual(parsed.TexturePaths, StringComparer.Ordinal)
            ? bytes
            : MtrlWriter.ReplaceTexturePaths(bytes, newTexturePaths);
        Store(_resolver.ResolveMaterialPath(source, renamedName), SessionAssetKind.Material, rewritten);
        return stored + 1;
    }

    private static bool IsFbxPath(string path)
        => string.Equals(Path.GetExtension(path), ".fbx", StringComparison.OrdinalIgnoreCase);

    /// <summary>Exports the effective model (session version when modified) as .glb or .fbx, picked by extension. Does not touch session state.</summary>
    public Task ExportModelAsync(EquipmentItem item, string outputPath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var resolved = _resolver.Resolve(item);
            var model = ParseEffectiveModel(resolved);

            var materials = model.MaterialNames
                .Select(name =>
                {
                    var mtrlPath = _resolver.ResolveMaterialPath(resolved, name);
                    byte[]? basePng = null;
                    byte[]? normalPng = null;
                    var bytes = _assets.TryReadFile(mtrlPath);
                    if (bytes is not null)
                    {
                        var parsed = MtrlParser.Parse(bytes);
                        basePng = EncodeTexture(parsed.TexturePaths.FirstOrDefault(p => TextureRole(p) == "Diffuse"));
                        normalPng = EncodeTexture(parsed.TexturePaths.FirstOrDefault(p => TextureRole(p) == "Normal"));
                    }

                    return new ModelMaterialInfo { Name = name, BaseColorPng = basePng, NormalPng = normalPng };
                })
                .ToArray();

            ct.ThrowIfCancellationRequested();
            if (IsFbxPath(outputPath))
                FbxExporter.Export(model, materials, outputPath);
            else
                GltfExporter.Export(model, materials, outputPath);
            _logger.LogInformation("Exported {Path} as {Format} to {Out}",
                resolved.MdlPath, IsFbxPath(outputPath) ? "FBX" : "GLTF", outputPath);
        }, ct);
    }

    /// <summary>Imports a GLTF/GLB or FBX (picked by extension) as the session replacement for the item's model and stores it in the session.</summary>
    public Task ImportModelAsync(EquipmentItem item, string modelPath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var resolved = _resolver.Resolve(item);
            var template = ParseEffectiveModel(resolved);

            var import = IsFbxPath(modelPath)
                ? FbxImporter.Import(modelPath, template)
                : GltfImporter.Import(modelPath, template);
            var written = MdlWriter.Write(template, import.Meshes, import.BoneTables);

            // Sanity: our own parser must accept what we are about to store.
            var check = MdlParser.Parse(written);
            if (check.Meshes.Count != import.Meshes.Count)
                throw new ModelImportException("Internal error: the rebuilt model failed verification.");

            Store(resolved.MdlPath, SessionAssetKind.Model, written);
            _logger.LogInformation("Imported {Model} as session model for {Path} ({Meshes} meshes)",
                modelPath, resolved.MdlPath, import.Meshes.Count);
        }, ct);
    }

    /// <summary>Exports the effective texture as PNG. Does not touch session state.</summary>
    public Task ExportTexturePngAsync(string texPath, string outputPath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var decoded = _textures.Decode(texPath)
                ?? throw new InvalidDataException($"Texture could not be decoded: {texPath}");
            File.WriteAllBytes(outputPath, ImageIo.EncodePng(decoded.Width, decoded.Height, decoded.Rgba));
            _logger.LogInformation("Exported texture {Path} to {Out}", texPath, outputPath);
        }, ct);
    }

    /// <summary>Imports an image file as the session replacement for a texture.</summary>
    public Task ImportTextureAsync(string texPath, string imagePath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var (width, height, rgba) = ImageIo.DecodeImageFile(imagePath);
            if (width <= 0 || height <= 0)
                throw new InvalidDataException("The image is empty.");

            // Guard against accidental wrong-file imports: FFXIV UVs assume the
            // original aspect ratio. Resolution itself may differ.
            var original = _textures.Decode(texPath);
            if (original is not null && original.Width > 0 && original.Height > 0)
            {
                var originalAspect = (double)original.Width / original.Height;
                var importedAspect = (double)width / height;
                if (Math.Abs(originalAspect - importedAspect) / originalAspect > 0.01)
                    throw new InvalidDataException(
                        $"The image is {width}x{height}, but this texture is {original.Width}x{original.Height} " +
                        $"— the aspect ratio must match or the texture will appear distorted.");
            }

            var tex = TexWriter.Write(width, height, rgba);
            Store(texPath, SessionAssetKind.Texture, tex);
            _logger.LogInformation("Imported {Image} as session texture for {Path} ({W}x{H})",
                imagePath, texPath, width, height);
        }, ct);
    }

    /// <summary>Applies edited color table rows to a material and stores the result in the session.</summary>
    public Task SetMaterialColorTableAsync(string mtrlPath, IReadOnlyList<MaterialColorRow> rows, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var bytes = _assets.TryReadFile(mtrlPath)
                ?? throw new InvalidDataException($"Material could not be read: {mtrlPath}");
            var patched = MtrlWriter.PatchColorTable(bytes, rows);
            Store(mtrlPath, SessionAssetKind.Material, patched);
        }, ct);
    }

    /// <summary>Packages the active session as a PMP mod file.</summary>
    public Task ExportPmpAsync(PmpMetadata metadata, string outputPath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (_link.IsLinked)
                throw new InvalidOperationException(
                    "PMP export packages the session workspace — unlink the Penumbra mod first (edits are already live in the mod folder).");
            PmpExporter.Export(_session, metadata, outputPath);
            _logger.LogInformation("Exported session as PMP to {Out}", outputPath);
        }, ct);
    }

    private ParsedModel ParseEffectiveModel(ResolvedModelInfo resolved)
    {
        var bytes = _assets.TryReadFile(resolved.MdlPath)
            ?? throw new AssetNotFoundException($"Model could not be read: {resolved.MdlPath}");
        return MdlParser.Parse(bytes);
    }

    private byte[]? EncodeTexture(string? texPath)
    {
        if (string.IsNullOrEmpty(texPath))
            return null;
        var decoded = _textures.Decode(texPath);
        return decoded is null ? null : ImageIo.EncodePng(decoded.Width, decoded.Height, decoded.Rgba);
    }

    internal static string TextureRole(string texPath)
    {
        var stem = Path.GetFileNameWithoutExtension(texPath);
        var suffix = stem[(stem.LastIndexOf('_') + 1)..];
        return suffix switch
        {
            "d" or "base" => "Diffuse",
            "n" or "norm" => "Normal",
            "m" or "mask" => "Mask",
            "id" or "index" => "Index",
            "s" or "spec" => "Specular",
            _ => "Other",
        };
    }
}
