using System.Diagnostics;
using Metal;
using Syphon.NET.Protocol;

namespace Syphon.NET;

/// <summary>What <see cref="SyphonClient.TryReceive"/> found.</summary>
public enum SyphonReceiveResult
{
    /// <summary>A frame, valid until it is disposed.</summary>
    Received,

    /// <summary>The server has not published a frame yet.</summary>
    NoFrame,

    /// <summary>The server has stopped; a client stays bound to the server it was made for.</summary>
    ServerRetired,
}

/// <summary>Handles a frame a client delivers; the frame is valid until the handler returns.</summary>
/// <param name="frame">The frame.</param>
public delegate void SyphonFrameHandler(in SyphonFrame frame);

/// <summary>How a client receives.</summary>
public sealed record SyphonClientOptions
{
    /// <summary>
    /// The Metal device <see cref="SyphonFrame.Retain"/> copies on; the system's default device when
    /// null.
    /// </summary>
    public IMTLDevice? Device { get; init; }
}

/// <summary>
/// Receives the frames a Syphon server publishes. Frames are the server's own surface, so reading one
/// costs no copy; the server renders the next frame into the same surface, so
/// <see cref="SyphonFrame.Retain"/> copies a frame to keep it.
/// </summary>
public sealed class SyphonClient : IDisposable
{
    private readonly ClientConnection _connection;
    private readonly SyphonClientOptions _options;
    private readonly SemaphoreSlim _frames = new(0);
    private readonly Lock _gate = new();
    private readonly Stack<IOSurface.IOSurface> _pool = new();
    private IMTLCommandQueue? _queue;
    private long _lastFrame;
    private string _serverName;
    private bool _frameOpen;
    private bool _running;
    private bool _disposed;

    /// <summary>Connects to a server.</summary>
    /// <param name="server">
    /// The server, from a <see cref="SyphonServerDirectory"/> or another process's
    /// <see cref="SyphonServerDescription.ToPropertyList"/>.
    /// </param>
    /// <param name="options">How the client receives.</param>
    /// <exception cref="ArgumentException">The server does not share IOSurfaces.</exception>
    public SyphonClient(SyphonServerDescription server, SyphonClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!server.SharesIOSurfaces)
        {
            throw new ArgumentException("The server shares no IOSurfaces.", nameof(server));
        }

        Server = server;
        _serverName = server.Name;
        _options = options ?? new();
        _connection = new ClientConnection(server.Uuid);
        _connection.NewFrame += () => _frames.Release();
        _connection.Retired += () =>
        {
            _frames.Release();
            ServerRetired?.Invoke(this, EventArgs.Empty);
        };
        _connection.Renamed += name =>
        {
            lock (_gate)
            {
                _serverName = name;
            }
        };
    }

    /// <summary>Raised when the server stops, on a thread of Syphon.NET's.</summary>
    public event EventHandler? ServerRetired;

    /// <summary>The server this client is connected to.</summary>
    public SyphonServerDescription Server { get; }

    /// <summary>The server's current name, which it may change while it runs.</summary>
    public string ServerName
    {
        get
        {
            lock (_gate)
            {
                return _serverName;
            }
        }
    }

    /// <summary>Whether the server is still running.</summary>
    public bool IsValid => _connection.IsServerActive;

    /// <summary>Whether the server has published a frame this client has not received.</summary>
    public bool HasNewFrame => _connection.FrameNumber != _lastFrame;

    /// <summary>Takes the server's current frame.</summary>
    /// <param name="frame">The frame, when the result is <see cref="SyphonReceiveResult.Received"/>.</param>
    /// <returns>What was found.</returns>
    public SyphonReceiveResult TryReceive(out SyphonFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frameOpen)
        {
            throw new InvalidOperationException("A frame is still held; dispose it first.");
        }

        frame = default;
        if (!_connection.IsServerActive)
        {
            return SyphonReceiveResult.ServerRetired;
        }

        IOSurface.IOSurface? surface = _connection.Surface;
        if (surface is null)
        {
            return SyphonReceiveResult.NoFrame;
        }

        long number = _connection.FrameNumber;
        bool isNew = number != _lastFrame;
        _lastFrame = number;
        surface.IncrementUseCount();
        _frameOpen = true;
        frame = new SyphonFrame(this, surface, number, isNew, MonotonicNanoseconds());
        return SyphonReceiveResult.Received;
    }

    /// <summary>
    /// Delivers the server's frames to <paramref name="handler"/> as it publishes them, on a thread of
    /// the client's own, until cancelled or until the server stops. The server's frame messages wake
    /// the client; frames published while the handler runs are delivered as the latest one.
    /// </summary>
    /// <param name="handler">Called with each new frame; the frame is valid until it returns.</param>
    /// <param name="cancellationToken">Stops receiving.</param>
    /// <returns>
    /// Completes when cancelled or when the server stops, or faults with the handler's exception.
    /// </returns>
    public Task RunAsync(SyphonFrameHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running)
        {
            throw new InvalidOperationException("The client is already running.");
        }

        _running = true;
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => Run(handler, completion, cancellationToken))
        {
            IsBackground = true,
            Name = "Syphon client",
        };
        thread.Start();
        return completion.Task;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
        _frames.Dispose();
        lock (_gate)
        {
            while (_pool.TryPop(out IOSurface.IOSurface? surface))
            {
                surface.Dispose();
            }
        }

        _queue?.Dispose();
    }

    internal void EndFrame(IOSurface.IOSurface surface)
    {
        if (_frameOpen)
        {
            _frameOpen = false;
            surface.DecrementUseCount();
        }
    }

    // Copies a frame on the GPU into a surface of the client's own, and waits for the copy, so the
    // lease holds this frame whatever the server renders next.
    internal SyphonFrameLease Retain(IOSurface.IOSurface source, long frameNumber, long observedAt)
    {
        int width = (int)source.Width;
        int height = (int)source.Height;
        IOSurface.IOSurface copy;
        IMTLCommandQueue queue;
        lock (_gate)
        {
            IMTLDevice device =
                _options.Device
                ?? MTLDevice.SystemDefault
                ?? throw new SyphonException("There is no Metal device to copy the frame on.");
            _queue ??=
                device.CreateCommandQueue()
                ?? throw new SyphonException("A Metal command queue could not be created.");
            queue = _queue;
            copy =
                _pool.TryPop(out IOSurface.IOSurface? pooled)
                && (int)pooled.Width == width
                && (int)pooled.Height == height
                    ? pooled
                    : Surfaces.CreateLocal(width, height);
        }

        using (IMTLTexture from = Surfaces.Texture(queue.Device, source))
        using (IMTLTexture to = Surfaces.Texture(queue.Device, copy))
        {
            IMTLCommandBuffer buffer =
                queue.CommandBuffer() ?? throw new SyphonException("No Metal command buffer.");
            IMTLBlitCommandEncoder blit =
                buffer.BlitCommandEncoder ?? throw new SyphonException("No Metal blit encoder.");
            blit.CopyFromTexture(
                from,
                0,
                0,
                new MTLOrigin(0, 0, 0),
                new MTLSize(width, height, 1),
                to,
                0,
                0,
                new MTLOrigin(0, 0, 0)
            );
            blit.EndEncoding();
            buffer.Commit();
            buffer.WaitUntilCompleted();
        }

        return new SyphonFrameLease(this, copy, frameNumber, observedAt);
    }

    internal void Return(IOSurface.IOSurface surface)
    {
        lock (_gate)
        {
            if (!_disposed && _pool.Count < 4)
            {
                _pool.Push(surface);
                return;
            }
        }

        surface.Dispose();
    }

    private void Run(
        SyphonFrameHandler handler,
        TaskCompletionSource completion,
        CancellationToken cancellationToken
    )
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (TryReceive(out SyphonFrame frame) == SyphonReceiveResult.ServerRetired)
                {
                    break;
                }

                using (frame)
                {
                    if (frame.IsNew)
                    {
                        handler(in frame);
                    }
                }

                // Frame messages that arrived meanwhile are one frame now: the latest.
                _frames.Wait(cancellationToken);
                while (_frames.Wait(0, CancellationToken.None)) { }
            }

            completion.TrySetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            // Rethrown through the task: the caller awaits RunAsync and sees the failure there.
            completion.TrySetException(error);
        }
        finally
        {
            _running = false;
        }
    }

    private static long MonotonicNanoseconds() =>
        (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000_000 / Stopwatch.Frequency);
}

/// <summary>
/// A frame from a server: the server's own surface, marked in use until disposed. Syphon has no lock,
/// so the server may render the next frame into the surface while it is held; read it promptly or keep
/// a copy with <see cref="Retain"/>.
/// </summary>
public readonly ref struct SyphonFrame : IDisposable
{
    private readonly SyphonClient? _client;

    internal SyphonFrame(
        SyphonClient client,
        IOSurface.IOSurface surface,
        long frameNumber,
        bool isNew,
        long observedAt
    )
    {
        _client = client;
        Surface = surface;
        FrameNumber = frameNumber;
        IsNew = isNew;
        ObservedAtNanoseconds = observedAt;
    }

    /// <summary>The server's surface, 8-bit BGRA.</summary>
    public IOSurface.IOSurface Surface { get; }

    /// <summary>The frame's width.</summary>
    public int Width => (int)Surface.Width;

    /// <summary>The frame's height.</summary>
    public int Height => (int)Surface.Height;

    /// <summary>
    /// The client's count of the server's frames at this one, advanced for every frame the server
    /// renders; Syphon carries no frame number of its own.
    /// </summary>
    public long FrameNumber { get; }

    /// <summary>Whether the server rendered this frame since the client's previous one.</summary>
    public bool IsNew { get; }

    /// <summary>
    /// When the client took the frame, in nanoseconds of the monotonic clock
    /// (<see cref="System.Diagnostics.Stopwatch"/>). Syphon carries no capture time, so this is when
    /// the frame was observed, not when it was produced.
    /// </summary>
    public long ObservedAtNanoseconds { get; }

    /// <summary>A Metal texture over the frame on <paramref name="device"/>, to read it with Metal.</summary>
    /// <param name="device">The device to read on.</param>
    /// <returns>The texture; dispose it before the frame.</returns>
    public IMTLTexture CreateTexture(IMTLDevice device) => Surfaces.Texture(device, Surface);

    /// <summary>Copies the frame's pixels, tightly packed 8-bit BGRA rows, into memory.</summary>
    /// <param name="destination">At least width x height x 4 bytes.</param>
    /// <returns>The bytes copied.</returns>
    public int CopyPixelsTo(Span<byte> destination) => Surfaces.Read(Surface, destination);

    /// <summary>Keeps the frame past the hold: copies it on the GPU into a surface the client owns.</summary>
    /// <returns>The copy.</returns>
    public SyphonFrameLease Retain() =>
        (_client ?? throw new InvalidOperationException("The frame was not received.")).Retain(
            Surface,
            FrameNumber,
            ObservedAtNanoseconds
        );

    /// <summary>Releases the frame.</summary>
    public void Dispose() => _client?.EndFrame(Surface);
}

/// <summary>A copy of a frame, kept past its hold (<see cref="SyphonFrame.Retain"/>).</summary>
public sealed class SyphonFrameLease : IDisposable
{
    private readonly SyphonClient _client;
    private IOSurface.IOSurface? _surface;

    internal SyphonFrameLease(
        SyphonClient client,
        IOSurface.IOSurface surface,
        long frameNumber,
        long observedAt
    )
    {
        _client = client;
        _surface = surface;
        Width = (int)surface.Width;
        Height = (int)surface.Height;
        FrameNumber = frameNumber;
        ObservedAtNanoseconds = observedAt;
    }

    /// <summary>The copy, 8-bit BGRA, valid until disposed.</summary>
    public IOSurface.IOSurface Surface =>
        _surface ?? throw new ObjectDisposedException(nameof(SyphonFrameLease));

    /// <summary>The frame's width.</summary>
    public int Width { get; }

    /// <summary>The frame's height.</summary>
    public int Height { get; }

    /// <summary>The client's count of the server's frames at this one.</summary>
    public long FrameNumber { get; }

    /// <summary>When the frame was observed, in nanoseconds of the monotonic clock.</summary>
    public long ObservedAtNanoseconds { get; }

    /// <summary>Copies the frame's pixels, tightly packed 8-bit BGRA rows, into memory.</summary>
    /// <param name="destination">At least width x height x 4 bytes.</param>
    /// <returns>The bytes copied.</returns>
    public int CopyPixelsTo(Span<byte> destination) => Surfaces.Read(Surface, destination);

    /// <summary>Returns the copy to the client's pool.</summary>
    public void Dispose()
    {
        IOSurface.IOSurface? surface = Interlocked.Exchange(ref _surface, null);
        if (surface is not null)
        {
            _client.Return(surface);
        }
    }
}
