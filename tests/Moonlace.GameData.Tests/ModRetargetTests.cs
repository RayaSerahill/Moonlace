using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Moonlace.Core.Models;
using Moonlace.Core.Penumbra;
using Moonlace.Core.Session;
using Moonlace.GameData.Import;
using Moonlace.GameData.Items;
using Moonlace.GameData.ModTools;
using Moonlace.GameData.Parsing;
using Moonlace.GameData.Resolution;

namespace Moonlace.GameData.Tests;

/// <summary>
/// Mod retargeting: a modpack built from one item's real game files is
/// rewired onto a different item + race/gender, saved as a new .pmp, and that
/// output imports back into Moonlace for viewing. Real game data, read-only.
/// </summary>
public sealed class ModRetargetTests : IDisposable
{
    private readonly LuminaGameDataService _service = new(NullLogger<LuminaGameDataService>.Instance);
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "moonlace-retarget-tests-" + Guid.NewGuid().ToString("N"));

    private static string? FindGameDir()
    {
        var env = Environment.GetEnvironmentVariable("MOONLACE_TEST_GAME_DIR");
        if (env is not null && Directory.Exists(Path.Combine(env, "sqpack")))
            return env;
        const string local = "/mnt/games/pelit/installs/ffxiv/game";
        return Directory.Exists(Path.Combine(local, "sqpack")) ? local : null;
    }

    private bool TryInit()
    {
        var dir = FindGameDir();
        if (dir is null)
            return false;
        _service.InitializeAsync(dir).GetAwaiter().GetResult();
        return true;
    }

    public void Dispose()
    {
        _service.Dispose();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private (AssetPathResolver Resolver, SessionService Session, RenderModelBuilder Builder,
        IReadOnlyList<EquipmentItem> Items, ModRetargeter Retargeter, ModpackImporter Importer,
        EffectiveAssetProvider Assets) CreateStack()
    {
        var session = new SessionService(NullLogger<SessionService>.Instance, Path.Combine(_tempRoot, "sessions"));
        var link = new PenumbraLinkService(NullLogger<PenumbraLinkService>.Instance);
        var assets = new EffectiveAssetProvider(_service, session, link);
        var resolver = new AssetPathResolver(_service, assets, NullLogger<AssetPathResolver>.Instance);
        var textures = new TextureDecoder(_service, assets, NullLogger<TextureDecoder>.Instance);
        var builder = new RenderModelBuilder(assets, resolver, textures, NullLogger<RenderModelBuilder>.Instance);
        var repo = new ItemRepository(_service, NullLogger<ItemRepository>.Instance);
        var items = repo.GetEquipmentItemsAsync().GetAwaiter().GetResult();
        var retargeter = new ModRetargeter(_service, resolver, repo, link, NullLogger<ModRetargeter>.Instance);
        var importer = new ModpackImporter(session, link, NullLogger<ModpackImporter>.Instance);
        return (resolver, session, builder, items, retargeter, importer, assets);
    }

    /// <summary>A body item with a c0101 model — Abes Jacket (the canonical example) when present.</summary>
    private static EquipmentItem PickSourceItem(IReadOnlyList<EquipmentItem> items, AssetPathResolver resolver)
    {
        var candidates = items.Where(i => !i.IsWeapon && !i.IsBodyPart && i.Slot == EquipSlot.Body).ToArray();
        return candidates.FirstOrDefault(i => i.Name == "Abes Jacket")
            ?? candidates.First(i => resolver.GetAvailableVariants(i).Any(v => v.Code == "0101"));
    }

    /// <summary>
    /// Builds a .pmp from the source item's real c0101 game files: the model,
    /// its race-coded textures, and (optionally) its item-owned materials.
    /// </summary>
    private (string PmpPath, EquipmentItem Source, string SetToken, IReadOnlyList<string> GamePaths) CreateGearPmp(
        (AssetPathResolver Resolver, SessionService Session, RenderModelBuilder Builder,
            IReadOnlyList<EquipmentItem> Items, ModRetargeter Retargeter, ModpackImporter Importer,
            EffectiveAssetProvider Assets) stack,
        bool includeMaterials)
    {
        var item = PickSourceItem(stack.Items, stack.Resolver);
        var resolved = stack.Resolver.ResolveForRace(item, "0101");
        var setToken = $"c0101e{item.ModelId:D4}";

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [resolved.MdlPath] = stack.Assets.TryReadFile(resolved.MdlPath)!,
        };

        var model = MdlParser.Parse(files[resolved.MdlPath]);
        foreach (var name in model.MaterialNames.Distinct(StringComparer.Ordinal))
        {
            if (!name.Contains(setToken, StringComparison.Ordinal))
                continue;
            var mtrlPath = stack.Resolver.ResolveMaterialPath(resolved, name);
            var mtrl = stack.Assets.TryReadFile(mtrlPath);
            if (mtrl is null)
                continue;
            if (includeMaterials)
                files[mtrlPath] = mtrl;
            foreach (var texPath in MtrlParser.Parse(mtrl).TexturePaths)
            {
                if (texPath.Contains(setToken, StringComparison.Ordinal)
                    && stack.Assets.TryReadFile(texPath) is { } tex)
                    files[texPath] = tex;
            }
        }

        var modDir = Path.Combine(_tempRoot, "src-mod-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(modDir, "files"));
        var fileNodes = new JsonObject();
        var index = 0;
        foreach (var (gamePath, bytes) in files)
        {
            var rel = $"files/{index++}.dat";
            File.WriteAllBytes(Path.Combine(modDir, rel.Replace('/', Path.DirectorySeparatorChar)), bytes);
            fileNodes[gamePath] = rel.Replace('/', '\\');
        }

        File.WriteAllText(Path.Combine(modDir, "meta.json"), new JsonObject
        {
            ["FileVersion"] = 3,
            ["Name"] = "Retarget Source",
            ["Author"] = "",
            ["Description"] = "",
            ["Version"] = "1.0.0",
            ["Website"] = "",
        }.ToJsonString());
        File.WriteAllText(Path.Combine(modDir, "default_mod.json"), new JsonObject
        {
            ["Name"] = "",
            ["Priority"] = 0,
            ["Files"] = fileNodes,
            ["FileSwaps"] = new JsonObject(),
            ["Manipulations"] = new JsonArray(),
        }.ToJsonString());

        var pmpPath = modDir + ".pmp";
        ZipFile.CreateFromDirectory(modDir, pmpPath);
        return (pmpPath, item, setToken, files.Keys.ToArray());
    }

    private static Dictionary<string, byte[]> ReadPmp(string pmpPath, out Dictionary<string, string> fileMap)
    {
        using var zip = ZipFile.OpenRead(pmpPath);
        using var defaultMod = JsonDocument.Parse(
            new StreamReader(zip.GetEntry("default_mod.json")!.Open()).ReadToEnd());
        fileMap = defaultMod.RootElement.GetProperty("Files").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!.Replace('\\', '/'), StringComparer.Ordinal);

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (gamePath, rel) in fileMap)
        {
            using var stream = zip.GetEntry(rel)!.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            files[gamePath] = memory.ToArray();
        }

        return files;
    }

    [SkippableFact]
    public async Task AnalyzeFindsGearBinding()
    {
        Skip.IfNot(TryInit());
        var stack = CreateStack();
        var (pmp, source, _, gamePaths) = CreateGearPmp(stack, includeMaterials: true);

        var analysis = await stack.Retargeter.AnalyzeAsync(pmp);

        Assert.Equal("Retarget Source", analysis.ModName);
        var binding = Assert.Single(analysis.Bindings);
        Assert.Equal($"e{source.ModelId:D4}", binding.SetCode);
        Assert.Equal("0101", binding.RaceCode);
        Assert.Equal(EquipSlot.Body, binding.Slot);
        Assert.Contains(source.Name, binding.ItemNames);
        Assert.Equal(gamePaths.Count, binding.GamePaths.Count);
        Assert.Equal(0, analysis.CarriedFileCount);
    }

    [SkippableFact]
    public async Task RetargetRewiresPathsAndImportsForViewing()
    {
        Skip.IfNot(TryInit());
        var stack = CreateStack();
        var (pmp, source, srcToken, gamePaths) = CreateGearPmp(stack, includeMaterials: true);
        var binding = Assert.Single((await stack.Retargeter.AnalyzeAsync(pmp)).Bindings);

        var destination = stack.Items.First(i =>
            !i.IsWeapon && !i.IsBodyPart && i.Slot == EquipSlot.Body && i.ModelId != source.ModelId);
        var dstToken = $"c0801e{destination.ModelId:D4}";
        var output = Path.Combine(_tempRoot, "retargeted.pmp");

        var report = await stack.Retargeter.RetargetAsync(pmp, binding, destination, "0801", output);
        Assert.Equal(gamePaths.Count, report.FilesRewired);
        Assert.Equal(0, report.FilesCarried);

        // Every redirection now points at the destination's paths.
        var files = ReadPmp(output, out _);
        Assert.All(files.Keys, k => Assert.DoesNotContain(srcToken, k));
        var mdlKey = Assert.Single(files.Keys, k => k.EndsWith(".mdl", StringComparison.Ordinal));
        Assert.Contains(dstToken, mdlKey);
        Assert.Contains(files.Keys, k => k.EndsWith(".mtrl", StringComparison.Ordinal) && k.Contains(dstToken));

        // The model's material names are re-coded, and skin references point
        // at a race the game ships skin for (target, or its gender base).
        var model = MdlParser.Parse(files[mdlKey]);
        Assert.DoesNotContain(model.MaterialNames, n => n.Contains(srcToken));
        Assert.Contains(model.MaterialNames, n => n.Contains(dstToken));
        Assert.DoesNotContain(model.MaterialNames, n => n.StartsWith("/mt_c0101b", StringComparison.Ordinal));

        // Rewired materials reference the moved textures at their new paths.
        foreach (var (path, bytes) in files.Where(f => f.Key.EndsWith(".mtrl", StringComparison.Ordinal)))
        {
            foreach (var tex in MtrlParser.Parse(bytes).TexturePaths)
                Assert.DoesNotContain(srcToken, tex);
            Assert.Contains(dstToken, path);
        }

        // The saved .pmp imports into a session for the destination item and
        // the viewport can build the new race version from it.
        stack.Session.ActivateForItem(destination);
        var importReport = await stack.Importer.ImportAsync(output);
        Assert.Equal(files.Count, importReport.FilesImported);

        Assert.Contains(stack.Resolver.GetAvailableVariants(destination), v => v.Code == "0801");
        stack.Resolver.PreferredRaceCode = "0801";
        var render = await stack.Builder.LoadAsync(destination);
        Assert.NotEmpty(render.Meshes);
        Assert.Contains(render.Meshes, m => m.Material.GamePath.Contains(dstToken));
    }

    [SkippableFact]
    public async Task RetargetPullsGameMaterialsTheModDoesNotShip()
    {
        Skip.IfNot(TryInit());
        var stack = CreateStack();
        var (pmp, source, _, _) = CreateGearPmp(stack, includeMaterials: false);
        var binding = Assert.Single((await stack.Retargeter.AnalyzeAsync(pmp)).Bindings);

        var destination = stack.Items.First(i =>
            !i.IsWeapon && !i.IsBodyPart && i.Slot == EquipSlot.Body && i.ModelId != source.ModelId);
        var dstToken = $"c0801e{destination.ModelId:D4}";
        var output = Path.Combine(_tempRoot, "retargeted-pulled.pmp");

        var report = await stack.Retargeter.RetargetAsync(pmp, binding, destination, "0801", output);
        Assert.True(report.MaterialsPulledIn > 0, "the mdl-only mod must pull its materials from the game");

        // The pulled material sits on the destination's paths and follows the
        // mod's redirected textures; untouched references keep their original
        // (still valid) game paths.
        var files = ReadPmp(output, out _);
        var mtrlKey = files.Keys.First(k => k.EndsWith(".mtrl", StringComparison.Ordinal) && k.Contains(dstToken));
        var texturePaths = MtrlParser.Parse(files[mtrlKey]).TexturePaths;
        Assert.Contains(texturePaths, t => files.ContainsKey(t) && t.Contains(dstToken));
    }
}
