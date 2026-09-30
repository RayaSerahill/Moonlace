using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Moonlace.Core.Models;
using Moonlace.Core.Session;
using Moonlace.GameData.Export;
using Moonlace.GameData.Interchange;
using Moonlace.GameData.Meta;
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

    /// <summary>True when a texture is bound through the diffuse sampler (g_SamplerDiffuse).</summary>
    public bool HasDiffuseSlot { get; init; }

    /// <summary>Where a new diffuse texture would go by default (see <see cref="MaterialEdits.SuggestDiffusePath"/>).</summary>
    public string SuggestedDiffusePath { get; init; } = "";
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

    /// <summary>Adds a metadata manipulation where edits go: the linked mod, or the active session item.</summary>
    private void StoreManipulation(JsonObject manipulation)
    {
        if (_link.IsLinked)
            _link.SetManipulation(manipulation);
        else
            _session.StoreManipulation(manipulation);
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
                // Materials nobody supplies (a third-party mod's, typed into
                // the Model tab) or that fail to parse are simply not listed;
                // the viewport renders them white.
                string mtrlPath;
                ParsedMaterial parsed;
                bool hasDiffuseSlot;
                try
                {
                    mtrlPath = _resolver.ResolveMaterialPath(resolved, name);
                    var bytes = _assets.TryReadFile(mtrlPath);
                    if (bytes is null)
                        continue;
                    parsed = MtrlParser.Parse(bytes);
                    hasDiffuseSlot = MaterialEdits.HasDiffuseSlot(MtrlDocument.Parse(bytes));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Material {Name} could not be read; not listing it", name);
                    continue;
                }

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
                    HasDiffuseSlot = hasDiffuseSlot,
                    SuggestedDiffusePath = MaterialEdits.SuggestDiffusePath(parsed.TexturePaths, mtrlPath),
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

    /// <summary>Reassigns each mesh to one of the model's existing material slots and stores the model.</summary>
    public Task SetMeshMaterialsAsync(EquipmentItem item, IReadOnlyList<int> materialIndices, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var resolved = _resolver.Resolve(item);
            var template = ParseEffectiveModel(resolved);
            var names = materialIndices
                .Select((materialIndex, i) =>
                    materialIndex >= 0 && materialIndex < template.MaterialNames.Count
                        ? template.MaterialNames[materialIndex]
                        : throw new ArgumentException($"Mesh {i}: material index {materialIndex} is out of range."))
                .ToArray();
            WriteMeshMaterials(resolved, template, names);
        }, ct);
    }

    /// <summary>
    /// Assigns each mesh a material by name. A name the model already has
    /// reuses its slot; any other name (a third-party mod's material such as
    /// "/mt_c0201b0001_bibo.mtrl", or a full game path) is added to the
    /// model's material list as-is. Nothing checks that such a material
    /// exists: another mod is assumed to supply it in game, and the viewport
    /// renders it white until something does.
    /// </summary>
    public Task SetMeshMaterialsAsync(EquipmentItem item, IReadOnlyList<string> materialNames, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var resolved = _resolver.Resolve(item);
            var template = ParseEffectiveModel(resolved);
            WriteMeshMaterials(resolved, template, materialNames);
        }, ct);
    }

    private void WriteMeshMaterials(ResolvedModelInfo resolved, ParsedModel template, IReadOnlyList<string> materialNames)
    {
        if (materialNames.Count != template.Meshes.Count)
            throw new ArgumentException(
                $"The model has {template.Meshes.Count} meshes, {materialNames.Count} assignments given.");

        var allNames = template.MaterialNames.ToList();
        var meshes = new ParsedMesh[template.Meshes.Count];
        for (var i = 0; i < meshes.Length; i++)
        {
            var name = materialNames[i]?.Trim() ?? "";
            if (name.Length == 0)
                throw new ArgumentException($"Mesh {i} has no material name.");
            if (name.Contains('\0'))
                throw new ArgumentException($"Mesh {i}: the material name contains a null character.");

            var materialIndex = allNames.IndexOf(name);
            if (materialIndex < 0)
            {
                materialIndex = allNames.Count;
                allNames.Add(name);
            }

            var mesh = template.Meshes[i];
            meshes[i] = new ParsedMesh
            {
                Vertices = mesh.Vertices,
                Indices = mesh.Indices,
                MaterialIndex = materialIndex,
                MaterialName = name,
                BoneTableIndex = mesh.BoneTableIndex,
                Submeshes = mesh.Submeshes,
            };
        }

        var written = MdlWriter.Write(template, meshes, template.BoneTables, materialNames: allNames);
        Store(resolved.MdlPath, SessionAssetKind.Model, written);
        _logger.LogInformation("Reassigned mesh materials for {Path}: [{Assignments}]",
            resolved.MdlPath, string.Join(", ", meshes.Select(m => m.MaterialName)));
    }

    /// <summary>
    /// Creates a new material for the item: a copy of <paramref name="sourceName"/>
    /// (a material the model already lists) stored under <paramref name="newName"/>
    /// in the item's current material set, and added to the model's material
    /// list so meshes can be assigned to it. No mesh uses it yet.
    /// </summary>
    public Task CreateMaterialAsync(EquipmentItem item, string newName, string sourceName, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var resolved = _resolver.Resolve(item);
            var template = ParseEffectiveModel(resolved);

            newName = NormalizeMaterialName(newName);
            if (template.MaterialNames.Contains(newName, StringComparer.Ordinal))
                throw new ArgumentException($"The model already has a material named {newName}.");
            if (!template.MaterialNames.Contains(sourceName, StringComparer.Ordinal))
                throw new ArgumentException($"{sourceName} is not one of this model's materials.");

            var sourcePath = _resolver.ResolveMaterialPath(resolved, sourceName);
            var bytes = _assets.TryReadFile(sourcePath)
                ?? throw new InvalidDataException($"The material to copy could not be read: {sourcePath}");
            var newPath = _resolver.ResolveMaterialPath(resolved, newName);

            var names = template.MaterialNames.Append(newName).ToArray();
            var written = MdlWriter.Write(template, template.Meshes, template.BoneTables, materialNames: names);
            if (!MdlParser.Parse(written).MaterialNames.SequenceEqual(names, StringComparer.Ordinal))
                throw new InvalidDataException("Internal error: the rewritten model failed verification.");

            Store(newPath, SessionAssetKind.Material, bytes);
            Store(resolved.MdlPath, SessionAssetKind.Model, written);
            _logger.LogInformation("Created material {Name} ({Path}) as a copy of {Source}", newName, newPath, sourceName);
        }, ct);
    }

    /// <summary>
    /// Removes a material from the model's material list. Meshes that used it
    /// move to the first remaining material. The .mtrl file itself is left
    /// alone (nothing references it any more). Returns how many meshes moved.
    /// </summary>
    public Task<int> DeleteMaterialAsync(EquipmentItem item, string name, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var resolved = _resolver.Resolve(item);
            var template = ParseEffectiveModel(resolved);

            var names = template.MaterialNames.ToList();
            var removedIndex = names.IndexOf(name);
            if (removedIndex < 0)
                throw new ArgumentException($"{name} is not one of this model's materials.");
            if (names.Count == 1)
                throw new InvalidOperationException("A model needs at least one material; this is the last one.");
            names.RemoveAt(removedIndex);

            var moved = 0;
            var meshes = template.Meshes.Select(mesh =>
            {
                var index = mesh.MaterialIndex;
                if (index == removedIndex)
                {
                    index = 0;
                    moved++;
                }
                else if (index > removedIndex)
                {
                    index--;
                }

                return new ParsedMesh
                {
                    Vertices = mesh.Vertices,
                    Indices = mesh.Indices,
                    MaterialIndex = index,
                    MaterialName = names[index],
                    BoneTableIndex = mesh.BoneTableIndex,
                    Submeshes = mesh.Submeshes,
                };
            }).ToArray();

            var written = MdlWriter.Write(template, meshes, template.BoneTables, materialNames: names);
            if (!MdlParser.Parse(written).MaterialNames.SequenceEqual(names, StringComparer.Ordinal))
                throw new InvalidDataException("Internal error: the rewritten model failed verification.");

            Store(resolved.MdlPath, SessionAssetKind.Model, written);
            _logger.LogInformation("Removed material {Name} from {Path} ({Moved} meshes moved to {First})",
                name, resolved.MdlPath, moved, names[0]);
            return moved;
        }, ct);
    }

    /// <summary>"mt_x_b" or "/mt_x_b.mtrl" → "/mt_x_b.mtrl": model material names are "/"-prefixed file names.</summary>
    public static string NormalizeMaterialName(string name)
    {
        name = name.Trim().Replace('\\', '/');
        if (name.Length == 0)
            throw new ArgumentException("Enter a name for the new material.");
        if (name.Contains('\0'))
            throw new ArgumentException("The material name contains a null character.");
        if (!name.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
            name += ".mtrl";
        if (!name.StartsWith('/') && !name.Contains('/'))
            name = "/" + name;
        return name;
    }

    /// <summary>
    /// A free material name next to <paramref name="baseName"/>: its trailing
    /// letter bumped ("/mt_c0101e6100_top_a.mtrl" → "_b", "_c", ...) past
    /// every name the model already has, else "_new" variants.
    /// </summary>
    public static string SuggestMaterialName(string baseName, IReadOnlyCollection<string> existing)
    {
        var match = System.Text.RegularExpressions.Regex.Match(baseName, @"^(.*_)([a-z])\.mtrl$");
        if (match.Success)
        {
            for (var letter = (char)(match.Groups[2].Value[0] + 1); letter <= 'z'; letter++)
            {
                var candidate = $"{match.Groups[1].Value}{letter}.mtrl";
                if (!existing.Contains(candidate))
                    return candidate;
            }
        }

        var stem = baseName.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) ? baseName[..^5] : baseName;
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? $"{stem}_new.mtrl" : $"{stem}_new{n}.mtrl";
            if (!existing.Contains(candidate))
                return candidate;
        }
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
    /// Adds a diffuse texture slot to a material (see
    /// <see cref="MaterialEdits.AddDiffuseSlot"/>). The texture may not exist
    /// yet; it shows up in the Texture tab to import an image into.
    /// </summary>
    public Task AddDiffuseSlotAsync(string mtrlPath, string texturePath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            texturePath = texturePath.Trim().Replace('\\', '/');
            if (texturePath.Length == 0)
                throw new ArgumentException("Enter a path for the diffuse texture.");
            if (!texturePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The diffuse texture path must end in .tex.");

            var bytes = _assets.TryReadFile(mtrlPath)
                ?? throw new InvalidDataException($"Material could not be read: {mtrlPath}");
            var rewritten = MaterialEdits.AddDiffuseSlot(bytes, texturePath);

            // Self-check before storing: both readers must see the new slot.
            if (!MaterialEdits.HasDiffuseSlot(MtrlDocument.Parse(rewritten))
                || !MtrlParser.Parse(rewritten).TexturePaths.Contains(texturePath))
                throw new InvalidDataException("Internal error: the rewritten material failed verification.");

            Store(mtrlPath, SessionAssetKind.Material, rewritten);
            _logger.LogInformation("Added diffuse slot {Texture} to {Path}", texturePath, mtrlPath);
        }, ct);
    }

    /// <summary>Switches a material's shader pack (name only; see <see cref="MaterialEdits.SetShader"/>).</summary>
    public Task SetMaterialShaderAsync(string mtrlPath, string shaderPack, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            shaderPack = shaderPack.Trim();
            if (shaderPack.Length == 0)
                throw new ArgumentException("Enter a shader pack name, e.g. character.shpk.");

            var bytes = _assets.TryReadFile(mtrlPath)
                ?? throw new InvalidDataException($"Material could not be read: {mtrlPath}");
            var rewritten = MaterialEdits.SetShader(bytes, shaderPack);
            if (MtrlParser.Parse(rewritten).ShaderPack != shaderPack)
                throw new InvalidDataException("Internal error: the rewritten material failed verification.");

            Store(mtrlPath, SessionAssetKind.Material, rewritten);
            _logger.LogInformation("Switched {Path} to shader {Shader}", mtrlPath, shaderPack);
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

    /// <summary>
    /// Imports a GLTF/GLB or FBX (picked by extension) as the replacement for
    /// the item's model and stores it (session, or the linked mod).
    ///
    /// Body kits are built on the base bodies (Midlander ♀ for every female
    /// race, Midlander ♂ for male ones) and the game reshapes a base model for
    /// the other races at runtime, but only when that race has no model of
    /// its own. So an import onto any other race's version is stored at its
    /// base race's path, and an EQDP manipulation turns the selected race's
    /// own model off (and the base race's on, should the item lack one), the
    /// way TexTools/Penumbra mods cover every race from one base model.
    /// Returns a user-facing note when that redirect happened.
    /// </summary>
    public Task<string?> ImportModelAsync(EquipmentItem item, string modelPath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var resolved = _resolver.Resolve(item);
            var targetPath = resolved.MdlPath;
            string? note = null;
            var manipulations = new List<JsonObject>();

            var selectedRace = SelectedRace(item, resolved);
            if (selectedRace is not null && AssetPathResolver.GenderBaseRace(selectedRace) is var baseRace
                && baseRace != selectedRace)
            {
                targetPath = _resolver.GetEquipmentModelPath(item, baseRace);
                // Template: the base model when there is one (its materials
                // and attributes are what the game pairs with it), else the
                // model the selected version shows now.
                if (_assets.FileExists(targetPath))
                    resolved = _resolver.ResolveForRace(item, baseRace);

                if (((_resolver.EffectiveEqdpBits(item, selectedRace) ?? 0) & 2) != 0)
                    manipulations.Add(EqdpManipulation(item, selectedRace, (_resolver.VanillaEqdpBits(item, selectedRace) ?? 0) & 1));
                if (((_resolver.EffectiveEqdpBits(item, baseRace) ?? 0) & 2) == 0)
                    manipulations.Add(EqdpManipulation(item, baseRace, ((_resolver.VanillaEqdpBits(item, baseRace) ?? 0) & 1) | 2));

                note = $"Imported onto the {RaceLabel(baseRace)} base model; {RaceLabel(selectedRace)} now uses it " +
                       "and the game reshapes it for that race.";
            }

            var template = ParseEffectiveModel(resolved);
            var import = IsFbxPath(modelPath)
                ? FbxImporter.Import(modelPath, template)
                : GltfImporter.Import(modelPath, template);
            var written = MdlWriter.Write(template, import.Meshes, import.BoneTables, import.BoneNames);

            // Sanity: our own parser must accept what we are about to store.
            var check = MdlParser.Parse(written);
            if (check.Meshes.Count != import.Meshes.Count || check.BoneNames.Count != import.BoneNames.Count)
                throw new ModelImportException("Internal error: the rebuilt model failed verification.");

            if (import.AddedBones.Count > 0)
                _logger.LogInformation("Import added {Count} bones the original model did not use: {Bones}",
                    import.AddedBones.Count, string.Join(", ", import.AddedBones));

            Store(targetPath, SessionAssetKind.Model, written);
            foreach (var manipulation in manipulations)
                StoreManipulation(manipulation);
            _logger.LogInformation("Imported {Model} as the model for {Path} ({Meshes} meshes, {Manipulations} EQDP changes)",
                modelPath, targetPath, import.Meshes.Count, manipulations.Count);
            return note;
        }, ct);
    }

    private static readonly Regex EquipmentRaceCode = new(@"/c(\d{4})[ea]\d{4}_[a-z]{3}\.mdl$", RegexOptions.Compiled);

    /// <summary>The race version being edited: the selector's choice, else the race code in the resolved model path.</summary>
    private string? SelectedRace(EquipmentItem item, ResolvedModelInfo resolved)
    {
        if (item.IsWeapon || item.IsBodyPart || !AssetPathResolver.TryEqdpSlot(item, out _))
            return null;
        if (_resolver.PreferredRaceCode is { } preferred)
            return preferred;
        var match = EquipmentRaceCode.Match(resolved.MdlPath);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static JsonObject EqdpManipulation(EquipmentItem item, string raceCode, int bits)
    {
        AssetPathResolver.TryEqdpSlot(item, out var slot);
        return EqdpTable.Manipulation(raceCode, (ushort)item.ModelId, slot, bits);
    }

    private static string RaceLabel(string raceCode) =>
        AssetPathResolver.KnownRaces.FirstOrDefault(r => r.Code == raceCode)?.Label ?? $"c{raceCode}";

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

    /// <summary>
    /// Imports an image file as the replacement for a texture. Any size and
    /// aspect ratio is accepted: the user may be remapping UVs or knowingly
    /// swapping in a different layout.
    /// </summary>
    public Task ImportTextureAsync(string texPath, string imagePath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var (width, height, rgba) = ImageIo.DecodeImageFile(imagePath);
            if (width <= 0 || height <= 0)
                throw new InvalidDataException("The image is empty.");

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

    internal static string TextureRole(string texPath) => TextureRoles.Classify(texPath).ToString();
}
