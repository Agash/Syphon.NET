using Syphon.NET.Interop;

namespace Syphon.NET;

/// <summary>
/// A non-owning view over an IOSurface, the zero-copy GPU buffer that Syphon shares between
/// processes. Provides dimensions, layout, and CPU access. The handle lifetime is managed by
/// whatever produced it (a <see cref="SyphonServer"/> owns surfaces it hands out;
/// <see cref="SyphonFrame"/> owns received frames). Pass <see cref="Handle"/> to other Apple
/// APIs (for example wrap it in a CVPixelBuffer for a zero-copy VideoToolbox encode).
/// </summary>
public readonly struct IOSurface : IEquatable<IOSurface>
{
    /// <summary>The underlying <c>IOSurfaceRef</c>.</summary>
    public nint Handle { get; }

    /// <summary>
    /// Wrap an existing <c>IOSurfaceRef</c> as a non-owning view, so a surface produced by another Apple
    /// API (for example a VideoToolbox decode's CVPixelBuffer surface) can be published zero-copy through
    /// <see cref="SyphonServer.Publish(IOSurface, bool)"/>. The caller owns the handle's lifetime.
    /// </summary>
    public IOSurface(nint handle) => Handle = handle;

    /// <summary>True if this view refers to a real surface.</summary>
    public bool IsValid => Handle != 0;

    /// <summary>Width in pixels.</summary>
    public int Width => (int)CoreFoundation.IOSurfaceGetWidth(Handle);

    /// <summary>Height in pixels.</summary>
    public int Height => (int)CoreFoundation.IOSurfaceGetHeight(Handle);

    /// <summary>Number of bytes per row, which may exceed <c>Width * 4</c> due to alignment.</summary>
    public int BytesPerRow => (int)CoreFoundation.IOSurfaceGetBytesPerRow(Handle);

    /// <summary>Total allocation size in bytes.</summary>
    public int AllocSize => (int)CoreFoundation.IOSurfaceGetAllocSize(Handle);

    /// <summary>Pixel format as a FourCC code.</summary>
    public SyphonPixelFormat PixelFormat => (SyphonPixelFormat)CoreFoundation.IOSurfaceGetPixelFormat(Handle);

    /// <summary>
    /// Lock the surface and return a writable view over its bytes. Dispose the returned scope
    /// to unlock. Use <paramref name="readOnly"/> for read access to avoid a needless GPU sync.
    /// </summary>
    public Lock LockBytes(bool readOnly) => new(Handle, readOnly);

    /// <summary>A locked window over an IOSurface's CPU memory. Unlocks on dispose.</summary>
    public readonly ref struct Lock
    {
        private readonly nint _handle;
        private readonly uint _options;
        private readonly nint _base;
        private readonly int _length;

        internal Lock(nint handle, bool readOnly)
        {
            _handle = handle;
            _options = readOnly ? CoreFoundation.LockReadOnly : 0;
            int rc = CoreFoundation.IOSurfaceLock(handle, _options, 0);
            if (rc != 0) throw new InvalidOperationException($"IOSurfaceLock failed ({rc}).");
            _base = CoreFoundation.IOSurfaceGetBaseAddress(handle);
            _length = (int)CoreFoundation.IOSurfaceGetAllocSize(handle);
        }

        /// <summary>The surface's raw bytes (row-padded; stride is <see cref="IOSurface.BytesPerRow"/>).</summary>
        public unsafe Span<byte> Bytes => new((void*)_base, _length);

        /// <summary>Unlock the surface.</summary>
        public void Dispose() => CoreFoundation.IOSurfaceUnlock(_handle, _options, 0);
    }

    /// <inheritdoc/>
    public bool Equals(IOSurface other) => Handle == other.Handle;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is IOSurface other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Handle.GetHashCode();

    /// <summary>Two views are equal when they refer to the same surface handle.</summary>
    public static bool operator ==(IOSurface left, IOSurface right) => left.Equals(right);

    /// <summary>Two views are unequal when they refer to different surface handles.</summary>
    public static bool operator !=(IOSurface left, IOSurface right) => !left.Equals(right);
}
