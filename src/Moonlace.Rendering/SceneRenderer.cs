using System.Numerics;
using Moonlace.Core.Models;
using Moonlace.Rendering.Camera;
using Moonlace.Rendering.OpenGL;
using Moonlace.Rendering.Shaders;
using Silk.NET.OpenGL;

namespace Moonlace.Rendering;

/// <summary>
/// Renders one <see cref="RenderModel"/> with an orbit camera. All methods
/// must be called on the thread that owns the GL context (Avalonia calls the
/// control's init/render/deinit on the UI thread with the context current).
///
/// Model data arrives on any thread via <see cref="SetModel"/>; the GPU upload
/// happens lazily at the start of the next <see cref="Render"/> call, keeping
/// all GL work on the context thread.
/// </summary>
public sealed class SceneRenderer : IDisposable
{
    private const int MaxColorRows = 32;

    private GL? _gl;
    private ShaderProgram? _shader;
    private GpuModel? _current;
    private RenderModel? _pending;
    private bool _pendingChanged;
    private readonly Lock _pendingLock = new();

    public OrbitCamera Camera { get; } = new();

    public bool IsInitialized => _gl is not null;

    /// <summary>Thread-safe: schedules a model (or null to clear) for display on the next frame.</summary>
    public void SetModel(RenderModel? model)
    {
        lock (_pendingLock)
        {
            _pending = model;
            _pendingChanged = true;
        }
    }

    public void Initialize(GL gl, bool isGles)
    {
        _gl = gl;
        var header = isGles ? "#version 300 es\n" : "#version 330 core\n";
        _shader = new ShaderProgram(gl, header + ShaderSources.Vertex, header + ShaderSources.Fragment);
    }

    public void Render(int framebufferWidth, int framebufferHeight)
    {
        if (_gl is null || _shader is null)
            return;
        var gl = _gl;

        UploadPendingIfAny(gl);

        gl.Viewport(0, 0, (uint)framebufferWidth, (uint)framebufferHeight);
        gl.ClearColor(0f, 0f, 0f, 0f);
        gl.ClearDepth(1f);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (_current is null || _current.Meshes.Count == 0)
            return;

        // FFXIV gear meshes are frequently single-sided (skirt/robe interiors);
        // culling would punch visible holes in them.
        gl.Disable(EnableCap.CullFace);
        gl.Disable(EnableCap.Blend);

        var aspect = framebufferHeight == 0 ? 1f : (float)framebufferWidth / framebufferHeight;

        _shader.Use();
        _shader.SetMatrix("uModel", Matrix4x4.Identity);
        _shader.SetMatrix("uView", Camera.ViewMatrix());
        _shader.SetMatrix("uProjection", Camera.ProjectionMatrix(aspect));
        _shader.SetVector3("uCameraPos", Camera.Position);
        _shader.SetVector3("uLightDir", Vector3.Normalize(new Vector3(-0.45f, -0.8f, -0.4f)));
        _shader.SetInt("uDiffuseTex", 0);
        _shader.SetInt("uNormalTex", 1);
        _shader.SetInt("uMaskTex", 2);
        _shader.SetInt("uIndexTex", 3);
        _shader.SetInt("uSpecularTex", 4);

        foreach (var mesh in _current.Meshes)
        {
            ApplyMaterial(mesh.Material);
            mesh.Gpu.Draw();
        }
    }

    private void ApplyMaterial(GpuMaterial material)
    {
        var s = _shader!;
        s.SetInt("uHasDiffuse", material.Diffuse is not null ? 1 : 0);
        s.SetInt("uHasNormal", material.Normal is not null ? 1 : 0);
        s.SetInt("uHasMask", material.Mask is not null ? 1 : 0);
        s.SetInt("uHasIndex", material.Index is not null ? 1 : 0);
        s.SetInt("uHasSpecular", material.Specular is not null ? 1 : 0);
        s.SetInt("uColorTableRows", material.ColorTableRows);
        s.SetVector3("uBaseTint", material.BaseTint);
        s.SetInt("uAlphaCutout", material.AlphaCutout ? 1 : 0);
        s.SetInt("uUseVertexColor", material.UseVertexColor ? 1 : 0);

        material.Diffuse?.Bind(0);
        material.Normal?.Bind(1);
        material.Mask?.Bind(2);
        material.Index?.Bind(3);
        material.Specular?.Bind(4);

        if (material.ColorTableRows > 0)
        {
            s.SetVector3Array("uCtDiffuse", material.CtDiffuse);
            s.SetVector3Array("uCtSpecular", material.CtSpecular);
            s.SetVector3Array("uCtEmissive", material.CtEmissive);
            s.SetFloatArray("uCtGloss", material.CtGloss);
            s.SetFloatArray("uCtSpecStrength", material.CtSpecStrength);
        }
    }

    private void UploadPendingIfAny(GL gl)
    {
        RenderModel? pending;
        lock (_pendingLock)
        {
            if (!_pendingChanged)
                return;
            pending = _pending;
            _pendingChanged = false;
        }

        _current?.Dispose();
        _current = null;

        if (pending is null)
            return;

        _current = GpuModel.Upload(gl, pending);
        Camera.FrameBounds(pending.BoundsMin, pending.BoundsMax);
    }

    public void Dispose()
    {
        _current?.Dispose();
        _current = null;
        _shader?.Dispose();
        _shader = null;
        _gl = null;
    }

    /// <summary>
    /// Drops all GPU object references WITHOUT making GL calls. For when the
    /// GL context was lost (driver reset, GPU hiccup): the handles are dead
    /// with it, and deleting them through a dead context can crash. The next
    /// <see cref="Initialize"/> + <see cref="Render"/> rebuilds everything
    /// from the pending model.
    /// </summary>
    public void AbandonGlResources(RenderModel? modelToRestore)
    {
        _current = null;
        _shader = null;
        _gl = null;
        lock (_pendingLock)
        {
            _pending = modelToRestore;
            _pendingChanged = true;
        }
    }

    /// <summary>A model resident on the GPU: meshes plus their material textures.</summary>
    private sealed class GpuModel : IDisposable
    {
        public required List<(GpuMesh Gpu, GpuMaterial Material)> Meshes { get; init; }

        public required List<GpuTexture> Textures { get; init; }

        public static GpuModel Upload(GL gl, RenderModel model)
        {
            var textures = new List<GpuTexture>();
            var textureCache = new Dictionary<string, GpuTexture>(StringComparer.Ordinal);
            var materialCache = new Dictionary<RenderMaterial, GpuMaterial>();
            var meshes = new List<(GpuMesh, GpuMaterial)>();

            GpuTexture? Upload(RenderTexture? tex)
            {
                if (tex is null)
                    return null;
                if (!textureCache.TryGetValue(tex.Key, out var gpu))
                {
                    gpu = new GpuTexture(gl, tex);
                    textureCache[tex.Key] = gpu;
                    textures.Add(gpu);
                }

                return gpu;
            }

            foreach (var mesh in model.Meshes)
            {
                if (!materialCache.TryGetValue(mesh.Material, out var material))
                {
                    material = GpuMaterial.From(mesh.Material, Upload);
                    materialCache[mesh.Material] = material;
                }

                meshes.Add((new GpuMesh(gl, mesh), material));
            }

            return new GpuModel { Meshes = meshes, Textures = textures };
        }

        public void Dispose()
        {
            foreach (var (mesh, _) in Meshes)
                mesh.Dispose();
            foreach (var texture in Textures)
                texture.Dispose();
            Meshes.Clear();
            Textures.Clear();
        }
    }

    private sealed class GpuMaterial
    {
        public GpuTexture? Diffuse;
        public GpuTexture? Normal;
        public GpuTexture? Mask;
        public GpuTexture? Index;
        public GpuTexture? Specular;
        public Vector3 BaseTint = Vector3.One;
        public bool AlphaCutout = true;
        public bool UseVertexColor = true;
        public int ColorTableRows;
        public Vector3[] CtDiffuse = [];
        public Vector3[] CtSpecular = [];
        public Vector3[] CtEmissive = [];
        public float[] CtGloss = [];
        public float[] CtSpecStrength = [];

        public static GpuMaterial From(RenderMaterial source, Func<RenderTexture?, GpuTexture?> upload)
        {
            var rows = Math.Min(source.ColorTable.Count, MaxColorRows);
            var material = new GpuMaterial
            {
                Diffuse = upload(source.Diffuse),
                Normal = upload(source.Normal),
                Mask = upload(source.Mask),
                Index = upload(source.Index),
                Specular = upload(source.Specular),
                // Skin/hair colors come from character customization data v1
                // doesn't read; a neutral tint keeps body parts plausible.
                BaseTint = source.ShaderPack switch
                {
                    "skin.shpk" => new Vector3(0.96f, 0.80f, 0.70f),
                    "hair.shpk" => new Vector3(0.42f, 0.34f, 0.28f),
                    _ => Vector3.One,
                },
                AlphaCutout = source.ShaderPack != "skin.shpk",
                UseVertexColor = source.ShaderPack != "skin.shpk",
                ColorTableRows = rows,
                CtDiffuse = new Vector3[MaxColorRows],
                CtSpecular = new Vector3[MaxColorRows],
                CtEmissive = new Vector3[MaxColorRows],
                CtGloss = new float[MaxColorRows],
                CtSpecStrength = new float[MaxColorRows],
            };

            for (var i = 0; i < rows; i++)
            {
                var row = source.ColorTable[i];
                material.CtDiffuse[i] = row.DiffuseColor;
                material.CtSpecular[i] = row.SpecularColor;
                material.CtEmissive[i] = row.EmissiveColor;
                material.CtGloss[i] = row.GlossStrength;
                material.CtSpecStrength[i] = row.SpecularStrength;
            }

            return material;
        }
    }
}
