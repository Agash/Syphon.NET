using Syphon.NET.Interop;

namespace Syphon.NET;

/// <summary>
/// Publishes video frames for other applications to consume (for example an OBS Syphon source).
/// Frames are shared zero-copy through IOSurface-backed Metal textures.
/// </summary>
/// <remarks>
/// There are two ways to publish:
/// <list type="bullet">
/// <item>For GPU-produced frames you already hold as an IOSurface (such as a VideoToolbox
/// CVPixelBuffer's surface), call <see cref="Publish(IOSurface, bool)"/> directly.</item>
/// <item>For CPU-produced frames, call <see cref="AcquireSurface"/> to get a writable surface,
/// fill it, then call <see cref="PublishCurrent"/>. <see cref="PublishPixels"/> wraps that.</item>
/// </list>
/// </remarks>
public sealed class SyphonServer : IDisposable
{
    private nint _handle;

    /// <summary>Create a server advertised to other applications under <paramref name="name"/>.</summary>
    public SyphonServer(string? name = null)
    {
        SyphonRuntime.EnsureInitialized();
        Name = name;
        _handle = SyphonNative.sy_server_create(name);
        if (_handle == 0)
            throw new InvalidOperationException("Failed to create the Syphon server.");
    }

    /// <summary>The advertised server name, if any.</summary>
    public string? Name { get; }

    /// <summary>True while at least one client is connected.</summary>
    public bool HasClients => _handle != 0 && SyphonNative.sy_server_has_clients(_handle) != 0;

    /// <summary>Publish an IOSurface you own directly (zero-copy for GPU producers).</summary>
    public void Publish(IOSurface surface, bool flipped = false)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        if (!surface.IsValid) throw new ArgumentException("Surface is not valid.", nameof(surface));
        if (SyphonNative.sy_server_publish_surface(_handle, surface.Handle, flipped ? 1 : 0) != 0)
            throw new InvalidOperationException("Failed to publish the surface.");
    }

    /// <summary>
    /// Get a server-owned writable surface of the given size and format, recreated when the
    /// dimensions or format change. Write pixels into it, then call <see cref="PublishCurrent"/>.
    /// </summary>
    public IOSurface AcquireSurface(int width, int height, SyphonPixelFormat format = SyphonPixelFormat.Bgra)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        nint surface = SyphonNative.sy_server_acquire_surface(_handle, (uint)width, (uint)height, (uint)format);
        if (surface == 0) throw new InvalidOperationException("Failed to acquire a surface.");
        return new IOSurface(surface);
    }

    /// <summary>Publish the surface most recently returned by <see cref="AcquireSurface"/>.</summary>
    public void PublishCurrent(bool flipped = false)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        if (SyphonNative.sy_server_publish_current(_handle, flipped ? 1 : 0) != 0)
            throw new InvalidOperationException("Failed to publish the current surface.");
    }

    /// <summary>
    /// Convenience for CPU producers: acquire a surface of the given size, copy
    /// <paramref name="pixels"/> (tightly packed rows of <c>width * 4</c> bytes) into it, and publish.
    /// </summary>
    public void PublishPixels(ReadOnlySpan<byte> pixels, int width, int height,
        SyphonPixelFormat format = SyphonPixelFormat.Bgra, bool flipped = false)
    {
        int rowBytes = width * 4;
        if (pixels.Length < rowBytes * height)
            throw new ArgumentException("Pixel buffer is smaller than width * 4 * height.", nameof(pixels));

        IOSurface surface = AcquireSurface(width, height, format);
        using (IOSurface.Lock locked = surface.LockBytes(readOnly: false))
        {
            Span<byte> dst = locked.Bytes;
            int stride = surface.BytesPerRow;
            for (int row = 0; row < height; row++)
                pixels.Slice(row * rowBytes, rowBytes).CopyTo(dst.Slice(row * stride, rowBytes));
        }
        PublishCurrent(flipped);
    }

    /// <summary>
    /// Create a client connected directly to this server, bypassing the distributed-notification
    /// directory. Useful for previewing your own output, and works in hosts that do not run a
    /// Cocoa run loop (which the directory requires).
    /// </summary>
    public SyphonClient CreateLoopbackClient(Action? onFrameReady = null)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        return SyphonClient.ForServer(_handle, onFrameReady);
    }

    /// <summary>
    /// Export this server's connection description as a portable byte blob. Hand it to another
    /// process (over any channel) and connect there with <see cref="SyphonClient.Connect"/> to
    /// receive frames without going through the distributed-notification directory.
    /// </summary>
    public byte[] ExportDescription()
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        int needed = SyphonNative.sy_server_copy_description(_handle, null, 0);
        if (needed <= 0) throw new InvalidOperationException("Failed to export the server description.");
        byte[] buffer = new byte[needed];
        int written = SyphonNative.sy_server_copy_description(_handle, buffer, buffer.Length);
        if (written != needed) throw new InvalidOperationException("Failed to export the server description.");
        return buffer;
    }

    /// <summary>
    /// Run the calling thread's Cocoa run loop for <paramref name="duration"/> so a server hosted
    /// in a process without its own run loop keeps responding to directory announce requests and
    /// stays discoverable. Call it periodically (for example once per published frame). Apps with a
    /// Cocoa run loop (MAUI/AppKit) do not need this.
    /// </summary>
    public static void PumpEvents(TimeSpan duration) => SyphonNative.sy_pump(duration.TotalSeconds);

    /// <inheritdoc/>
    public void Dispose()
    {
        nint h = Interlocked.Exchange(ref _handle, 0);
        if (h != 0) SyphonNative.sy_server_destroy(h);
    }
}
