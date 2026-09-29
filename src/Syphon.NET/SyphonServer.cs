using System.Diagnostics;
using Metal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Syphon.NET.Protocol;

namespace Syphon.NET;

/// <summary>How a server presents itself.</summary>
public sealed record SyphonServerOptions
{
    /// <summary>
    /// Keep the server out of directories: clients reach it only through a description the
    /// application hands them (<see cref="SyphonServerDescription.ToPropertyList"/>).
    /// </summary>
    public bool IsPrivate { get; init; }

    /// <summary>Where the server logs its lifecycle and failures.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }
}

/// <summary>
/// Shares frames with other applications as IOSurfaces: a Syphon server. OBS, Resolume, MadMapper
/// and other Syphon applications see it in their directory by its name and application.
/// </summary>
/// <remarks>
/// <para>
/// Frames are 8-bit BGRA, as every Syphon server's are. The server owns one surface and renders each
/// frame into it; a client reads the same surface, so publishing costs no copy between processes.
/// </para>
/// <para>
/// Directories started after the server find it by asking servers to announce themselves, which it
/// hears through the main thread's run loop: an AppKit or MAUI application serves it, a console host
/// serves it with <see cref="SyphonMainLoop"/>.
/// </para>
/// </remarks>
public sealed partial class SyphonServer : IDisposable
{
    private static readonly Lock s_liveGate = new();
    private static readonly HashSet<SyphonServer> s_live = [];

    private readonly ILogger<SyphonServer> _logger;
    private readonly Lock _gate = new();
    private long _frames;
    private readonly ServerConnection _connection;
    private readonly IDisposable? _discovery;
    private readonly NSObject? _activity;
    private readonly bool _broadcasts;
    private IOSurface.IOSurface? _surface;
    private IMTLTexture? _surfaceTexture;
    private bool _surfaceChanged;
    private string _name;
    private bool _frameOpen;
    private bool _disposed;

    static SyphonServer() =>
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => RetireRemaining();

    /// <summary>Starts a server.</summary>
    /// <param name="name">The name clients see; empty when the application's name says enough.</param>
    /// <param name="options">How the server presents itself.</param>
    public SyphonServer(string? name = null, SyphonServerOptions? options = null)
    {
        _logger = (
            options?.LoggerFactory ?? NullLoggerFactory.Instance
        ).CreateLogger<SyphonServer>();
        _name = name ?? string.Empty;
        Uuid = SyphonProtocol.CreateUuid();
        _broadcasts = options?.IsPrivate != true;
        _connection = new ServerConnection(Uuid);
        _connection.ClientsChanged += hasClients =>
        {
            LogClientsChanged(Name, hasClients);
            ClientsChanged?.Invoke(this, hasClients);
        };

        // The SDK keeps a serving process out of App Nap and automatic termination while it serves.
        _activity = NSProcessInfo.ProcessInfo.BeginActivity(
            NSActivityOptions.AutomaticTerminationDisabled | NSActivityOptions.Background,
            Uuid
        );

        if (_broadcasts)
        {
            lock (s_liveGate)
            {
                _ = s_live.Add(this);
            }

            _discovery = NotificationHub.Subscribe(OnNotification);
            Broadcast(SyphonProtocol.Announce);
        }

        LogStarted(_name, Uuid, !_broadcasts);
    }

    /// <summary>
    /// Raised when the first client connects (<see langword="true"/>) and when the last one leaves
    /// (<see langword="false"/>), on a thread of Syphon.NET's.
    /// </summary>
    public event EventHandler<bool>? ClientsChanged;

    /// <summary>The name clients see. Renaming tells clients and directories.</summary>
    public string Name
    {
        get
        {
            lock (_gate)
            {
                return _name;
            }
        }
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_gate)
            {
                _name = value ?? string.Empty;
            }

            LogRenamed(Name, Uuid);
            _connection.SetName(Name);
            if (_broadcasts)
            {
                Broadcast(SyphonProtocol.Update);
            }
        }
    }

    /// <summary>The server's description, for directories and for connecting a client directly.</summary>
    public SyphonServerDescription Description =>
        SyphonServerDescription.Create(Uuid, Name, ApplicationName);

    /// <summary>Whether any client is connected.</summary>
    public bool HasClients => _connection.HasClients;

    /// <summary>
    /// The surface frames are rendered into (8-bit BGRA), or null before the first frame. It changes
    /// when the frame size does.
    /// </summary>
    public IOSurface.IOSurface? Surface
    {
        get
        {
            lock (_gate)
            {
                return _surface;
            }
        }
    }

    internal string Uuid { get; }

    private static string ApplicationName =>
        NSRunningApplication.CurrentApplication.LocalizedName
        ?? Process.GetCurrentProcess().ProcessName;

    /// <summary>
    /// Takes the server's surface for the application to render the next frame into, with no copy;
    /// <see cref="SyphonServerFrame.Publish"/> publishes it. Syphon has no lock: clients may read the
    /// surface while it is being written, so render in one GPU submission and publish when it completes.
    /// </summary>
    /// <param name="width">The frame's width; the surface is recreated when it changes.</param>
    /// <param name="height">The frame's height.</param>
    /// <returns>The open frame.</returns>
    public SyphonServerFrame BeginFrame(int width, int height)
    {
        ThrowIfUnusable();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        IOSurface.IOSurface surface = EnsureSurface(width, height);
        _frameOpen = true;
        return new SyphonServerFrame(this, surface);
    }

    /// <summary>
    /// Publishes a copy of a Metal texture as the next frame: a GPU copy into the server's surface,
    /// encoded into <paramref name="commandBuffer"/>, published when the command buffer completes.
    /// </summary>
    /// <param name="texture">The frame, 8-bit BGRA.</param>
    /// <param name="commandBuffer">
    /// A command buffer on the texture's device, not yet committed; the application commits it.
    /// </param>
    /// <param name="region">
    /// The part of the texture that is the frame, origin at the top left; the whole texture when null.
    /// </param>
    /// <param name="flipped">
    /// Whether the texture's rows are bottom first, as an OpenGL-style renderer leaves them; the frame
    /// is then flipped on the way, so clients see it upright.
    /// </param>
    public void PublishTexture(
        IMTLTexture texture,
        IMTLCommandBuffer commandBuffer,
        MTLRegion? region = null,
        bool flipped = false
    )
    {
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(commandBuffer);
        ThrowIfUnusable();
        if (
            texture.PixelFormat is not (MTLPixelFormat.BGRA8Unorm or MTLPixelFormat.BGRA8Unorm_sRGB)
        )
        {
            throw new ArgumentException(
                $"Syphon frames are 8-bit BGRA; the texture is {texture.PixelFormat}.",
                nameof(texture)
            );
        }

        MTLRegion frame =
            region
            ?? new MTLRegion(
                new MTLOrigin(0, 0, 0),
                new MTLSize((nint)texture.Width, (nint)texture.Height, 1)
            );
        if (
            frame.Origin.X < 0
            || frame.Origin.Y < 0
            || frame.Size.Width <= 0
            || frame.Size.Height <= 0
            || (nuint)(frame.Origin.X + frame.Size.Width) > texture.Width
            || (nuint)(frame.Origin.Y + frame.Size.Height) > texture.Height
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(region),
                "The region is not inside the texture."
            );
        }

        int width = (int)frame.Size.Width;
        int height = (int)frame.Size.Height;
        IMTLTexture target;
        lock (_gate)
        {
            IOSurface.IOSurface surface = EnsureSurface(width, height);
            if (_surfaceTexture is null || _surfaceTexture.Device.Handle != texture.Device.Handle)
            {
                _surfaceTexture?.Dispose();
                _surfaceTexture = Surfaces.Texture(texture.Device, surface);
            }

            target = _surfaceTexture;
        }

        if (flipped)
        {
            FlipCopy.Encode(commandBuffer, texture, frame, target);
        }
        else
        {
            IMTLBlitCommandEncoder blit =
                commandBuffer.BlitCommandEncoder
                ?? throw new SyphonException("The command buffer gave no blit encoder.");
            blit.CopyFromTexture(
                texture,
                0,
                0,
                frame.Origin,
                frame.Size,
                target,
                0,
                0,
                new MTLOrigin(0, 0, 0)
            );
            blit.EndEncoding();
        }

        commandBuffer.AddCompletedHandler(completed =>
        {
            if (completed.Status == MTLCommandBufferStatus.Error)
            {
                // The copy did not happen; clients keep the previous frame.
                LogCopyFailed(Name, completed.Error?.LocalizedDescription);
                return;
            }

            if (!_disposed)
            {
                Publish();
            }
        });
    }

    /// <summary>Publishes pixels from memory as the next frame, copied into the server's surface.</summary>
    /// <param name="pixels">The frame, 8-bit BGRA rows.</param>
    /// <param name="width">The frame's width.</param>
    /// <param name="height">The frame's height.</param>
    /// <param name="stride">Bytes per row in <paramref name="pixels"/>; 0 for tightly packed.</param>
    public void PublishPixels(ReadOnlySpan<byte> pixels, int width, int height, int stride = 0)
    {
        ThrowIfUnusable();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int rowBytes = width * 4;
        stride = stride == 0 ? rowBytes : stride;
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, rowBytes);
        if (pixels.Length < (stride * (height - 1)) + rowBytes)
        {
            throw new ArgumentException(
                "The pixels are fewer than the frame's size.",
                nameof(pixels)
            );
        }

        IOSurface.IOSurface surface = EnsureSurface(width, height);
        Surfaces.Write(surface, pixels, width, height, stride);
        Publish();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _connection.Dispose();
        if (_broadcasts)
        {
            _discovery?.Dispose();
            Broadcast(SyphonProtocol.Retire);
            lock (s_liveGate)
            {
                _ = s_live.Remove(this);
            }
        }

        if (_activity is not null)
        {
            NSProcessInfo.ProcessInfo.EndActivity(_activity);
        }

        _surfaceTexture?.Dispose();
        _surface?.Dispose();
        long frames = Interlocked.Read(ref _frames);
        LogRetired(Name, Uuid, frames);
    }

    internal void EndFrame(bool publish)
    {
        if (!_frameOpen)
        {
            return;
        }

        _frameOpen = false;
        if (publish)
        {
            Publish();
        }
    }

    // Announces a new surface to clients first, then the frame (SyphonServerBase.publish).
    private void Publish()
    {
        uint? announce = null;
        lock (_gate)
        {
            if (_surfaceChanged && _surface is not null)
            {
                announce = _surface.SurfaceId;
                _surfaceChanged = false;
            }
        }

        if (announce is uint surfaceId)
        {
            LogSurface(Name, surfaceId);
            _connection.SetSurface(surfaceId);
        }

        _connection.PublishNewFrame();
        _ = Interlocked.Increment(ref _frames);
    }

    private IOSurface.IOSurface EnsureSurface(int width, int height)
    {
        lock (_gate)
        {
            if (
                _surface is not null
                && (int)_surface.Width == width
                && (int)_surface.Height == height
            )
            {
                return _surface;
            }

            _surfaceTexture?.Dispose();
            _surfaceTexture = null;
            _surface?.Dispose();
            _surface = Surfaces.CreateGlobal(width, height);
            _surfaceChanged = true;
            return _surface;
        }
    }

    private void OnNotification(string name, NSDictionary? _)
    {
        // Any application's announce request, a directory looking for servers, is answered.
        if (name == SyphonProtocol.AnnounceRequest && !_disposed)
        {
            try
            {
                Broadcast(SyphonProtocol.Announce);
            }
            catch (Exception error)
            {
                // Not rethrown: this runs on the main run loop, which must keep serving the rest of
                // the process. The next announce request tries again.
                LogAnnounceFailed(error, Name);
            }
        }
    }

    private void Broadcast(string notification) =>
        NotificationHub.Post(notification, Uuid, Description.Dictionary);

    // A server not disposed when the process ends still tells directories it is gone, as the SDK's
    // library destructor does.
    private static void RetireRemaining()
    {
        SyphonServer[] remaining;
        lock (s_liveGate)
        {
            remaining = [.. s_live];
        }

        foreach (SyphonServer server in remaining)
        {
            using NSDictionary description = NSDictionary.FromObjectAndKey(
                new NSString(server.Uuid),
                new NSString(SyphonProtocol.UuidKey)
            );
            NotificationHub.Post(SyphonProtocol.Retire, SyphonProtocol.UuidKey, description);
        }
    }

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_frameOpen)
        {
            throw new InvalidOperationException("A frame is open; publish or dispose it first.");
        }
    }

    [LoggerMessage(
        EventId = 50,
        Level = LogLevel.Information,
        Message = "Syphon server \"{Name}\" started ({Uuid}, private: {IsPrivate})"
    )]
    private partial void LogStarted(string name, string uuid, bool isPrivate);

    [LoggerMessage(
        EventId = 51,
        Level = LogLevel.Debug,
        Message = "Syphon server \"{Name}\" publishes in surface {SurfaceId}"
    )]
    private partial void LogSurface(string name, uint surfaceId);

    [LoggerMessage(
        EventId = 52,
        Level = LogLevel.Debug,
        Message = "Syphon server \"{Name}\" has clients: {HasClients}"
    )]
    private partial void LogClientsChanged(string name, bool hasClients);

    [LoggerMessage(
        EventId = 53,
        Level = LogLevel.Information,
        Message = "Syphon server {Uuid} renamed to \"{Name}\""
    )]
    private partial void LogRenamed(string name, string uuid);

    [LoggerMessage(
        EventId = 54,
        Level = LogLevel.Information,
        Message = "Syphon server \"{Name}\" retired after {Frames} frames ({Uuid})"
    )]
    private partial void LogRetired(string name, string uuid, long frames);

    [LoggerMessage(
        EventId = 55,
        Level = LogLevel.Error,
        Message = "Syphon server \"{Name}\" did not publish a frame: its GPU copy failed ({Reason})"
    )]
    private partial void LogCopyFailed(string name, string? reason);

    [LoggerMessage(
        EventId = 56,
        Level = LogLevel.Error,
        Message = "Syphon server \"{Name}\" could not answer an announce request"
    )]
    private partial void LogAnnounceFailed(Exception error, string name);
}

/// <summary>
/// The server's surface, taken to render the next frame into (<see cref="SyphonServer.BeginFrame"/>).
/// </summary>
public readonly ref struct SyphonServerFrame : IDisposable
{
    private readonly SyphonServer? _server;

    internal SyphonServerFrame(SyphonServer server, IOSurface.IOSurface surface)
    {
        _server = server;
        Surface = surface;
    }

    /// <summary>The surface to render into, 8-bit BGRA, at the frame's size.</summary>
    public IOSurface.IOSurface Surface { get; }

    /// <summary>The frame's width.</summary>
    public int Width => (int)Surface.Width;

    /// <summary>The frame's height.</summary>
    public int Height => (int)Surface.Height;

    /// <summary>
    /// A Metal texture over the surface on <paramref name="device"/>, to render the frame with Metal.
    /// </summary>
    /// <param name="device">The device to render on.</param>
    /// <returns>The texture; dispose it when the frame is rendered.</returns>
    public IMTLTexture CreateTexture(IMTLDevice device) => Surfaces.Texture(device, Surface);

    /// <summary>Publishes what was rendered as the next frame.</summary>
    public void Publish() =>
        (_server ?? throw new InvalidOperationException("The frame was not opened.")).EndFrame(
            publish: true
        );

    /// <summary>Ends the frame; unless it was published, clients are not told of it.</summary>
    public void Dispose() => _server?.EndFrame(publish: false);
}
