using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Moonlace.Core.Models;
using Moonlace.Core.Penumbra;
using Moonlace.Core.Session;
using Moonlace.GameData.Import;

namespace Moonlace.GameData.Tests;

/// <summary>
/// Modpack import as edits: a .pmp's effective files (default option
/// selection) land in the active session, or in the linked Penumbra mod
/// while live editing. Pure file-system tests — no game data needed.
/// </summary>
public sealed class ModpackImportTests : IDisposable
{
    private static readonly byte[] DefaultMtrl = [0xD0, 0xD1, 0xD2];
    private static readonly byte[] RedMtrl = [0xE0, 0xE1];
    private static readonly byte[] BlueMtrl = [0xB0, 0xB1, 0xB2, 0xB3];
    private static readonly byte[] Avfx = [0xAF, 0xFA];

    private const string MtrlGamePath = "chara/equipment/e0001/material/v0001/mt_test.mtrl";
    private const string AvfxGamePath = "vfx/common/eff/test.avfx";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "moonlace-import-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static EquipmentItem TestItem() => new()
    {
        RowId = 4242,
        Name = "Import Test Body",
        Slot = EquipSlot.Body,
        ModelId = 1,
        SecondaryId = 0,
        Variant = 1,
    };

    /// <summary>
    /// A .pmp with a default file, a Single group whose default is the second
    /// option ("Blue"), a non-game pseudo path, and one IMC manipulation.
    /// </summary>
    private string CreateTestPmp()
    {
        var modDir = Path.Combine(_root, "mod-src");
        Directory.CreateDirectory(Path.Combine(modDir, "files"));
        File.WriteAllBytes(Path.Combine(modDir, "files", "default.mtrl"), DefaultMtrl);
        File.WriteAllBytes(Path.Combine(modDir, "files", "opt_red.mtrl"), RedMtrl);
        File.WriteAllBytes(Path.Combine(modDir, "files", "opt_blue.mtrl"), BlueMtrl);
        File.WriteAllBytes(Path.Combine(modDir, "files", "extra.avfx"), Avfx);
        File.WriteAllBytes(Path.Combine(modDir, "files", "unused.tex"), [1, 2, 3]);

        File.WriteAllText(Path.Combine(modDir, "meta.json"), new JsonObject
        {
            ["FileVersion"] = 3,
            ["Name"] = "Import Test",
            ["Author"] = "",
            ["Description"] = "",
            ["Version"] = "1.0.0",
            ["Website"] = "",
        }.ToJsonString());

        File.WriteAllText(Path.Combine(modDir, "default_mod.json"), new JsonObject
        {
            ["Name"] = "",
            ["Priority"] = 0,
            ["Files"] = new JsonObject
            {
                [MtrlGamePath] = "files\\default.mtrl",
                [AvfxGamePath] = "files\\extra.avfx",
                ["Unused Option/chara/equipment/e0001/texture/unused.tex"] = "files\\unused.tex",
            },
            ["FileSwaps"] = new JsonObject(),
            ["Manipulations"] = new JsonArray(new JsonObject { ["Type"] = "Imc" }),
        }.ToJsonString());

        File.WriteAllText(Path.Combine(modDir, "group_001_color.json"), new JsonObject
        {
            ["Name"] = "Color",
            ["Description"] = "",
            ["Priority"] = 1,
            ["Type"] = "Single",
            ["DefaultSettings"] = 1,
            ["Options"] = new JsonArray(
                new JsonObject
                {
                    ["Name"] = "Red",
                    ["Files"] = new JsonObject { [MtrlGamePath] = "files\\opt_red.mtrl" },
                },
                new JsonObject
                {
                    ["Name"] = "Blue",
                    ["Files"] = new JsonObject { [MtrlGamePath] = "files\\opt_blue.mtrl" },
                }),
        }.ToJsonString());

        var pmpPath = Path.Combine(_root, "import-test.pmp");
        ZipFile.CreateFromDirectory(modDir, pmpPath);
        return pmpPath;
    }

    private (SessionService Session, PenumbraLinkService Link, ModpackImporter Importer) CreateStack()
    {
        var session = new SessionService(NullLogger<SessionService>.Instance, Path.Combine(_root, "sessions"));
        var link = new PenumbraLinkService(NullLogger<PenumbraLinkService>.Instance);
        var importer = new ModpackImporter(session, link, NullLogger<ModpackImporter>.Instance);
        return (session, link, importer);
    }

    [Fact]
    public async Task ImportIntoSessionAppliesDefaultSelectionAndSkipsPseudoPaths()
    {
        var (session, _, importer) = CreateStack();
        session.ActivateForItem(TestItem());

        var report = await importer.ImportAsync(CreateTestPmp());

        Assert.Equal("Import Test", report.ModName);
        Assert.Equal(2, report.FilesImported);
        Assert.Equal(1, report.PseudoPathsSkipped);

        // The Single group's default is option 1 ("Blue"), which overrides the default file.
        Assert.Equal(BlueMtrl, session.TryReadAsset(MtrlGamePath));
        Assert.Equal(Avfx, session.TryReadAsset(AvfxGamePath));

        Assert.Contains(session.Entries, e => e.GamePath == MtrlGamePath && e.Kind == SessionAssetKind.Material);
        Assert.Contains(session.Entries, e => e.GamePath == AvfxGamePath && e.Kind == SessionAssetKind.Other);
        Assert.DoesNotContain(session.Entries, e => e.GamePath.EndsWith("unused.tex", StringComparison.Ordinal));

        // The unselected "Red" option and the IMC manipulation are called out.
        Assert.Contains(report.Warnings, w => w.Contains("1 other option"));
        Assert.Contains(report.Warnings, w => w.Contains("manipulation"));
    }

    [Fact]
    public async Task ImportWithoutItemOrLinkFails()
    {
        var (_, _, importer) = CreateStack();
        Assert.Null(importer.DescribeDestination());
        await Assert.ThrowsAsync<ModpackImportException>(() => importer.ImportAsync(CreateTestPmp()));
    }

    [Fact]
    public async Task ImportIntoLinkedModWritesLiveEdits()
    {
        var (session, link, importer) = CreateStack();

        // A minimal legacy-layout mod to live edit; no item selection needed.
        var targetDir = Path.Combine(_root, "target-mod");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "meta.json"), new JsonObject
        {
            ["FileVersion"] = 3,
            ["Name"] = "Target Mod",
        }.ToJsonString());
        File.WriteAllText(Path.Combine(targetDir, "default_mod.json"), new JsonObject
        {
            ["Name"] = "",
            ["Priority"] = 0,
            ["Files"] = new JsonObject(),
            ["FileSwaps"] = new JsonObject(),
            ["Manipulations"] = new JsonArray(),
        }.ToJsonString());
        link.Link(targetDir, []);

        var report = await importer.ImportAsync(CreateTestPmp());

        Assert.Equal(2, report.FilesImported);
        Assert.Contains("Target Mod", report.Destination);

        // Edits are live in the mod folder, registered and revertible; the session stays empty.
        Assert.Equal(BlueMtrl, link.TryReadAsset(MtrlGamePath));
        Assert.Equal(Avfx, link.TryReadAsset(AvfxGamePath));
        Assert.True(link.ChangedFileCount > 0);
        Assert.Empty(session.Entries);

        var defaultMod = JsonNode.Parse(File.ReadAllText(Path.Combine(targetDir, "default_mod.json")))!.AsObject();
        Assert.NotNull(defaultMod["Files"]![MtrlGamePath]);

        link.RevertAll();
        Assert.Null(link.TryReadAsset(MtrlGamePath));
    }
}
