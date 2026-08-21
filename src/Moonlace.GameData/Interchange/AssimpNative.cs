using System.Runtime.InteropServices;
using Silk.NET.Assimp;
using Silk.NET.Core.Contexts;

namespace Moonlace.GameData.Interchange;

/// <summary>
/// Loads the assimp native library and exposes a shared <see cref="Assimp"/>
/// API instance for FBX interchange.
///
/// Silk.NET's default loader probes "runtimes/{RID}/native" with the
/// machine's own runtime identifier, which on rolling distros is a distro RID
/// (e.g. "arch-x64") that never matches the portable RID folders NuGet
/// restores. The loader here builds the portable RID itself and prefers the
/// assimp 5 binary, which is the version the Silk.NET 2.23 bindings were
/// generated against.
/// </summary>
internal static class AssimpNative
{
    private static readonly Lazy<Assimp> LazyApi = new(
        () => new Assimp(new StaticContext(LoadNativeLibrary())),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static Assimp Api => LazyApi.Value;

    private static nint LoadNativeLibrary()
    {
        var names = OperatingSystem.IsWindows()
            ? new[] { "Assimp64.dll", "assimp.dll" }
            : OperatingSystem.IsMacOS()
                ? new[] { "libassimp.5.dylib", "libassimp.6.dylib", "libassimp.dylib" }
                : new[] { "libassimp.so.5", "libassimp.so.6", "libassimp.so" };

        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var rid = $"{os}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
        var directories = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native"),
            AppContext.BaseDirectory,
        };

        foreach (var dir in directories)
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (System.IO.File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                    return handle;
            }
        }

        // Last resort: whatever the system loader can find by name.
        foreach (var name in names)
        {
            if (NativeLibrary.TryLoad(name, out var handle))
                return handle;
        }

        throw new DllNotFoundException(
            "The assimp native library could not be found; FBX import and export are unavailable.");
    }

    private sealed class StaticContext(nint handle) : INativeContext
    {
        public nint GetProcAddress(string proc, int? slot = null) => NativeLibrary.GetExport(handle, proc);

        public bool TryGetProcAddress(string proc, out nint addr, int? slot = null)
            => NativeLibrary.TryGetExport(handle, proc, out addr);

        public void Dispose()
        {
        }
    }
}
