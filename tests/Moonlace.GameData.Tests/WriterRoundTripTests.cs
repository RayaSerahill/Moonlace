using Lumina.Data.Files;
using Microsoft.Extensions.Logging.Abstractions;
using Moonlace.GameData.Parsing;

namespace Moonlace.GameData.Tests;

/// <summary>
/// Round-trip verification for the Moonlace writers. Game data is only ever
/// read; all written bytes live in memory or temp files.
/// </summary>
public sealed class WriterRoundTripTests : IDisposable
{
    private readonly LuminaGameDataService _service = new(NullLogger<LuminaGameDataService>.Instance);

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

    public void Dispose() => _service.Dispose();

    private const string WeaponMdl = "chara/weapon/w0201/obj/body/b0001/model/w0201b0001.mdl";
    private const string BodyMdl = "chara/equipment/e0001/model/c0101e0001_top.mdl";
    private const string WeaponMtrl = "chara/weapon/w0201/obj/body/b0001/material/v0005/mt_w0201b0001_a.mtrl";

    [SkippableTheory]
    [InlineData(WeaponMdl)]
    [InlineData(BodyMdl)]
    public void MdlWriteReadRoundTripPreservesGeometry(string path)
    {
        Skip.IfNot(TryInit());
        var original = MdlParser.Parse(_service.Lumina.GetFile(path)!.Data);
        Assert.NotNull(original.EditData);

        var written = MdlWriter.Write(original, original.Meshes, original.BoneTables);
        var reparsed = MdlParser.Parse(written);

        Assert.Equal(original.Meshes.Count, reparsed.Meshes.Count);
        Assert.Equal(original.MaterialNames, reparsed.MaterialNames);
        Assert.Equal(original.BoneNames, reparsed.BoneNames);
        Assert.Equal(original.BoneTables.Count, reparsed.BoneTables.Count);
        for (var t = 0; t < original.BoneTables.Count; t++)
            Assert.Equal(original.BoneTables[t], reparsed.BoneTables[t]);

        for (var m = 0; m < original.Meshes.Count; m++)
        {
            var a = original.Meshes[m];
            var b = reparsed.Meshes[m];
            Assert.Equal(a.Indices, b.Indices);
            Assert.Equal(a.MaterialIndex, b.MaterialIndex);
            Assert.Equal(a.BoneTableIndex, b.BoneTableIndex);
            Assert.Equal(a.Vertices.Length, b.Vertices.Length);
            for (var v = 0; v < a.Vertices.Length; v += 37) // sample
            {
                var av = a.Vertices[v];
                var bv = b.Vertices[v];
                Assert.True((av.Position - bv.Position).Length() < 1e-4f, $"mesh {m} vertex {v} position drift");
                Assert.True((av.Normal - bv.Normal).Length() < 1e-4f, $"mesh {m} vertex {v} normal drift");
                Assert.True((av.Uv - bv.Uv).Length() < 1e-3f, $"mesh {m} vertex {v} uv drift");
                Assert.Equal(av.BlendIndicesPacked, bv.BlendIndicesPacked);
                Assert.True(Math.Abs(av.BlendWeights.X - bv.BlendWeights.X) < 0.02f, $"mesh {m} vertex {v} weight drift");
            }
        }
    }

    [SkippableFact]
    public void WrittenMdlIsReadableByLumina()
    {
        Skip.IfNot(TryInit());
        var original = MdlParser.Parse(_service.Lumina.GetFile(WeaponMdl)!.Data);
        var written = MdlWriter.Write(original, original.Meshes, original.BoneTables);

        // Lumina's MdlFile reads v5 — an independent implementation check.
        var tmp = Path.Combine(Path.GetTempPath(), $"moonlace-test-{Guid.NewGuid():N}.mdl");
        try
        {
            File.WriteAllBytes(tmp, written);
            var lumina = _service.Lumina.GetFileFromDisk<MdlFile>(tmp, WeaponMdl);

            Assert.Equal(MdlParser.VersionV5, lumina.FileHeader.Version);
            Assert.Equal(original.Meshes.Count, lumina.Meshes.Length);
            Assert.Equal(3, lumina.FileHeader.LodCount);
            Assert.Equal(original.BoneNames.Count, lumina.BoneNameOffsets.Length);

            var model = new Lumina.Models.Models.Model(lumina);
            Assert.Equal(original.Meshes.Count, model.Meshes.Length);
            var v0 = model.Meshes[0].Vertices[0];
            var expected = original.Meshes[0].Vertices[0].Position;
            Assert.True(Math.Abs(v0.Position!.Value.X - expected.X) < 1e-4f, "Lumina-read position mismatch");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [SkippableFact]
    public void MtrlColorTablePatchRoundTrips()
    {
        Skip.IfNot(TryInit());
        var original = _service.Lumina.GetFile(WeaponMtrl)!.Data;
        var parsed = MtrlParser.Parse(original);
        Assert.True(parsed.ColorTable.Count > 0);

        var rows = parsed.ColorTable.ToArray();
        rows[2].Diffuse = new System.Numerics.Vector3(0.75f, 0.125f, 0.25f);
        rows[2].Gloss = 12f;
        rows[2].SpecularStrength = 0.5f;

        var patched = MtrlWriter.PatchColorTable(original, rows);
        Assert.Equal(original.Length, patched.Length);

        var reparsed = MtrlParser.Parse(patched);
        Assert.Equal(parsed.ShaderPack, reparsed.ShaderPack);
        Assert.Equal(parsed.TexturePaths, reparsed.TexturePaths);
        Assert.True(Math.Abs(reparsed.ColorTable[2].Diffuse.X - 0.75f) < 0.01f);
        Assert.True(Math.Abs(reparsed.ColorTable[2].Gloss - 12f) < 0.1f);
        Assert.True(Math.Abs(reparsed.ColorTable[2].SpecularStrength - 0.5f) < 0.01f);
        // Untouched rows survive byte-exact.
        Assert.Equal(parsed.ColorTable[0].Diffuse, reparsed.ColorTable[0].Diffuse);
        Assert.Equal(parsed.ColorTable[5].Specular, reparsed.ColorTable[5].Specular);
    }

    [SkippableFact]
    public void TexWriteIsReadableByMoonlaceAndLumina()
    {
        Skip.IfNot(TryInit());
        const int w = 8;
        const int h = 4;
        var rgba = new byte[w * h * 4];
        for (var i = 0; i < rgba.Length; i++)
            rgba[i] = (byte)(i * 7);

        var tex = TexWriter.Write(w, h, rgba);

        var back = TexWriter.TryReadB8G8R8A8(tex);
        Assert.NotNull(back);
        Assert.Equal((w, h), (back.Value.Width, back.Value.Height));
        Assert.Equal(rgba, back.Value.Rgba);

        var tmp = Path.Combine(Path.GetTempPath(), $"moonlace-test-{Guid.NewGuid():N}.tex");
        try
        {
            File.WriteAllBytes(tmp, tex);
            var lumina = _service.Lumina.GetFileFromDisk<TexFile>(tmp, "test.tex");
            Assert.Equal(w, lumina.Header.Width);
            Assert.Equal(h, lumina.Header.Height);
            var converted = lumina.TextureBuffer.Filter(0, 0, TexFile.TextureFormat.B8G8R8A8);
            Assert.Equal(w, converted.Width);
            // First pixel round-trips through Lumina: BGRA stored, compare against RGBA source.
            Assert.Equal(rgba[0], converted.RawData[2]);
            Assert.Equal(rgba[1], converted.RawData[1]);
            Assert.Equal(rgba[2], converted.RawData[0]);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
