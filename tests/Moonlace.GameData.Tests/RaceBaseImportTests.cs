using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moonlace.Core.Models;
using Moonlace.Core.Session;
using Moonlace.GameData.Editing;
using Moonlace.GameData.Export;
using Moonlace.GameData.Items;
using Moonlace.GameData.Meta;
using Moonlace.GameData.Resolution;

namespace Moonlace.GameData.Tests;

/// <summary>
/// Races built on a base body: EQDP reading, the game's model fallback chain
/// (Miqo'te ♀ → Midlander ♀ → Midlander ♂), and imports onto a non-base
/// race landing on the base model with an EQDP manipulation, the way
/// TexTools/Penumbra mods cover every race. Game data is only ever read.
/// </summary>
public sealed class RaceBaseImportTests : IDisposable
{
    private readonly LuminaGameDataService _service = new(NullLogger<LuminaGameDataService>.Instance);
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "moonlace-racebase-tests-" + Guid.NewGuid().ToString("N"));

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

    private (SessionService Session, ItemEditingService Editing, AssetPathResolver Resolver, EquipmentItem Item) CreateStack(string itemName)
    {
        var session = new SessionService(NullLogger<SessionService>.Instance, Path.Combine(_tempRoot, "sessions"));
        var link = new Moonlace.Core.Penumbra.PenumbraLinkService(NullLogger<Moonlace.Core.Penumbra.PenumbraLinkService>.Instance);
        var assets = new EffectiveAssetProvider(_service, session, link);
        var resolver = new AssetPathResolver(_service, assets, NullLogger<AssetPathResolver>.Instance);
        var textures = new TextureDecoder(_service, assets, NullLogger<TextureDecoder>.Instance);
        var editing = new ItemEditingService(assets, resolver, textures, session, link, NullLogger<ItemEditingService>.Instance);

        var repo = new ItemRepository(_service, NullLogger<ItemRepository>.Instance);
        var items = repo.GetEquipmentItemsAsync().GetAwaiter().GetResult();
        var item = items.First(i => i.Name == itemName);
        session.ActivateForItem(item);
        return (session, editing, resolver, item);
    }

    // Linen Bliaud is e0016 body: Miqo'te ♀ (c0801) ships its own model,
    // Au Ra ♀ (c1401) has none and falls back to Midlander ♀ in game.
    private const string Bliaud = "Linen Bliaud";
    private const string C0201 = "chara/equipment/e0016/model/c0201e0016_top.mdl";
    private const string C0801 = "chara/equipment/e0016/model/c0801e0016_top.mdl";

    [SkippableFact]
    public void EqdpTableReadsTheGamesModelFlags()
    {
        Skip.IfNot(TryInit());
        var eqdp = new EqdpTable(_service);
        Assert.Equal(2, eqdp.VanillaBits("0801", 16, "Body", accessory: false)); // own model, shared material
        Assert.Equal(3, eqdp.VanillaBits("0201", 16, "Body", accessory: false));
        Assert.Equal(0, (eqdp.VanillaBits("1401", 16, "Body", accessory: false) ?? 0) & 2);

        // A manipulation overrides the game's value; its Entry is shifted to the slot.
        var off = EqdpTable.Manipulation("0801", 16, "Body", 0);
        Assert.Equal(0, eqdp.EffectiveBits("0801", 16, "Body", false, [off]));
        var on = EqdpTable.Manipulation("1401", 16, "Body", 3);
        Assert.Equal(3 << 2, on["Manipulation"]!["Entry"]!.GetValue<int>());
        Assert.Equal("AuRa", on["Manipulation"]!["Race"]!.GetValue<string>());
    }

    [SkippableFact]
    public void FemaleRaceWithoutItsOwnModelPreviewsTheFemaleBaseNotTheMaleModel()
    {
        Skip.IfNot(TryInit());
        var (_, _, resolver, item) = CreateStack(Bliaud);

        resolver.PreferredRaceCode = "1401";
        Assert.Equal(C0201, resolver.Resolve(item).MdlPath);
    }

    [SkippableFact]
    public async Task ImportOntoMiqoteFemaleLandsOnTheMidlanderFemaleBase()
    {
        Skip.IfNot(TryInit());
        var (session, editing, resolver, item) = CreateStack(Bliaud);

        // A Midlander ♀-shaped model, like a body kit export.
        resolver.PreferredRaceCode = "0201";
        var glb = Path.Combine(_tempRoot, "base.glb");
        Directory.CreateDirectory(_tempRoot);
        await editing.ExportModelAsync(item, glb);

        resolver.PreferredRaceCode = "0801";
        var note = await editing.ImportModelAsync(item, glb);

        Assert.NotNull(note);
        Assert.Contains("Midlander ♀", note);
        // Stored on the base path only; the Miqo'te ♀ file stays vanilla.
        Assert.Contains(session.Entries, e => e.GamePath == C0201);
        Assert.DoesNotContain(session.Entries, e => e.GamePath == C0801);

        // One manipulation: Miqo'te ♀ loses its own model (material bit kept as vanilla, which is 0).
        var manipulation = Assert.Single(session.Manipulations);
        Assert.Equal("Eqdp", manipulation["Type"]!.GetValue<string>());
        Assert.Equal("Miqote", manipulation["Manipulation"]!["Race"]!.GetValue<string>());
        Assert.Equal("Female", manipulation["Manipulation"]!["Gender"]!.GetValue<string>());
        Assert.Equal(0, manipulation["Manipulation"]!["Entry"]!.GetValue<int>());

        // Moonlace now previews Miqo'te ♀ the way the game shows it.
        Assert.Equal(C0201, resolver.Resolve(item).MdlPath);
        Assert.Contains(resolver.GetAvailableVariants(item), v => v.Code == "0801" && v.Label.Contains("uses Midlander ♀"));

        // Importing again does not stack manipulations.
        await editing.ImportModelAsync(item, glb);
        Assert.Single(session.Manipulations);

        // The PMP carries the manipulation next to the model.
        var pmp = Path.Combine(_tempRoot, "out.pmp");
        await editing.ExportPmpAsync(new PmpMetadata { Name = "Race base test" }, pmp);
        using var zip = ZipFile.OpenRead(pmp);
        using var reader = new StreamReader(zip.GetEntry("default_mod.json")!.Open());
        var defaultMod = JsonDocument.Parse(reader.ReadToEnd()).RootElement;
        Assert.Equal(1, defaultMod.GetProperty("Manipulations").GetArrayLength());
        Assert.True(defaultMod.GetProperty("Files").TryGetProperty(C0201, out _));
    }

    [SkippableFact]
    public async Task ImportOntoABaseRaceNeedsNoManipulation()
    {
        Skip.IfNot(TryInit());
        var (session, editing, resolver, item) = CreateStack(Bliaud);

        resolver.PreferredRaceCode = "0201";
        var glb = Path.Combine(_tempRoot, "base2.glb");
        Directory.CreateDirectory(_tempRoot);
        await editing.ExportModelAsync(item, glb);
        var note = await editing.ImportModelAsync(item, glb);

        Assert.Null(note);
        Assert.Empty(session.Manipulations);
        Assert.Contains(session.Entries, e => e.GamePath == C0201);
    }
}
