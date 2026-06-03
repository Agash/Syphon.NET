using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("Syphon.NET.Tests")]

namespace Syphon.NET;

internal static class AssemblyInitializer
{
    // A single SetDllImportResolver call (microseconds) is the canonical AOT-safe way to
    // locate the bundled native shim. CA2255 warns against [ModuleInitializer] in libraries;
    // the trade-off is acceptable here as there is no other entry point a consumer must call.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(AssemblyInitializer).Assembly,
            static (name, asm, path) =>
            {
                if (name is not Interop.SyphonNative.Lib) return 0;
                // Packaged as runtimes/osx-*/native/libsyphon_shim.dylib (resolved by the runtime),
                // or copied next to the assembly for local builds and tests.
                if (NativeLibrary.TryLoad("libsyphon_shim.dylib", asm, path, out nint h)) return h;
                if (NativeLibrary.TryLoad("syphon_shim", asm, path, out h)) return h;
                return 0;
            });
    }
}
