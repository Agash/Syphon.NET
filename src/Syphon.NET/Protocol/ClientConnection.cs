using System.Runtime.InteropServices;
using ObjCRuntime;

namespace Syphon.NET.Protocol;

// A client's side of the messaging (SyphonClientConnectionManager.m). The client opens a port of its
// own, registers with the server's port for info and frames, and follows the surface the server
// names. A frame is new when the server announces one, which it does once the frame is in the
// surface, or when the surface changes.
//
// A server tells its registered clients it is retiring, but registering is a message the server reads
// later: one that retires first never knows this client and only says so in the retire notification
// every Syphon application sees. The connection follows that notification too, so a client made just
// before its server went away still learns it is gone.
internal sealed partial class ClientConnection : IDisposable
{
    private readonly Lock _gate = new();
    private readonly string _uuid = SyphonProtocol.CreateUuid();
    private readonly string _serverUuid;
    private readonly MessageReceiver _receiver;
    private uint _surfaceId;
    private IOSurface.IOSurface? _surface;
    private long _frame;
    private bool _serverActive = true;
    private readonly IDisposable _retirements;

    public ClientConnection(string serverUuid)
    {
        _serverUuid = serverUuid;
        _retirements = NotificationHub.Subscribe(OnNotification);
        _receiver =
            MessageReceiver.TryCreate(_uuid, OnMessage)
            ?? throw new SyphonException(
                $"The client's message port {_uuid} could not be created."
            );
        using MessageSender? server = MessageSender.TryCreate(serverUuid);
        if (server is null)
        {
            _serverActive = false;
            return;
        }

        server.Send((uint)ServerMessage.AddClientForInfo, new NSString(_uuid));
        server.SendAndFlush((uint)ServerMessage.AddClientForFrames, new NSString(_uuid));
    }

    // Raised on the messaging queue for every frame the server publishes.
    public event Action? NewFrame;

    // Raised on the messaging queue when the server retires.
    public event Action? Retired;

    // Raised on the messaging queue when the server is renamed.
    public event Action<string>? Renamed;

    public bool IsServerActive
    {
        get
        {
            lock (_gate)
            {
                return _serverActive;
            }
        }
    }

    // The current frame number, advanced by each frame the server announces and each new surface; 0
    // before the first surface.
    public long FrameNumber
    {
        get
        {
            lock (_gate)
            {
                return SurfaceHavingLock() is null ? 0 : _frame;
            }
        }
    }

    // The surface frames are in, or null before the server has published.
    public IOSurface.IOSurface? Surface
    {
        get
        {
            lock (_gate)
            {
                return SurfaceHavingLock();
            }
        }
    }

    public void Dispose()
    {
        bool active;
        lock (_gate)
        {
            active = _serverActive;
            _serverActive = false;
            _surface?.Dispose();
            _surface = null;
        }

        if (active)
        {
            using MessageSender? server = MessageSender.TryCreate(_serverUuid);
            if (server is not null)
            {
                server.Send((uint)ServerMessage.RemoveClientForFrames, new NSString(_uuid));
                server.SendAndFlush((uint)ServerMessage.RemoveClientForInfo, new NSString(_uuid));
            }
        }

        _retirements.Dispose();
        _receiver.Dispose();
    }

    private IOSurface.IOSurface? SurfaceHavingLock()
    {
        if (_surface is null && _surfaceId != 0)
        {
            // IOSurfaceLookup returns a +1 reference; the wrapper takes it over.
            nint handle = IOSurfaceLookup(_surfaceId);
            _surface =
                handle == 0
                    ? null
                    : Runtime.GetINativeObject<IOSurface.IOSurface>(handle, owns: true);
        }

        return _surface;
    }

    private void OnMessage(uint type, NSObject? payload)
    {
        switch ((ClientMessage)type)
        {
            case ClientMessage.NewFrame:
                lock (_gate)
                {
                    _frame++;
                }

                NewFrame?.Invoke();
                break;
            case ClientMessage.UpdateSurfaceId when payload is NSNumber id:
                lock (_gate)
                {
                    _surfaceId = id.UInt32Value;
                    _frame++;
                    _surface?.Dispose();
                    _surface = null;
                }

                break;
            case ClientMessage.UpdateServerName when payload is NSString name:
                Renamed?.Invoke(name);
                break;
            case ClientMessage.RetireServer:
                Retire();
                break;
            default:
                break;
        }
    }

    private void OnNotification(string name, NSDictionary? userInfo)
    {
        if (
            name == SyphonProtocol.Retire
            && userInfo is not null
            && new SyphonServerDescription(userInfo).Uuid == _serverUuid
        )
        {
            Retire();
        }
    }

    // Once, whichever of the server's message and the retire notification comes first.
    private void Retire()
    {
        lock (_gate)
        {
            if (!_serverActive)
            {
                return;
            }

            _serverActive = false;
            _surface?.Dispose();
            _surface = null;
        }

        Retired?.Invoke();
    }

    // Not in Microsoft's IOSurface binding: the global lookup by ID, which Syphon's surfaces are
    // created for (kIOSurfaceIsGlobal).
    [LibraryImport("/System/Library/Frameworks/IOSurface.framework/IOSurface")]
    private static partial nint IOSurfaceLookup(uint surfaceId);
}
