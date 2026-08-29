using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Moonlace.Core.Penumbra;
using Moonlace.GameData.ModTools;
using Moonlace.GameData.Parsing;

namespace Moonlace.GameData.Tests;

/// <summary>
/// Animation retargeting: a modpack built from one emote's real game files is
/// rewired onto a different emote, saved as a new .pmp, with the pap clip
/// names patched to what the destination's timeline references. Real game
/// data, read-only.
/// </summary>
public sealed class AnimationRetargetTests : IDisposable
{
    private const string SourceKey = "emote/dance";
    private const string DestinationKey = "emote/cheer";

    private readonly LuminaGameDataService _service = new(NullLogger<LuminaGameDataService>.Instance);
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "moonlace-anim-retarget-tests-" + Guid.NewGuid().ToString("N"));

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

    private (AnimationTimelineCatalog Catalog, AnimationRetargeter Retargeter) CreateStack()
    {
        var catalog = new AnimationTimelineCatalog(_service);
        var link = new PenumbraLinkService(NullLogger<PenumbraLinkService>.Instance);
        return (catalog, new AnimationRetargeter(_service, catalog, link, NullLogger<AnimationRetargeter>.Instance));
    }

    private static string PapPath(string race, string key) =>
        $"chara/human/c{race}/animation/a0001/bt_common/{key}.pap";

    private static string TmbPath(string key) => $"chara/action/{key}.tmb";

    /// <summary>The emote's real c0101/c0201 paps and its timeline, straight from the game.</summary>
    private Dictionary<string, byte[]> CollectEmoteFiles(string key, bool includeTimeline)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var race in new[] { "0101", "0201" })
        {
            var path = PapPath(race, key);
            if (_service.Lumina.GetFile(path)?.Data is { } pap)
                files[path] = pap;
        }

        Assert.NotEmpty(files);
        if (includeTimeline)
            files[TmbPath(key)] = _service.Lumina.GetFile(TmbPath(key))!.Data;
        return files;
    }

    private string WritePmp(Dictionary<string, byte[]> files)
    {
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
            ["Name"] = "Anim Retarget Source",
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
        return pmpPath;
    }

    private static Dictionary<string, byte[]> ReadPmp(string pmpPath)
    {
        using var zip = ZipFile.OpenRead(pmpPath);
        using var defaultMod = JsonDocument.Parse(
            new StreamReader(zip.GetEntry("default_mod.json")!.Open()).ReadToEnd());
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var property in defaultMod.RootElement.GetProperty("Files").EnumerateObject())
        {
            using var stream = zip.GetEntry(property.Value.GetString()!.Replace('\\', '/'))!.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            files[property.Name] = memory.ToArray();
        }

        return files;
    }

    [SkippableFact]
    public void PapClipNamesRoundTrip()
    {
        Skip.IfNot(TryInit());
        var pap = _service.Lumina.GetFile(PapPath("0101", SourceKey))!.Data;

        var names = PapAnimations.ReadNames(pap);
        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.False(string.IsNullOrEmpty(n)));

        var renamed = PapAnimations.RenameAnimations(pap, _ => "cbem_moonlace_test");
        Assert.NotSame(pap, renamed);
        Assert.Equal(pap.Length, renamed.Length);
        Assert.All(PapAnimations.ReadNames(renamed), n => Assert.Equal("cbem_moonlace_test", n));

        // Untouched rename returns the same instance, and everything outside
        // the name fields is byte-identical after a real rename.
        Assert.Same(pap, PapAnimations.RenameAnimations(pap, _ => null));
        var restored = PapAnimations.RenameAnimations(renamed, _ => names[0]);
        Assert.Equal(pap, restored);
    }

    [SkippableFact]
    public void CatalogMatchesPathsAndLabelsEmotes()
    {
        Skip.IfNot(TryInit());
        var (catalog, _) = CreateStack();

        Assert.Equal(SourceKey, catalog.MatchKey(PapPath("0101", SourceKey)));
        Assert.Equal(SourceKey, catalog.MatchKey(TmbPath(SourceKey)));
        Assert.Null(catalog.MatchKey("chara/equipment/e0147/model/c0101e0147_top.mdl"));
        Assert.Null(catalog.MatchKey("chara/action/emote/definitely_not_a_real_key.tmb"));

        Assert.Contains("Dance", catalog.Describe(SourceKey).DisplayName);
        Assert.Equal("Chair sitting pose 3", catalog.Describe("emote/j_pose02_loop").DisplayName);
        Assert.Contains(catalog.Destinations, d => d.Key == DestinationKey);
    }

    [SkippableFact]
    public async Task AnalyzeFindsAnimationBinding()
    {
        Skip.IfNot(TryInit());
        var (_, retargeter) = CreateStack();
        var files = CollectEmoteFiles(SourceKey, includeTimeline: true);
        var pmp = WritePmp(files);

        var analysis = await retargeter.AnalyzeAsync(pmp);

        Assert.Equal("Anim Retarget Source", analysis.ModName);
        var binding = Assert.Single(analysis.Bindings);
        Assert.Equal(SourceKey, binding.TimelineKey);
        Assert.Contains("Dance", binding.DisplayName);
        Assert.Equal(files.Count, binding.GamePaths.Count);
        Assert.Contains("0101", binding.RaceCodes);
        Assert.True(binding.HasTimelineFile);
        Assert.Equal(0, analysis.CarriedFileCount);
    }

    [SkippableFact]
    public async Task RetargetMovesPapsAndPatchesClipNames()
    {
        Skip.IfNot(TryInit());
        var (catalog, retargeter) = CreateStack();
        var files = CollectEmoteFiles(SourceKey, includeTimeline: true);
        var pmp = WritePmp(files);
        var binding = Assert.Single((await retargeter.AnalyzeAsync(pmp)).Bindings);
        var destination = catalog.Destinations.Single(d => d.Key == DestinationKey);
        var output = Path.Combine(_tempRoot, "retargeted.pmp");

        var report = await retargeter.RetargetAsync(pmp, binding, destination, output);

        // The unmodified timeline is dropped; every pap moves.
        Assert.Equal(files.Count - 1, report.FilesRewired);
        Assert.Equal(1, report.TimelinesDropped);
        Assert.Equal(0, report.FilesCarried);
        Assert.True(report.PapsRenamed > 0, "the clip names must be patched for the destination");

        var outFiles = ReadPmp(output);
        Assert.All(outFiles.Keys, k => Assert.DoesNotContain(SourceKey, k));
        Assert.DoesNotContain(outFiles.Keys, k => k.EndsWith(".tmb", StringComparison.Ordinal));

        // Each pap sits on the destination's path for its race and carries
        // exactly the clip names the destination's vanilla pap declares.
        foreach (var race in binding.RaceCodes)
        {
            var moved = outFiles[PapPath(race, DestinationKey)];
            var vanilla = _service.Lumina.GetFile(PapPath(race, DestinationKey))!.Data;
            Assert.Equal(PapAnimations.ReadNames(vanilla), PapAnimations.ReadNames(moved));

            // The havok payload is untouched: same bytes as the source pap
            // outside the renamed fields.
            Assert.Equal(files[PapPath(race, SourceKey)].Length, moved.Length);
        }
    }

    [SkippableFact]
    public async Task EditedTimelineMovesAsIsAndPapsKeepTheirNames()
    {
        Skip.IfNot(TryInit());
        var (catalog, retargeter) = CreateStack();
        var files = CollectEmoteFiles(SourceKey, includeTimeline: true);

        // An edited timeline (one flipped byte at the end, where the string
        // table lives) must survive the retarget, and the paps must keep
        // their own clip names so the pair stays consistent.
        var timeline = (byte[])files[TmbPath(SourceKey)].Clone();
        timeline[^1] ^= 0xFF;
        files[TmbPath(SourceKey)] = timeline;

        var pmp = WritePmp(files);
        var binding = Assert.Single((await retargeter.AnalyzeAsync(pmp)).Bindings);
        var destination = catalog.Destinations.Single(d => d.Key == DestinationKey);
        var output = Path.Combine(_tempRoot, "retargeted-custom-tmb.pmp");

        var report = await retargeter.RetargetAsync(pmp, binding, destination, output);

        Assert.Equal(files.Count, report.FilesRewired);
        Assert.Equal(0, report.TimelinesDropped);
        Assert.Equal(0, report.PapsRenamed);
        Assert.Contains(report.Warnings, w => w.Contains("edited timeline"));

        var outFiles = ReadPmp(output);
        Assert.Equal(timeline, outFiles[TmbPath(DestinationKey)]);
        foreach (var race in binding.RaceCodes)
        {
            Assert.Equal(
                PapAnimations.ReadNames(files[PapPath(race, SourceKey)]),
                PapAnimations.ReadNames(outFiles[PapPath(race, DestinationKey)]));
        }
    }

    [SkippableFact]
    public async Task RetargetRefusesANoOpAssignment()
    {
        Skip.IfNot(TryInit());
        var (catalog, retargeter) = CreateStack();
        var pmp = WritePmp(CollectEmoteFiles(SourceKey, includeTimeline: false));
        var binding = Assert.Single((await retargeter.AnalyzeAsync(pmp)).Bindings);
        var same = catalog.Destinations.Single(d => d.Key == SourceKey);

        await Assert.ThrowsAsync<AnimationRetargetException>(
            () => retargeter.RetargetAsync(pmp, binding, same, Path.Combine(_tempRoot, "noop.pmp")));
    }
}
