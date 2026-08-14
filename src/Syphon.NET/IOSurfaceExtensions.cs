using CoreVideo;
using IOSurface;

namespace Syphon.NET;

/// <summary>
/// Ergonomic helpers over the Microsoft <see cref="IOSurface.IOSurface"/> binding - the bits it doesn't surface directly:
/// a scoped CPU lock yielding a <see cref="Span{T}"/>, tightly-packed copy in/out, int-typed dimensions and
/// per-plane info, pixel-format predicates, and a creation factory. These add only convenience; the surface
/// type itself is used directly (no wrapper), and the zero-copy GPU paths keep using the raw handle.
/// </summary>
public static class IOSurfaceExtensions
{
    private const uint FourCcBgra = 0x42475241;       // 'BGRA'
    private const uint FourCcNv12VideoRange = 0x34323076; // '420v'
    private const uint FourCcNv12FullRange = 0x34323066;  // '420f'

    // ---- Dimensions / format -----------------------------------------------------------------------------

    /// <summary>Surface dimensions as <see cref="int"/> (the binding exposes them as <see cref="nint"/>).</summary>
    public static (int Width, int Height) PixelSize(this IOSurface.IOSurface surface) =>
        ((int)surface.Width, (int)surface.Height);

    /// <summary>True if the surface is 32-bit BGRA (the Syphon/Metal default).</summary>
    public static bool IsBgra(this IOSurface.IOSurface surface) => surface.PixelFormat == FourCcBgra;

    /// <summary>True if the surface is NV12 biplanar (either range) - what a VideoToolbox hardware decode emits.</summary>
    public static bool IsNv12(this IOSurface.IOSurface surface) =>
        surface.PixelFormat == FourCcNv12VideoRange || surface.PixelFormat == FourCcNv12FullRange;

    /// <summary>Number of planes (1 for packed BGRA, 2 for NV12).</summary>
    /// <remarks>
    /// IOSurface itself reports <c>0</c> for a non-planar surface and rejects its per-plane accessors
    /// outright. A packed surface is one plane everywhere it matters (it is what <see cref="PlaneInfo"/>
    /// describes at index 0), so it is reported as such here - matching CoreVideo's plane model.
    /// </remarks>
    public static int PlaneCount(this IOSurface.IOSurface surface) => Math.Max(1, (int)surface.PlaneCount);

    /// <summary>Dimensions and row stride of a plane (plane 0 = luma/BGRA, plane 1 = NV12 CbCr), as ints.</summary>
    /// <remarks>
    /// On a non-planar surface the per-plane accessors raise <c>NSGenericException</c> ("surface is not
    /// planar"), so plane 0 is answered from the surface itself.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The surface has no such plane.</exception>
    public static (int Width, int Height, int BytesPerRow) PlaneInfo(this IOSurface.IOSurface surface, int plane)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plane);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(plane, surface.PlaneCount());

        if (surface.PlaneCount == 0)
            return ((int)surface.Width, (int)surface.Height, (int)surface.BytesPerRow);

        return ((int)surface.GetWidth((nuint)plane), (int)surface.GetHeight((nuint)plane), (int)surface.GetBytesPerRow((nuint)plane));
    }

    // ---- Scoped CPU access -------------------------------------------------------------------------------

    /// <summary>
    /// Lock the surface for CPU access and return a scope exposing its bytes. Dispose (e.g. with
    /// <c>using</c>) to unlock. Pass <paramref name="readOnly"/> for reads to avoid a needless GPU sync.
    /// </summary>
    public static LockedSurface LockBytes(this IOSurface.IOSurface surface, bool readOnly) => new(surface, readOnly);

    /// <summary>Copy the surface's pixels out as tightly-packed rows (stride collapsed to <c>Width * bytesPerPixel</c>).</summary>
    /// <returns>The number of bytes written (<c>Width * bytesPerPixel * Height</c>).</returns>
    public static int CopyTightlyPacked(this IOSurface.IOSurface surface, Span<byte> destination, int bytesPerPixel = 4)
    {
        int width = (int)surface.Width, height = (int)surface.Height;
        int rowBytes = width * bytesPerPixel;
        int needed = rowBytes * height;
        if (destination.Length < needed)
            throw new ArgumentException($"Destination too small: need {needed} bytes.", nameof(destination));

        using LockedSurface locked = surface.LockBytes(readOnly: true);
        ReadOnlySpan<byte> src = locked.Bytes;
        int stride = locked.BytesPerRow;
        for (int row = 0; row < height; row++)
            src.Slice(row * stride, rowBytes).CopyTo(destination.Slice(row * rowBytes, rowBytes));
        return needed;
    }

    /// <summary>Copy tightly-packed rows of <paramref name="source"/> into the surface (respecting its row stride).</summary>
    public static void WritePixels(this IOSurface.IOSurface surface, ReadOnlySpan<byte> source, int bytesPerPixel = 4)
    {
        int width = (int)surface.Width, height = (int)surface.Height;
        int rowBytes = width * bytesPerPixel;
        if (source.Length < rowBytes * height)
            throw new ArgumentException("Source is smaller than Width * bytesPerPixel * Height.", nameof(source));

        using LockedSurface locked = surface.LockBytes(readOnly: false);
        Span<byte> dst = locked.Bytes;
        int stride = locked.BytesPerRow;
        for (int row = 0; row < height; row++)
            source.Slice(row * rowBytes, rowBytes).CopyTo(dst.Slice(row * stride, rowBytes));
    }

    /// <summary>A locked window over an IOSurface's CPU memory; unlocks on dispose.</summary>
    public readonly ref struct LockedSurface
    {
        private readonly IOSurface.IOSurface _surface;
        private readonly IOSurfaceLockOptions _options;

        internal LockedSurface(IOSurface.IOSurface surface, bool readOnly)
        {
            _surface = surface;
            _options = readOnly ? IOSurfaceLockOptions.ReadOnly : default;
            int seed = 0;
            _surface.Lock(_options, ref seed);
        }

        /// <summary>Bytes per row, which may exceed <c>Width * bytesPerPixel</c> due to alignment.</summary>
        public int BytesPerRow => (int)_surface.BytesPerRow;

        /// <summary>The surface's raw bytes (row-padded; stride is <see cref="BytesPerRow"/>).</summary>
        public unsafe Span<byte> Bytes => new((void*)_surface.BaseAddress, (int)_surface.AllocationSize);

        /// <summary>Unlock the surface.</summary>
        public void Dispose()
        {
            int seed = 0;
            _surface.Unlock(_options, ref seed);
        }
    }

    // ---- Creation ----------------------------------------------------------------------------------------

    /// <summary>Create a single-plane IOSurface of the given size and pixel format (e.g. for a Metal output target).</summary>
    public static IOSurface.IOSurface Create(int width, int height, CVPixelFormatType format, int bytesPerElement = 4) =>
        new(new IOSurfaceOptions
        {
            Width = width,
            Height = height,
            BytesPerElement = bytesPerElement,
            PixelFormat = (uint)format,
        });

    /// <summary>Create a 32-bit BGRA IOSurface of the given size (the common Metal/Syphon output format).</summary>
    public static IOSurface.IOSurface CreateBgra(int width, int height) => Create(width, height, CVPixelFormatType.CV32BGRA);
}
