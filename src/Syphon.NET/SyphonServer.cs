using CoreVideo;
using IOSurface;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ObjCRuntime;
using Syphon.NET.Interop;

namespace Syphon.NET;

/// <summary>
/// Publishes video frames for other applications to consume (for example an OBS Syphon source).
/// Frames are shared zero-copy through IOSurface-backed Metal textures. Surfaces are the Microsoft
/// <see cref="IOSurface.IOSurface"/> bindings directly - no wrapper type.
/// </summary>
/// <remarks>
/// There are two ways to publish:
/// <list type="bullet">
/// <item>For GPU-produced frames you already hold as an <see cref="IOSurface.IOSurface"/> (such as a VideoToolbox
/// CVPixelBuffer's surface), call <see cref="Publish(IOSurface.IOSurface, bool)"/> directly.</item>
/// <item>For CPU-produced frames, call <see cref="AcquireSurface"/> to get a writable surface,
/// fill it, then call <see cref="PublishCurrent"/>. <see cref="PublishPixels"/> wraps that.</item>
/// </list>
/// </remarks>
public sealed partial class SyphonServer : IDisposable
{
    private readonly ILogger _logger;
    private nint _handle;
    private bool _firstPublishLogged;

    /// <summary>Create a server advertised to other applications under <paramref name="name"/>.</summary>
    /// <param name="name">Server name advertised to clients.</param>
    /// <param name="loggerFactory">Optional factory for Debug/Trace diagnostics; omit for none.</param>
    public SyphonServer(string? name = null, ILoggerFactory? loggerFactory = null)
    {
        SyphonRuntime.EnsureInitialized();
        Name = name;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger($"Syphon.NET.Server.{name ?? "(unnamed)"}");
        _handle = SyphonNative.sy_server_create(name);
        if (_handle == 0)
            throw new InvalidOperationException("Failed to create the Syphon server.");
        LogCreated();
    }

    private void LogFirstPublish()
    {
        if (_firstPublishLogged) return;
        _firstPublishLogged = true;
        LogFirstFrame(HasClients);
    }

    /// <summary>The advertised server name, if any.</summary>
    public string? Name { get; }

    /// <summary>True while at least one client is connected.</summary>
    public bool HasClients => _handle != 0 && SyphonNative.sy_server_has_clients(_handle) != 0;

    /// <summary>Publish an IOSurface you own directly (zero-copy for GPU producers).</summary>
    public void Publish(IOSurface.IOSurface surface, bool flipped = false)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        ArgumentNullException.ThrowIfNull(surface);
        if (SyphonNative.sy_server_publish_surface(_handle, surface.Handle.Handle, flipped ? 1 : 0) != 0)
            throw new InvalidOperationException("Failed to publish the surface.");
        LogFirstPublish();
    }

    /// <summary>
    /// Get a server-owned writable surface of the given size and format, recreated when the
    /// dimensions or format change. Write pixels into it, then call <see cref="PublishCurrent"/>.
    /// </summary>
    public IOSurface.IOSurface AcquireSurface(int width, int height, CVPixelFormatType format = CVPixelFormatType.CV32BGRA)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        nint surface = SyphonNative.sy_server_acquire_surface(_handle, (uint)width, (uint)height, (uint)format);
        if (surface == 0) throw new InvalidOperationException("Failed to acquire a surface.");
        // The server owns the surface (it recreates/releases it), so wrap it non-owning.
        return Runtime.GetINativeObject<IOSurface.IOSurface>(surface, owns: false)
            ?? throw new InvalidOperationException("Failed to wrap the acquired surface.");
    }

    /// <summary>Publish the surface most recently returned by <see cref="AcquireSurface"/>.</summary>
    public void PublishCurrent(bool flipped = false)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        if (SyphonNative.sy_server_publish_current(_handle, flipped ? 1 : 0) != 0)
            throw new InvalidOperationException("Failed to publish the current surface.");
        LogFirstPublish();
    }

    /// <summary>
    /// Convenience for CPU producers: acquire a surface of the given size, copy
    /// <paramref name="pixels"/> (tightly packed rows of <c>width * 4</c> bytes) into it, and publish.
    /// </summary>
    public void PublishPixels(ReadOnlySpan<byte> pixels, int width, int height,
        CVPixelFormatType format = CVPixelFormatType.CV32BGRA, bool flipped = false)
    {
        int rowBytes = width * 4;
        if (pixels.Length < rowBytes * height)
            throw new ArgumentException("Pixel buffer is smaller than width * 4 * height.", nameof(pixels));

        IOSurface.IOSurface surface = AcquireSurface(width, height, format);
        using (IOSurfaceExtensions.LockedSurface locked = surface.LockBytes(readOnly: false))
        {
            Span<byte> dst = locked.Bytes;
            int stride = locked.BytesPerRow;
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
        return SyphonClient.ForServer(_handle, onFrameReady, _logger);
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

    /// <summary>
    /// Drain any pending run-loop events without blocking. Call once per published frame to keep the
    /// server discoverable without the per-frame stall that a timed <see cref="PumpEvents"/> incurs when
    /// the run loop is idle.
    /// </summary>
    public static void PumpOnce() => SyphonNative.sy_pump_once();

    /// <inheritdoc/>
    public void Dispose()
    {
        nint h = Interlocked.Exchange(ref _handle, 0);
        if (h != 0) SyphonNative.sy_server_destroy(h);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "server created")]
    private partial void LogCreated();

    [LoggerMessage(Level = LogLevel.Debug, Message = "first frame published (hasClients={HasClients})")]
    private partial void LogFirstFrame(bool hasClients);
}
