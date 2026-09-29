using Metal;

namespace Syphon.NET;

// IOSurfaces as Syphon uses them: 8-bit BGRA, and for servers global, so a client in another process
// can look them up by ID.
internal static class Surfaces
{
    private const uint Bgra = 0x42475241; // 'BGRA', kCVPixelFormatType_32BGRA

    public static IOSurface.IOSurface CreateGlobal(int width, int height) =>
        new(new GlobalSurfaceOptions(width, height));

    public static IOSurface.IOSurface CreateLocal(int width, int height) =>
        new(
            new IOSurface.IOSurfaceOptions
            {
                Width = width,
                Height = height,
                PixelFormat = Bgra,
                BytesPerElement = 4,
            }
        );

    // A Metal texture over a surface, as SyphonMetalClient and the Metal server wrap theirs.
    public static IMTLTexture Texture(IMTLDevice device, IOSurface.IOSurface surface)
    {
        ArgumentNullException.ThrowIfNull(device);
        MTLTextureDescriptor descriptor = MTLTextureDescriptor.CreateTexture2DDescriptor(
            MTLPixelFormat.BGRA8Unorm,
            (nuint)surface.Width,
            (nuint)surface.Height,
            mipmapped: false
        );
        descriptor.Usage =
            MTLTextureUsage.ShaderRead | MTLTextureUsage.ShaderWrite | MTLTextureUsage.RenderTarget;
        descriptor.StorageMode = MTLStorageMode.Shared;
        return device.CreateTexture(descriptor, surface, 0)
            ?? throw new SyphonException("The IOSurface could not back a Metal texture.");
    }

    public static unsafe void Write(
        IOSurface.IOSurface surface,
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride
    )
    {
        _ = surface.Lock(IOSurface.IOSurfaceLockOptions.AvoidSync);
        try
        {
            byte* target = (byte*)surface.BaseAddress;
            int targetStride = (int)surface.BytesPerRow;
            for (int y = 0; y < height; y++)
            {
                pixels
                    .Slice(y * stride, width * 4)
                    .CopyTo(new Span<byte>(target + (y * targetStride), width * 4));
            }
        }
        finally
        {
            _ = surface.Unlock(IOSurface.IOSurfaceLockOptions.AvoidSync);
        }
    }

    // Copies a surface's pixels, tightly packed, into memory.
    public static unsafe int Read(IOSurface.IOSurface surface, Span<byte> destination)
    {
        int width = (int)surface.Width;
        int height = (int)surface.Height;
        int rowBytes = width * 4;
        if (destination.Length < rowBytes * height)
        {
            throw new ArgumentException(
                $"The frame is {rowBytes * height} bytes; the destination holds {destination.Length}.",
                nameof(destination)
            );
        }

        _ = surface.Lock(IOSurface.IOSurfaceLockOptions.ReadOnly);
        try
        {
            byte* source = (byte*)surface.BaseAddress;
            int sourceStride = (int)surface.BytesPerRow;
            for (int y = 0; y < height; y++)
            {
                new ReadOnlySpan<byte>(source + (y * sourceStride), rowBytes).CopyTo(
                    destination[(y * rowBytes)..]
                );
            }
        }
        finally
        {
            _ = surface.Unlock(IOSurface.IOSurfaceLockOptions.ReadOnly);
        }

        return rowBytes * height;
    }

    // kIOSurfaceIsGlobal is deprecated, and Microsoft's IOSurfaceOptions does not carry it, but it
    // is what lets another process find the surface by ID, which is how every Syphon client finds a
    // server's frames (SyphonServerBase.newSurfaceForWidth).
    private sealed class GlobalSurfaceOptions : IOSurface.IOSurfaceOptions
    {
        public GlobalSurfaceOptions(int width, int height)
        {
            Width = width;
            Height = height;
            PixelFormat = Bgra;
            BytesPerElement = 4;
            SetBooleanValue(new NSString("IOSurfaceIsGlobal"), true);
        }
    }
}
