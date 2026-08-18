using System.Collections.Concurrent;
using Lumina.Data.Files;
using Microsoft.Extensions.Logging;
using Moonlace.Core.Models;
using Moonlace.GameData.Parsing;

namespace Moonlace.GameData;

/// <summary>
/// Decodes FFXIV .tex files into RGBA8 pixel data, with a session-lifetime
/// cache. Session-aware: when the active session holds a modified copy of a
/// texture, that copy is decoded instead, and the cache key carries the
/// session revision so edits invalidate exactly the affected texture.
/// </summary>
public sealed class TextureDecoder
{
    private readonly LuminaGameDataService _gameData;
    private readonly EffectiveAssetProvider _assets;
    private readonly ILogger<TextureDecoder> _logger;
    private readonly ConcurrentDictionary<string, RenderTexture?> _cache = new(StringComparer.Ordinal);

    public TextureDecoder(LuminaGameDataService gameData, EffectiveAssetProvider assets, ILogger<TextureDecoder> logger)
    {
        _gameData = gameData;
        _assets = assets;
        _logger = logger;
    }

    /// <summary>Loads and decodes the effective texture, or returns null when missing/undecodable.</summary>
    public RenderTexture? Decode(string texPath)
    {
        var revision = _assets.Revision(texPath);
        var cacheKey = revision == 0 ? texPath : $"{texPath}#{revision}";
        return _cache.GetOrAdd(cacheKey, _ => DecodeUncached(texPath, revision));
    }

    private RenderTexture? DecodeUncached(string texPath, int revision)
    {
        try
        {
            return revision == 0 ? DecodeOriginal(texPath) : DecodeSessionCopy(texPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode texture {Path} (revision {Rev})", texPath, revision);
            return null;
        }
    }

    private RenderTexture? DecodeOriginal(string texPath)
    {
        var tex = _gameData.Lumina.GetFile<TexFile>(texPath);
        if (tex is null)
        {
            _logger.LogWarning("Texture not found: {Path}", texPath);
            return null;
        }

        var converted = tex.TextureBuffer.Filter(mip: 0, z: 0, format: TexFile.TextureFormat.B8G8R8A8);
        return FromBgra(texPath, converted.Width, converted.Height, converted.RawData);
    }

    private RenderTexture? DecodeSessionCopy(string texPath)
    {
        var bytes = _assets.TryReadFile(texPath);
        if (bytes is null)
            return null;

        // Moonlace writes session textures as uncompressed B8G8R8A8.
        var direct = TexWriter.TryReadB8G8R8A8(bytes);
        if (direct is { } d)
        {
            return new RenderTexture { Key = texPath, Width = d.Width, Height = d.Height, Rgba = d.Rgba };
        }

        // Fallback for any other format: let Lumina parse it from a temp file.
        var tmp = Path.Combine(Path.GetTempPath(), $"moonlace-tex-{Guid.NewGuid():N}.tex");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            var tex = _gameData.Lumina.GetFileFromDisk<TexFile>(tmp, texPath);
            var converted = tex.TextureBuffer.Filter(mip: 0, z: 0, format: TexFile.TextureFormat.B8G8R8A8);
            return FromBgra(texPath, converted.Width, converted.Height, converted.RawData);
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch (IOException)
            {
            }
        }
    }

    private static RenderTexture FromBgra(string key, int width, int height, byte[] bgra)
    {
        var rgba = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = bgra[i + 3];
        }

        return new RenderTexture { Key = key, Width = width, Height = height, Rgba = rgba };
    }
}
