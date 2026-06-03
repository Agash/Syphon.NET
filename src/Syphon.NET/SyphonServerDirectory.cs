using System.Text;
using Syphon.NET.Interop;

namespace Syphon.NET;

/// <summary>
/// Discovers Syphon servers published by other applications and creates clients for them.
/// </summary>
public sealed class SyphonServerDirectory : IDisposable
{
    private const int FieldBufferSize = 256;
    private nint _handle;

    /// <summary>Open the shared server directory.</summary>
    public SyphonServerDirectory()
    {
        SyphonRuntime.EnsureInitialized();
        _handle = SyphonNative.sy_directory_create();
        if (_handle == 0)
            throw new InvalidOperationException("Failed to open the Syphon server directory.");
    }

    /// <summary>Number of servers currently advertised.</summary>
    public int Count
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle == 0, this);
            return SyphonNative.sy_directory_count(_handle);
        }
    }

    /// <summary>
    /// Run the calling thread's Cocoa run loop for <paramref name="duration"/> so the directory
    /// receives server announce/retire notifications. Hosts that already run a Cocoa run loop (a
    /// MAUI/AppKit app) do not need this; plain console or server processes must call it (on the
    /// same thread that created this directory) for <see cref="GetServers"/> to discover anything.
    /// </summary>
    public void PumpEvents(TimeSpan duration)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        SyphonNative.sy_pump(duration.TotalSeconds);
    }

    /// <summary>Snapshot the currently advertised servers.</summary>
    public IReadOnlyList<SyphonServerDescription> GetServers()
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        int count = SyphonNative.sy_directory_count(_handle);
        var list = new List<SyphonServerDescription>(count);

        byte[] uuid = new byte[FieldBufferSize];
        byte[] app = new byte[FieldBufferSize];
        byte[] name = new byte[FieldBufferSize];
        for (int i = 0; i < count; i++)
        {
            if (SyphonNative.sy_directory_get(_handle, i, uuid, app, name, FieldBufferSize) != 0)
                continue;
            list.Add(new SyphonServerDescription(Decode(uuid), Decode(app), Decode(name)));
        }
        return list;
    }

    /// <summary>
    /// Create a client for the server at <paramref name="index"/> within the current snapshot.
    /// The optional <paramref name="onFrameReady"/> handler fires when a new frame arrives;
    /// frames are retrieved with <see cref="SyphonClient.TryGetFrame"/>.
    /// </summary>
    public SyphonClient CreateClient(int index, Action? onFrameReady = null)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        return SyphonClient.FromDirectory(_handle, index, onFrameReady);
    }

    private static string Decode(byte[] buffer)
    {
        int end = Array.IndexOf<byte>(buffer, 0);
        if (end < 0) end = buffer.Length;
        return Encoding.UTF8.GetString(buffer, 0, end);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        nint h = Interlocked.Exchange(ref _handle, 0);
        if (h != 0) SyphonNative.sy_directory_destroy(h);
    }
}
