using System.Runtime.InteropServices;
using IOSurface;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ObjCRuntime;
using Syphon.NET.Interop;

namespace Syphon.NET;

/// <summary>
/// Receives video frames from a Syphon server. Either subscribe to <see cref="FrameReady"/> and
/// pull on notification, or poll <see cref="TryGetFrame"/> on your own cadence (for example a
/// render or encode loop). Frames are shared zero-copy through IOSurface.
/// </summary>
public sealed partial class SyphonClient : IDisposable
{
    private readonly ILogger _logger;
    private nint _handle;
    private GCHandle _self;
    private bool _firstFrameLogged;

    /// <summary>Raised on an arbitrary thread when a new frame becomes available.</summary>
    public event Action? FrameReady;

    private unsafe SyphonClient(Action? onFrameReady, Func<nint, nint, nint> createNative, string failure, ILogger logger)
    {
        SyphonRuntime.EnsureInitialized();
        _logger = logger;
        if (onFrameReady is not null) FrameReady += onFrameReady;

        _self = GCHandle.Alloc(this, GCHandleType.Weak);
        nint cb = (nint)(delegate* unmanaged<nint, void>)&OnNewFrame;
        _handle = createNative(cb, GCHandle.ToIntPtr(_self));
        if (_handle == 0)
        {
            _self.Free();
            throw new InvalidOperationException(failure);
        }
        LogCreated();
    }

    internal static SyphonClient FromDirectory(nint directory, int index, Action? onFrameReady, ILogger logger) =>
        new(onFrameReady,
            (cb, ctx) => SyphonNative.sy_client_create(directory, index, cb, ctx),
            $"Failed to create a Syphon client for server index {index} (stale or out of range).", logger);

    internal static SyphonClient ForServer(nint server, Action? onFrameReady, ILogger logger) =>
        new(onFrameReady,
            (cb, ctx) => SyphonNative.sy_client_create_for_server(server, cb, ctx),
            "Failed to create a loopback Syphon client for the server.", logger);

    /// <summary>
    /// Connect to a server using a description exported via <see cref="SyphonServer.ExportDescription"/>,
    /// typically obtained from another process out of band. Needs no directory or Cocoa run loop.
    /// </summary>
    /// <param name="description">A description blob from <see cref="SyphonServer.ExportDescription"/>.</param>
    /// <param name="onFrameReady">Optional handler raised when a new frame arrives.</param>
    /// <param name="loggerFactory">Optional factory for Debug/Trace diagnostics; omit for none.</param>
    public static SyphonClient Connect(ReadOnlySpan<byte> description, Action? onFrameReady = null, ILoggerFactory? loggerFactory = null)
    {
        byte[] desc = description.ToArray();
        return new SyphonClient(onFrameReady,
            (cb, ctx) => SyphonNative.sy_client_create_from_description(desc, desc.Length, cb, ctx),
            "Failed to connect a Syphon client from the exported description.",
            (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("Syphon.NET.Client"));
    }

    /// <summary>True while connected to a live server.</summary>
    public bool IsValid => _handle != 0 && SyphonNative.sy_client_is_valid(_handle) != 0;

    /// <summary>
    /// Return the latest frame's backing <see cref="IOSurface.IOSurface"/>, or <c>null</c> if no new frame is available
    /// since the last call. The surface is returned retained; dispose it once consumed (it releases the
    /// retain). Zero-copy - the bytes live in shared GPU memory.
    /// </summary>
    public IOSurface.IOSurface? TryGetFrame()
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        nint surface = SyphonNative.sy_client_copy_new_frame(_handle);
        if (surface == 0) return null;
        if (!_firstFrameLogged) { _firstFrameLogged = true; LogFirstFrame(); }
        return Runtime.GetINativeObject<IOSurface.IOSurface>(surface, owns: true);
    }

    [UnmanagedCallersOnly]
    private static void OnNewFrame(nint ctx)
    {
        try
        {
            if (GCHandle.FromIntPtr(ctx).Target is SyphonClient client)
                client.FrameReady?.Invoke();
        }
        catch
        {
            // Callbacks must never let an exception cross the native boundary.
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        nint h = Interlocked.Exchange(ref _handle, 0);
        if (h != 0) SyphonNative.sy_client_destroy(h);
        if (_self.IsAllocated) _self.Free();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "client created")]
    private partial void LogCreated();

    [LoggerMessage(Level = LogLevel.Debug, Message = "first frame received")]
    private partial void LogFirstFrame();
}
