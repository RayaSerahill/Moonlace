using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moonlace.Core.Penumbra;

namespace Moonlace.Core.Tests;

/// <summary>
/// Penumbra live-edit link: mod parsing (FileVersion 4 and legacy 3),
/// option-aware file mapping, backup-before-edit, revert, and option changes
/// that preserve edits. All against fabricated mod folders on disk.
/// </summary>
public sealed class PenumbraLinkServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "moonlace-penumbra-tests-" + Guid.NewGuid().ToString("N"));

    private PenumbraLinkService Create() => new(NullLogger<PenumbraLinkService>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>A FileVersion 4 mod: everything inline in meta.json, like current Penumbra writes.</summary>
    private string CreateV4Mod()
    {
        var dir = Path.Combine(_root, "v4mod");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "base.mdl"), Encoding.UTF8.GetBytes("base model"));
        File.WriteAllBytes(Path.Combine(dir, "marie.mdl"), Encoding.UTF8.GetBytes("marie model"));
        Directory.CreateDirectory(Path.Combine(dir, "mats"));
        File.WriteAllBytes(Path.Combine(dir, "mats", "ring.mtrl"), Encoding.UTF8.GetBytes("ring material"));
        File.WriteAllBytes(Path.Combine(dir, "mats", "invis.mtrl"), Encoding.UTF8.GetBytes("invisible material"));

        var meta = new
        {
            FileVersion = 4,
            Name = "Test Trinity",
            DefaultData = new
            {
                Files = new Dictionary<string, string>
                {
                    ["chara/accessory/a0053/model/c0801a0053_rir.mdl"] = "base.mdl",
                    ["chara/accessory/a0053/material/v0001/mt_rir_b.mtrl"] = "mats\\ring.mtrl",
                },
            },
            Groups = new object[]
            {
                new
                {
                    Type = "Single",
                    Name = "Sculpt",
                    Priority = 3,
                    Options = new object[]
                    {
                        new { Name = "Vanilla" },
                        new
                        {
                            Name = "Marie",
                            Files = new Dictionary<string, string>
                            {
                                ["chara/accessory/a0053/model/c0801a0053_rir.mdl"] = "marie.mdl",
                            },
                        },
                    },
                },
                new
                {
                    Type = "Multi",
                    Name = "Hide Toggles",
                    Priority = 5,
                    Options = new object[]
                    {
                        new
                        {
                            Name = "Right ring",
                            Priority = 2,
                            Files = new Dictionary<string, string>
                            {
                                ["chara/accessory/a0053/material/v0001/mt_rir_b.mtrl"] = "mats\\invis.mtrl",
                            },
                        },
                    },
                },
            },
        };
        File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(meta));
        return dir;
    }

    /// <summary>A legacy FileVersion 3 mod: default_mod.json plus group files.</summary>
    private string CreateV3Mod()
    {
        var dir = Path.Combine(_root, "v3mod");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.tex"), Encoding.UTF8.GetBytes("texture a"));
        File.WriteAllBytes(Path.Combine(dir, "b.tex"), Encoding.UTF8.GetBytes("texture b"));

        File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(new
        {
            FileVersion = 3,
            Name = "Legacy Mod",
        }));
        File.WriteAllText(Path.Combine(dir, "default_mod.json"), JsonSerializer.Serialize(new
        {
            Name = "",
            Priority = 0,
            Files = new Dictionary<string, string> { ["chara/x/a.tex"] = "a.tex" },
        }));
        File.WriteAllText(Path.Combine(dir, "group_001_variant.json"), JsonSerializer.Serialize(new
        {
            Name = "Variant",
            Type = "Single",
            Priority = 1,
            DefaultSettings = 1,
            Options = new object[]
            {
                new { Name = "A" },
                new
                {
                    Name = "B",
                    Files = new Dictionary<string, string> { ["chara/x/a.tex"] = "b.tex" },
                },
            },
        }));
        return dir;
    }

    [Fact]
    public void InspectParsesV4ModInline()
    {
        var link = Create();
        var info = link.Inspect(CreateV4Mod());

        Assert.Equal("Test Trinity", info.Name);
        Assert.Equal(2, info.DefaultFiles.Count);
        Assert.Equal(2, info.Groups.Count);
        Assert.Equal(PenumbraGroupType.Single, info.Groups[0].Type);
        Assert.Equal("Vanilla", info.Groups[0].Options[0].Name);
        Assert.Empty(info.Groups[0].Options[0].Files);
        Assert.Equal(PenumbraGroupType.Multi, info.Groups[1].Type);
    }

    [Fact]
    public void InspectParsesLegacyV3ModAndDefaultSettings()
    {
        var link = Create();
        var info = link.Inspect(CreateV3Mod());

        Assert.Equal("Legacy Mod", info.Name);
        Assert.Single(info.DefaultFiles);
        var group = Assert.Single(info.Groups);
        Assert.Equal([1], group.DefaultSelection());
    }

    [Fact]
    public void InspectRejectsNonModDirectory()
    {
        var dir = Path.Combine(_root, "notamod");
        Directory.CreateDirectory(dir);

        var ex = Assert.Throws<PenumbraLinkException>(() => Create().Inspect(dir));
        Assert.Contains("meta.json", ex.Message);
    }

    [Fact]
    public void FileMapFollowsSelectedOptionsAndPriorities()
    {
        var link = Create();
        var dir = CreateV4Mod();

        // Vanilla sculpt, no hide toggles: defaults only.
        link.Link(dir, [[0], []]);
        Assert.Equal("base model", Encoding.UTF8.GetString(
            link.TryReadAsset("chara/accessory/a0053/model/c0801a0053_rir.mdl")!));
        Assert.Equal("ring material", Encoding.UTF8.GetString(
            link.TryReadAsset("chara/accessory/a0053/material/v0001/mt_rir_b.mtrl")!));

        // Marie sculpt + hide right ring: options override the defaults.
        link.SetSelection([[1], [0]]);
        Assert.Equal("marie model", Encoding.UTF8.GetString(
            link.TryReadAsset("chara/accessory/a0053/model/c0801a0053_rir.mdl")!));
        Assert.Equal("invisible material", Encoding.UTF8.GetString(
            link.TryReadAsset("chara/accessory/a0053/material/v0001/mt_rir_b.mtrl")!));

        // Unmapped paths stay with the game.
        Assert.Null(link.TryReadAsset("chara/somewhere/else.tex"));
        Assert.Equal(0, link.GetRevision("chara/somewhere/else.tex"));
        Assert.NotEqual(0, link.GetRevision("chara/accessory/a0053/model/c0801a0053_rir.mdl"));
    }

    [Fact]
    public void FirstEditBacksUpTheOriginalOnceAndRevertRestoresIt()
    {
        var link = Create();
        var dir = CreateV4Mod();
        link.Link(dir, [[1], []]);
        const string gamePath = "chara/accessory/a0053/model/c0801a0053_rir.mdl";

        link.WriteAsset(gamePath, Encoding.UTF8.GetBytes("edit one"));
        link.WriteAsset(gamePath, Encoding.UTF8.GetBytes("edit two"));

        // The mod file itself now carries the edit, the backup holds the original.
        Assert.Equal("edit two", File.ReadAllText(Path.Combine(dir, "marie.mdl")));
        Assert.Equal("marie model", File.ReadAllText(Path.Combine(dir, ".moonlace-backup", "files", "marie.mdl")));
        Assert.Equal(1, link.ChangedFileCount);
        Assert.True(link.IsChanged(gamePath));

        link.RevertAll();

        Assert.Equal("marie model", File.ReadAllText(Path.Combine(dir, "marie.mdl")));
        Assert.False(Directory.Exists(Path.Combine(dir, ".moonlace-backup")));
        Assert.Equal(0, link.ChangedFileCount);
        Assert.False(link.IsChanged(gamePath));
    }

    [Fact]
    public void EditingAnUncoveredPathRegistersItInTheModAndRevertRemovesIt()
    {
        var link = Create();
        var dir = CreateV4Mod();
        link.Link(dir, [[0], []]);
        const string gamePath = "chara/equipment/e0003/texture/new_d.tex";

        link.WriteAsset(gamePath, Encoding.UTF8.GetBytes("new texture"));

        // Readable back through the link, present on disk, and registered in meta.json.
        Assert.Equal("new texture", Encoding.UTF8.GetString(link.TryReadAsset(gamePath)!));
        var newFile = Path.Combine(dir, "moonlace", "chara", "equipment", "e0003", "texture", "new_d.tex");
        Assert.True(File.Exists(newFile));
        var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "meta.json")));
        Assert.True(meta.RootElement.GetProperty("DefaultData").GetProperty("Files").TryGetProperty(gamePath, out _));

        // Re-inspecting the mod sees the new redirection too (it is real mod content now).
        Assert.True(link.Inspect(dir).DefaultFiles.ContainsKey(gamePath));

        link.RevertAll();

        Assert.False(File.Exists(newFile));
        Assert.Null(link.TryReadAsset(gamePath));
        var reverted = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "meta.json")));
        Assert.False(reverted.RootElement.GetProperty("DefaultData").GetProperty("Files").TryGetProperty(gamePath, out _));
    }

    [Fact]
    public void UncoveredPathRegistrationWorksForLegacyMods()
    {
        var link = Create();
        var dir = CreateV3Mod();
        link.Link(dir, [[0]]);
        const string gamePath = "chara/x/new.tex";

        link.WriteAsset(gamePath, Encoding.UTF8.GetBytes("new"));

        var defaultMod = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "default_mod.json")));
        Assert.True(defaultMod.RootElement.GetProperty("Files").TryGetProperty(gamePath, out _));

        link.RevertAll();
        var reverted = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "default_mod.json")));
        Assert.False(reverted.RootElement.GetProperty("Files").TryGetProperty(gamePath, out _));
    }

    [Fact]
    public void ChangingOptionsKeepsEditsAlreadyOnDisk()
    {
        var link = Create();
        var dir = CreateV4Mod();
        link.Link(dir, [[1], []]);
        const string gamePath = "chara/accessory/a0053/model/c0801a0053_rir.mdl";

        link.WriteAsset(gamePath, Encoding.UTF8.GetBytes("edited marie"));
        link.SetSelection([[0], []]);

        // The path resolves through the new option (base.mdl), the edit stays in marie.mdl.
        Assert.Equal("base model", Encoding.UTF8.GetString(link.TryReadAsset(gamePath)!));
        Assert.Equal("edited marie", File.ReadAllText(Path.Combine(dir, "marie.mdl")));
        Assert.Equal(1, link.ChangedFileCount);

        // Switching back shows the edit again — nothing was lost.
        link.SetSelection([[1], []]);
        Assert.Equal("edited marie", Encoding.UTF8.GetString(link.TryReadAsset(gamePath)!));
    }

    [Fact]
    public void RelinkingFindsBackupsFromAPreviousRun()
    {
        var dir = CreateV4Mod();
        const string gamePath = "chara/accessory/a0053/model/c0801a0053_rir.mdl";

        var first = Create();
        first.Link(dir, [[1], []]);
        first.WriteAsset(gamePath, Encoding.UTF8.GetBytes("edited"));
        first.Unlink();

        // Unlinking keeps the edit and its backup in the mod folder.
        Assert.Equal("edited", File.ReadAllText(Path.Combine(dir, "marie.mdl")));

        var second = Create();
        second.Link(dir, [[1], []]);
        Assert.Equal(1, second.ChangedFileCount);

        second.RevertAll();
        Assert.Equal("marie model", File.ReadAllText(Path.Combine(dir, "marie.mdl")));
    }

    [Fact]
    public void MaterialsMatchAcrossVariantFolders()
    {
        // Mods pin materials to one vNNNN folder and repoint the item there
        // via an IMC manipulation Moonlace does not apply; the same material
        // name in another variant folder must still resolve to the mod file.
        var link = Create();
        link.Link(CreateV4Mod(), [[0], []]);

        var bytes = link.TryReadAsset("chara/accessory/a0053/material/v0008/mt_rir_b.mtrl");
        Assert.Equal("ring material", Encoding.UTF8.GetString(bytes!));
        Assert.NotEqual(0, link.GetRevision("chara/accessory/a0053/material/v0008/mt_rir_b.mtrl"));

        // Writing through the variant-fallback path edits the mod's actual material file.
        link.WriteAsset("chara/accessory/a0053/material/v0008/mt_rir_b.mtrl", Encoding.UTF8.GetBytes("edited"));
        Assert.Equal("edited", Encoding.UTF8.GetString(link.TryReadAsset("chara/accessory/a0053/material/v0001/mt_rir_b.mtrl")!));

        // Different material names or non-materials do not cross-match.
        Assert.Null(link.TryReadAsset("chara/accessory/a0053/material/v0008/mt_rir_x.mtrl"));
    }

    [Fact]
    public void AddedGroupsAndOptionsLandInTheModJsonAndRevertRemovesThem()
    {
        var link = Create();
        var dir = CreateV4Mod();
        link.Link(dir, [[0], []]);

        link.AddGroup("Style", PenumbraGroupType.Single);
        link.AddOption("Style", "Default");
        link.AddOption("Style", "Fancy");

        var info = link.Inspect(dir);
        var style = info.Groups.Single(g => g.Name == "Style");
        Assert.Equal(PenumbraGroupType.Single, style.Type);
        Assert.Equal(["Default", "Fancy"], style.Options.Select(o => o.Name));
        Assert.All(style.Options, o => Assert.Empty(o.Files));

        // Duplicates are refused.
        Assert.Throws<PenumbraLinkException>(() => link.AddGroup("style", PenumbraGroupType.Multi));
        Assert.Throws<PenumbraLinkException>(() => link.AddOption("Style", "fancy"));

        link.RevertAll();
        Assert.DoesNotContain(link.Inspect(dir).Groups, g => g.Name == "Style");
    }

    [Fact]
    public void EditTargetCapturesEditsIntoTheOptionAndDefaultsStayUntouched()
    {
        var link = Create();
        var dir = CreateV4Mod();
        link.Link(dir, [[0], []]);
        const string mdl = "chara/accessory/a0053/model/c0801a0053_rir.mdl";

        link.AddGroup("Style", PenumbraGroupType.Single);
        link.AddOption("Style", "Default");
        link.AddOption("Style", "Fancy");
        link.SetEditTarget("Style", "Fancy");
        Assert.Equal(new PenumbraEditTarget("Style", "Fancy"), link.EditTarget);

        link.WriteAsset(mdl, Encoding.UTF8.GetBytes("fancy model"));

        // The edit is visible through the link but the default file kept its bytes;
        // the redirection was registered in the option, not in DefaultData.
        Assert.Equal("fancy model", Encoding.UTF8.GetString(link.TryReadAsset(mdl)!));
        Assert.Equal("base model", File.ReadAllText(Path.Combine(dir, "base.mdl")));
        var info = link.Inspect(dir);
        Assert.Equal("base.mdl", info.DefaultFiles[mdl]);
        var fancy = info.Groups.Single(g => g.Name == "Style").Options.Single(o => o.Name == "Fancy");
        Assert.True(fancy.Files.ContainsKey(mdl));

        // Selecting the "Default" option instead clears the target and shows the default look…
        link.SetSelection([[0], [], [0]]);
        Assert.Null(link.EditTarget);
        Assert.Equal("base model", Encoding.UTF8.GetString(link.TryReadAsset(mdl)!));

        // …and re-enabling "Fancy" brings the captured edit back.
        link.SetSelection([[0], [], [1]]);
        Assert.Equal("fancy model", Encoding.UTF8.GetString(link.TryReadAsset(mdl)!));

        // Revert removes the option file, the group and the registration.
        link.RevertAll();
        Assert.Equal("base model", Encoding.UTF8.GetString(link.TryReadAsset(mdl)!));
        Assert.DoesNotContain(link.Inspect(dir).Groups, g => g.Name == "Style");
        Assert.False(Directory.Exists(Path.Combine(dir, "moonlace")));
    }

    [Fact]
    public void EditTargetReusesTheModsMaterialVariantKey()
    {
        // The mod maps the material at v0001; an edit requested via v0008
        // must layer over the mod's own key, not invent a second one.
        var link = Create();
        var dir = CreateV4Mod();
        link.Link(dir, [[0], []]);

        link.AddGroup("Tint", PenumbraGroupType.Multi);
        link.AddOption("Tint", "Red");
        link.SetEditTarget("Tint", "Red");

        link.WriteAsset("chara/accessory/a0053/material/v0008/mt_rir_b.mtrl", Encoding.UTF8.GetBytes("red"));

        var red = link.Inspect(dir).Groups.Single(g => g.Name == "Tint").Options.Single();
        var key = Assert.Single(red.Files.Keys);
        Assert.Equal("chara/accessory/a0053/material/v0001/mt_rir_b.mtrl", key, ignoreCase: true);
        Assert.Equal("ring material", File.ReadAllText(Path.Combine(dir, "mats", "ring.mtrl")));
        Assert.Equal("red", Encoding.UTF8.GetString(link.TryReadAsset("chara/accessory/a0053/material/v0001/mt_rir_b.mtrl")!));
    }

    [Fact]
    public void GroupAuthoringWorksForLegacyModsViaGroupFiles()
    {
        var link = Create();
        var dir = CreateV3Mod();
        link.Link(dir, [[0]]);

        link.AddGroup("Extras", PenumbraGroupType.Multi);
        link.AddOption("Extras", "Glow");
        link.SetEditTarget("Extras", "Glow");
        link.WriteAsset("chara/x/new.tex", Encoding.UTF8.GetBytes("glow"));

        var groupFile = Path.Combine(dir, "group_002_extras.json");
        Assert.True(File.Exists(groupFile));
        var parsed = JsonDocument.Parse(File.ReadAllText(groupFile));
        var option = parsed.RootElement.GetProperty("Options").EnumerateArray().Single();
        Assert.Equal("Glow", option.GetProperty("Name").GetString());
        Assert.True(option.GetProperty("Files").TryGetProperty("chara/x/new.tex", out _));

        // default_mod.json was not touched by the option-targeted write.
        var defaultMod = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "default_mod.json")));
        Assert.False(defaultMod.RootElement.GetProperty("Files").TryGetProperty("chara/x/new.tex", out _));

        link.RevertAll();
        Assert.False(File.Exists(groupFile));
    }

    [Fact]
    public void RedirectionsOutsideTheModFolderAreRefused()
    {
        var dir = Path.Combine(_root, "evil");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "meta.json"), JsonSerializer.Serialize(new
        {
            FileVersion = 4,
            Name = "Evil",
            DefaultData = new
            {
                Files = new Dictionary<string, string> { ["chara/x/a.tex"] = "..\\outside.tex" },
            },
        }));

        var link = Create();
        link.Link(dir, []);
        Assert.Throws<PenumbraLinkException>(() => link.WriteAsset("chara/x/a.tex", [1, 2, 3]));
    }
}
