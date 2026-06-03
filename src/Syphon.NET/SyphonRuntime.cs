using Syphon.NET.Interop;

namespace Syphon.NET;

/// <summary>Shared initialisation of the native Metal device backing all servers and clients.</summary>
internal static class SyphonRuntime
{
    private static int s_initialized;

    /// <summary>
    /// Initialise the shared Metal device once. Throws if no Metal device is available
    /// (for example on a headless machine without a GPU).
    /// </summary>
    internal static void EnsureInitialized()
    {
        if (Volatile.Read(ref s_initialized) != 0) return;
        if (SyphonNative.sy_init() != 0)
            throw new PlatformNotSupportedException(
                "Syphon requires a Metal-capable macOS device; none was available.");
        Volatile.Write(ref s_initialized, 1);
    }
}
