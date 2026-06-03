using System.Runtime.InteropServices;

namespace Syphon.NET.Interop;

/// <summary>
/// Minimal P/Invoke surface for the IOSurface and CoreFoundation system frameworks.
/// IOSurface is a plain C API, so it is bound directly rather than through the shim.
/// </summary>
internal static partial class CoreFoundation
{
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string IOSurfaceLib = "/System/Library/Frameworks/IOSurface.framework/IOSurface";

    /// <summary>Lock the surface for read-only access (no GPU flush of CPU writes).</summary>
    internal const uint LockReadOnly = 0x00000001;

    [LibraryImport(CoreFoundationLib)]
    internal static partial void CFRetain(nint cf);

    [LibraryImport(CoreFoundationLib)]
    internal static partial void CFRelease(nint cf);

    [LibraryImport(IOSurfaceLib)]
    internal static partial nuint IOSurfaceGetWidth(nint surface);

    [LibraryImport(IOSurfaceLib)]
    internal static partial nuint IOSurfaceGetHeight(nint surface);

    [LibraryImport(IOSurfaceLib)]
    internal static partial nuint IOSurfaceGetBytesPerRow(nint surface);

    [LibraryImport(IOSurfaceLib)]
    internal static partial nuint IOSurfaceGetAllocSize(nint surface);

    [LibraryImport(IOSurfaceLib)]
    internal static partial uint IOSurfaceGetPixelFormat(nint surface);

    [LibraryImport(IOSurfaceLib)]
    internal static partial nint IOSurfaceGetBaseAddress(nint surface);

    [LibraryImport(IOSurfaceLib)]
    internal static partial int IOSurfaceLock(nint surface, uint options, nint seed);

    [LibraryImport(IOSurfaceLib)]
    internal static partial int IOSurfaceUnlock(nint surface, uint options, nint seed);
}
