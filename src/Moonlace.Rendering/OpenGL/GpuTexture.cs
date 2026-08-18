using Moonlace.Core.Models;
using Silk.NET.OpenGL;

namespace Moonlace.Rendering.OpenGL;

/// <summary>An uploaded 2D RGBA8 texture with mipmaps. Explicitly disposed.</summary>
public sealed class GpuTexture : IDisposable
{
    private readonly GL _gl;

    public uint Handle { get; }

    public unsafe GpuTexture(GL gl, RenderTexture source)
    {
        _gl = gl;
        Handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, Handle);
        fixed (byte* data = source.Rgba)
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                (uint)source.Width, (uint)source.Height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, data);
        }

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.GenerateMipmap(TextureTarget.Texture2D);
        gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Bind(int unit)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + unit);
        _gl.BindTexture(TextureTarget.Texture2D, Handle);
    }

    public void Dispose() => _gl.DeleteTexture(Handle);
}
