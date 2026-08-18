using StbImageSharp;
using StbImageWriteSharp;

namespace Moonlace.GameData.Interchange;

/// <summary>PNG (and common image) encode/decode for interchange. Preview/IO only — never touches game data.</summary>
public static class ImageIo
{
    /// <summary>Encodes RGBA8 pixels as PNG.</summary>
    public static byte[] EncodePng(int width, int height, byte[] rgba)
    {
        using var ms = new MemoryStream();
        new ImageWriter().WritePng(rgba, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, ms);
        return ms.ToArray();
    }

    /// <summary>Decodes a PNG/JPG/TGA/BMP file to RGBA8. Throws with a readable message on failure.</summary>
    public static (int Width, int Height, byte[] Rgba) DecodeImageFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var image = ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            return (image.Width, image.Height, image.Data);
        }
        catch (Exception ex) when (ex is not IOException)
        {
            throw new InvalidDataException(
                $"\"{Path.GetFileName(path)}\" could not be read as an image. Supported formats: PNG, JPG, TGA, BMP.", ex);
        }
    }
}
