namespace Syphon.NET.Protocol;

// The names, keys and message types of the Syphon protocol (SyphonPrivate.h, SyphonPrivate.m).
internal static class SyphonProtocol
{
    public const string Identifier = "info.v002.Syphon";

    // Distributed notifications: servers announce, update and retire themselves, and answer an
    // announce request (the directory's discovery) with an announce.
    public const string AnnounceRequest = "info.v002.Syphon.ServerAnnounceRequest";
    public const string Announce = "info.v002.Syphon.ServerAnnounce";
    public const string Retire = "info.v002.Syphon.ServerRetire";
    public const string Update = "info.v002.Syphon.ServerUpdate";

    // Keys of a server description, the notifications' user info.
    public const string UuidKey = "SyphonServerDescriptionUUIDKey";
    public const string NameKey = "SyphonServerDescriptionNameKey";
    public const string AppNameKey = "SyphonServerDescriptionAppNameKey";
    public const string DictionaryVersionKey = "SyphonServerDescriptionDictionaryVersionKey";
    public const string SurfacesKey = "SyphonServerDescriptionSurfacesKey";
    public const string SurfaceTypeKey = "SyphonSurfaceType";
    public const string SurfaceTypeIOSurface = "SyphonSurfaceTypeIOSurface";
    public const uint DictionaryVersion = 0;

    // How long an announce request waits for servers to answer before the directory drops those
    // that did not (kSyphonServerDirectoryAnnounceTimeout).
    public static readonly TimeSpan AnnounceTimeout = TimeSpan.FromSeconds(6);

    // A new identity for a server's or client's message port, as SyphonCreateUUIDString makes one:
    // the identifier, a dot, and an upper-case CFUUID string.
    public static string CreateUuid() =>
        $"{Identifier}.{Guid.NewGuid().ToString("D").ToUpperInvariant()}";
}

// Messages a client sends to a server's port; the payload is the client's port name.
internal enum ServerMessage : uint
{
    AddClientForInfo = 0,
    AddClientForFrames = 1,
    RemoveClientForInfo = 2,
    RemoveClientForFrames = 3,
}

// Messages a server sends to a client's port.
internal enum ClientMessage : uint
{
    // Payload: the server's new name (NSString).
    UpdateServerName = 0,

    // No payload.
    NewFrame = 1,

    // Payload: the IOSurfaceID frames are now in (NSNumber, unsigned int).
    UpdateSurfaceId = 2,

    // No payload.
    RetireServer = 3,
}
