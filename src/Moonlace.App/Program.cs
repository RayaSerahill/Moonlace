using Avalonia;
using System;

namespace Moonlace.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack must run before anything else: on install/update/uninstall
        // hooks it exits the process, and after an update it restarts us here.
        Velopack.VelopackApp.Build().Run();

        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(BuildX11Options())
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    // GLX context sharing is unreliable on NVIDIA under XWayland (the
    // compositor spams glXMakeContextCurrent/eglMakeCurrent failures and the
    // viewport loses its context), so the default order prefers backends that
    // behave there, with llvmpipe allowed so the viewport still works without
    // hardware GL (VMs, Xvfb). MOONLACE_RENDERER=vulkan|egl|glx|software
    // overrides the order for troubleshooting.
    private static X11PlatformOptions BuildX11Options()
    {
        var order = Environment.GetEnvironmentVariable("MOONLACE_RENDERER")?.ToLowerInvariant() switch
        {
            "vulkan" => new[] { X11RenderingMode.Vulkan, X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software },
            "egl" => [X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software],
            "glx" => [X11RenderingMode.Glx, X11RenderingMode.Software],
            "software" => [X11RenderingMode.Software],
            _ => [X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software],
        };

        return new X11PlatformOptions
        {
            RenderingMode = order,
            GlxRendererBlacklist = [],
            // On by default: serializing render + UI avoids the NVIDIA
            // multi-threaded context races behind intermittent
            // glXMakeContextCurrent/eglMakeCurrent render-loop failures, and
            // costs nothing for Moonlace's event-driven scene.
            // MOONLACE_RENDER_UI_THREAD=0 restores the threaded render loop.
            ShouldRenderOnUIThread = Environment.GetEnvironmentVariable("MOONLACE_RENDER_UI_THREAD") != "0",
        };
    }
}
