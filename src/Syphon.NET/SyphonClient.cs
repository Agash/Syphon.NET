using System.Runtime.InteropServices;
using Syphon.NET.Interop;

namespace Syphon.NET;

/// <summary>
/// Receives video frames from a Syphon server. Either subscribe to <see cref="FrameReady"/> and
/// pull on notification, or poll <see cref="TryGetFrame"/> on your own cadence (for example a
/// render or encode loop). Frames are shared zero-copy through IOSurface.
/// </summary>
public sealed class SyphonClient : IDisposable
{
    private nint _handle;
    private GCHandle _self;

    /// <summary>Raised on an arbitrary thread when a new frame becomes available.</summary>
    public event Action? FrameReady;

    private unsafe SyphonClient(Action? onFrameReady, Func<nint, nint, nint> createNative, string failure)
    {
        SyphonRuntime.EnsureInitialized();
        if (onFrameReady is not null) FrameReady += onFrameReady;

        _self = GCHandle.Alloc(this, GCHandleType.Weak);
        nint cb = (nint)(delegate* unmanaged<nint, void>)&OnNewFrame;
        _handle = createNative(cb, GCHandle.ToIntPtr(_self));
        if (_handle == 0)
        {
            _self.Free();
            throw new InvalidOperationException(failure);
        }
    }

    internal static SyphonClient FromDirectory(nint directory, int index, Action? onFrameReady) =>
        new(onFrameReady,
            (cb, ctx) => SyphonNative.sy_client_create(directory, index, cb, ctx),
            $"Failed to create a Syphon client for server index {index} (stale or out of range).");

    internal static SyphonClient ForServer(nint server, Action? onFrameReady) =>
        new(onFrameReady,
            (cb, ctx) => SyphonNative.sy_client_create_for_server(server, cb, ctx),
            "Failed to create a loopback Syphon client for the server.");

    /// <summary>
    /// Connect to a server using a description exported via <see cref="SyphonServer.ExportDescription"/>,
    /// typically obtained from another process out of band. Needs no directory or Cocoa run loop.
    /// </summary>
    public static SyphonClient Connect(ReadOnlySpan<byte> description, Action? onFrameReady = null)
    {
        byte[] desc = description.ToArray();
        return new SyphonClient(onFrameReady,
            (cb, ctx) => SyphonNative.sy_client_create_from_description(desc, desc.Length, cb, ctx),
            "Failed to connect a Syphon client from the exported description.");
    }

    /// <summary>True while connected to a live server.</summary>
    public bool IsValid => _handle != 0 && SyphonNative.sy_client_is_valid(_handle) != 0;

    /// <summary>
    /// Return the latest frame, or <c>null</c> if no new frame is available since the last call.
    /// Dispose the returned frame once consumed.
    /// </summary>
    public SyphonFrame? TryGetFrame()
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        nint surface = SyphonNative.sy_client_copy_new_frame(_handle);
        return surface == 0 ? null : new SyphonFrame(surface);
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
}
