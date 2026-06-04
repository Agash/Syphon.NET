using System.Runtime.InteropServices;

namespace Syphon.NET.Interop;

/// <summary>
/// P/Invoke surface over the native shim (<c>libsyphon_shim.dylib</c>), whose flat C ABI
/// is declared in <c>native/include/syphon_shim.h</c>. All handles are pointer-sized;
/// IOSurface handles are plain CoreFoundation references.
/// </summary>
internal static partial class SyphonNative
{
    internal const string Lib = "syphon_shim";

    [LibraryImport(Lib)]
    internal static partial int sy_init();

    [LibraryImport(Lib)]
    internal static partial void sy_pump(double seconds);

    // ---- Server ----

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint sy_server_create(string? name);

    [LibraryImport(Lib)]
    internal static partial void sy_server_destroy(nint server);

    [LibraryImport(Lib)]
    internal static partial int sy_server_has_clients(nint server);

    [LibraryImport(Lib)]
    internal static partial int sy_server_publish_surface(nint server, nint surface, int flipped);

    [LibraryImport(Lib)]
    internal static partial nint sy_server_acquire_surface(nint server, uint width, uint height, uint pixelFormat);

    [LibraryImport(Lib)]
    internal static partial int sy_server_publish_current(nint server, int flipped);

    // ---- Directory ----

    [LibraryImport(Lib)]
    internal static partial nint sy_directory_create();

    [LibraryImport(Lib)]
    internal static partial void sy_directory_destroy(nint dir);

    [LibraryImport(Lib)]
    internal static partial int sy_directory_count(nint dir);

    [LibraryImport(Lib)]
    internal static partial int sy_directory_get(nint dir, int index, byte[]? uuid, byte[]? appName, byte[]? name, int bufLen);

    // ---- Client ----

    [LibraryImport(Lib)]
    internal static partial nint sy_client_create(nint dir, int index, nint cb, nint ctx);

    [LibraryImport(Lib)]
    internal static partial nint sy_client_create_for_server(nint server, nint cb, nint ctx);

    [LibraryImport(Lib)]
    internal static partial int sy_server_copy_description(nint server, byte[]? buf, int bufLen);

    [LibraryImport(Lib)]
    internal static partial nint sy_client_create_from_description(byte[] desc, int descLen, nint cb, nint ctx);

    [LibraryImport(Lib)]
    internal static partial void sy_client_destroy(nint client);

    [LibraryImport(Lib)]
    internal static partial int sy_client_is_valid(nint client);

    [LibraryImport(Lib)]
    internal static partial nint sy_client_copy_new_frame(nint client);

    // ---- Surface effect (general GPU fragment-shader pass over IOSurfaces) ----

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint sy_effect_create(string mslSource, string fragmentFunction);

    [LibraryImport(Lib)]
    internal static partial void sy_effect_destroy(nint effect);

    [LibraryImport(Lib)]
    internal static partial nint sy_effect_render(nint effect, uint outWidth, uint outHeight,
        [In] nint[] inSurfaces, [In] uint[] inPlanes, [In] uint[] inFormats, int count);
}
