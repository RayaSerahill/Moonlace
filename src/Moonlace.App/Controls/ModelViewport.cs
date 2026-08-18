using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Moonlace.Core.Models;
using Moonlace.Rendering;
using Silk.NET.OpenGL;

namespace Moonlace.App.Controls;

/// <summary>
/// Embedded OpenGL viewport. Owns a <see cref="SceneRenderer"/>; Avalonia
/// guarantees init/render/deinit run with the GL context current, so all GPU
/// resource lifetime stays inside those callbacks. Model data is handed over
/// thread-safely via <see cref="Model"/> and uploaded on the next frame.
///
/// Camera controls: left-drag orbit, wheel zoom, right/middle-drag pan.
/// </summary>
public sealed class ModelViewport : OpenGlControlBase
{
    public static readonly StyledProperty<RenderModel?> ModelProperty =
        AvaloniaProperty.Register<ModelViewport, RenderModel?>(nameof(Model));

    private readonly SceneRenderer _renderer = new();
    private GL? _gl;
    private Point _lastPointer;
    private bool _orbiting;
    private bool _panning;

    public RenderModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>Set when GL init or rendering failed; the view shows it instead of a black box.</summary>
    public static readonly StyledProperty<string?> RenderErrorProperty =
        AvaloniaProperty.Register<ModelViewport, string?>(nameof(RenderError));

    public string? RenderError
    {
        get => GetValue(RenderErrorProperty);
        set => SetValue(RenderErrorProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty)
        {
            _renderer.SetModel(change.GetNewValue<RenderModel?>());
            RequestNextFrameRendering();
        }
    }

    public override void Render(global::Avalonia.Media.DrawingContext context)
    {
        // The GL surface is presented through a composition visual that the
        // hit-tester does not see, leaving the control transparent to the
        // pointer — camera input never arrives. A transparent fill is
        // hit-testable geometry, so this one rect makes the whole viewport
        // clickable.
        context.FillRectangle(global::Avalonia.Media.Brushes.Transparent, new Rect(Bounds.Size));
        base.Render(context);
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _gl = GL.GetApi(gl.GetProcAddress);
            var isGles = GlVersion.Type == GlProfileType.OpenGLES;
            System.Diagnostics.Trace.WriteLine($"ModelViewport: GL init, version {GlVersion.Major}.{GlVersion.Minor} {GlVersion.Type}");
            _renderer.Initialize(_gl, isGles);
            _renderer.SetModel(Model);
            RenderError = null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"ModelViewport: GL init FAILED: {ex}");
            RenderError = $"3D viewport could not be initialized.\n{ex.Message}";
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _renderer.Dispose();
        _gl?.Dispose();
        _gl = null;
    }

    protected override void OnOpenGlLost()
    {
        // The context died (driver reset/GPU hiccup) — its GPU handles died
        // with it, so drop them without GL calls and queue the current model
        // for re-upload when Avalonia hands us a fresh context.
        System.Diagnostics.Trace.WriteLine("ModelViewport: GL context lost; resources abandoned for rebuild");
        _renderer.AbandonGlResources(Model);
        _gl = null;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_gl is null || RenderError is not null)
            return;

        try
        {
            var scaling = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
            var width = Math.Max(1, (int)(Bounds.Width * scaling));
            var height = Math.Max(1, (int)(Bounds.Height * scaling));
            _renderer.Render(width, height);
        }
        catch (Exception ex)
        {
            RenderError = $"Rendering failed.\n{ex.Message}";
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        _lastPointer = point.Position;
        _orbiting = point.Properties.IsLeftButtonPressed;
        _panning = point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed;
        if (_orbiting || _panning)
        {
            e.Pointer.Capture(this);
            Cursor = new Cursor(_orbiting ? StandardCursorType.Hand : StandardCursorType.SizeAll);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_orbiting && !_panning)
            return;

        var position = e.GetPosition(this);
        var delta = position - _lastPointer;
        _lastPointer = position;

        if (_orbiting)
            _renderer.Camera.Orbit((float)delta.X * -0.008f, (float)delta.Y * 0.008f);
        else
            _renderer.Camera.Pan((float)delta.X, (float)delta.Y);

        RequestNextFrameRendering();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _orbiting = false;
        _panning = false;
        e.Pointer.Capture(null);
        Cursor = Cursor.Default;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _renderer.Camera.Zoom((float)e.Delta.Y);
        RequestNextFrameRendering();
    }
}
