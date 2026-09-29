using Syphon.NET.Protocol;

namespace Syphon.NET;

/// <summary>
/// A Syphon server as it describes itself to clients: what a directory lists and what a client
/// connects with. The UUID is its identity; the name can change while it runs.
/// </summary>
public sealed class SyphonServerDescription : IEquatable<SyphonServerDescription>
{
    internal SyphonServerDescription(NSDictionary dictionary)
    {
        Dictionary = dictionary;
        Uuid = Read(dictionary, SyphonProtocol.UuidKey) ?? string.Empty;
        Name = Read(dictionary, SyphonProtocol.NameKey) ?? string.Empty;
        AppName = Read(dictionary, SyphonProtocol.AppNameKey) ?? string.Empty;
    }

    /// <summary>The server's identity, which never changes while it runs.</summary>
    public string Uuid { get; }

    /// <summary>The server's name; often empty, when the application's name says enough.</summary>
    public string Name { get; }

    /// <summary>The name of the application running the server.</summary>
    public string AppName { get; }

    /// <summary>Whether the server shares IOSurfaces, the only kind of surface Syphon has shipped.</summary>
    public bool SharesIOSurfaces =>
        Dictionary[SyphonProtocol.SurfacesKey] is NSArray surfaces
        && Enumerable
            .Range(0, (int)surfaces.Count)
            .Any(i =>
                surfaces.GetItem<NSDictionary>((nuint)i)?[SyphonProtocol.SurfaceTypeKey]
                    is NSString type
                && type == SyphonProtocol.SurfaceTypeIOSurface
            );

    internal NSDictionary Dictionary { get; }

    /// <summary>
    /// Reads a description another process exported with <see cref="ToPropertyList"/>, to connect a
    /// client without a directory.
    /// </summary>
    /// <param name="propertyList">The property list's bytes.</param>
    /// <returns>The description.</returns>
    /// <exception cref="ArgumentException">The bytes are not a server description.</exception>
    public static SyphonServerDescription FromPropertyList(ReadOnlySpan<byte> propertyList)
    {
        using NSData data = NSData.FromArray(propertyList.ToArray());
        NSPropertyListFormat format = NSPropertyListFormat.Binary;
        NSObject? read = NSPropertyListSerialization.PropertyListWithData(
            data,
            ref format,
            out NSError? error
        );
        return
            read is NSDictionary dictionary && Read(dictionary, SyphonProtocol.UuidKey) is not null
            ? new SyphonServerDescription(dictionary)
            : throw new ArgumentException(
                $"The data is not a Syphon server description. {error?.LocalizedDescription}",
                nameof(propertyList)
            );
    }

    /// <summary>
    /// The description as a binary property list, which another process reads with
    /// <see cref="FromPropertyList"/> to connect without a directory.
    /// </summary>
    /// <returns>The property list's bytes.</returns>
    public unsafe byte[] ToPropertyList()
    {
        using NSData data =
            NSPropertyListSerialization.DataWithPropertyList(
                Dictionary,
                NSPropertyListFormat.Binary,
                out NSError? error
            )
            ?? throw new SyphonException(
                $"The description could not be serialized. {error?.LocalizedDescription}"
            );
        return new ReadOnlySpan<byte>((void*)data.Bytes, (int)data.Length).ToArray();
    }

    /// <inheritdoc/>
    public bool Equals(SyphonServerDescription? other) =>
        other is not null && string.Equals(Uuid, other.Uuid, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SyphonServerDescription);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Uuid);

    /// <inheritdoc/>
    public override string ToString() =>
        Name.Length == 0 ? AppName
        : AppName.Length == 0 ? Name
        : $"{AppName}: {Name}";

    // The dictionary a server announces (SyphonServerBase.serverDescription).
    internal static SyphonServerDescription Create(string uuid, string name, string appName)
    {
        using NSMutableDictionary surface = new()
        {
            [SyphonProtocol.SurfaceTypeKey] = new NSString(SyphonProtocol.SurfaceTypeIOSurface),
        };
        NSMutableDictionary dictionary = new()
        {
            [SyphonProtocol.DictionaryVersionKey] = NSNumber.FromUInt32(
                SyphonProtocol.DictionaryVersion
            ),
            [SyphonProtocol.NameKey] = new NSString(name),
            [SyphonProtocol.UuidKey] = new NSString(uuid),
            [SyphonProtocol.AppNameKey] = new NSString(appName),
            [SyphonProtocol.SurfacesKey] = NSArray.FromNSObjects(surface),
        };
        return new SyphonServerDescription(dictionary);
    }

    private static string? Read(NSDictionary dictionary, string key) =>
        dictionary[key] is NSString value ? (string)value : null;
}
