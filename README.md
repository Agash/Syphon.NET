# Syphon.NET

[![NuGet](https://img.shields.io/nuget/v/Syphon.NET.svg)](https://www.nuget.org/packages/Syphon.NET)
[![build](https://github.com/Agash/Syphon.NET/actions/workflows/build.yml/badge.svg)](https://github.com/Agash/Syphon.NET/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

[Syphon](https://syphon.info) for .NET 11: share video frames between macOS applications in real time
as IOSurfaces. Frames published here appear in OBS, Resolume, MadMapper, TouchDesigner and every other
Syphon application, and theirs can be received here.

It implements the Syphon protocol: server announcements and the server directory over distributed
notifications, the per-server and per-client message ports, and global IOSurfaces for frames, on
Microsoft's macOS bindings (Foundation, CoreFoundation, IOSurface, Metal). It is tested byte-exact in
both directions against the Syphon framework running in a separate process.

- Native AOT compatible, with no native dependencies.
- Borrowed frames are the server's own surface: receiving costs no copy.
- Zero-copy publishing: render straight into the server's surface.
- Frames and messages travel on threads of their own; only discovery uses the main run loop.

> **Alpha.** Expect breaking changes before 1.0.

## Requirements

- macOS 15 or later, with a Metal GPU.
- .NET 11 with the `macos` workload (the library targets `net11.0-macos`).

## Install

```sh
dotnet add package Syphon.NET
```

## Publish

```csharp
using SyphonServer server = new("My Output");

// Copy a Metal texture into the server's surface on the GPU, as part of your command buffer:
server.PublishTexture(texture, commandBuffer);

// Or render into the server's surface directly, with no copy:
using (SyphonServerFrame frame = server.BeginFrame(1920, 1080))
{
    Render(frame.CreateTexture(device)); // or write frame.Surface on the CPU
    frame.Publish();
}

// Or from CPU memory (BGRA rows):
server.PublishPixels(bgraPixels, width: 1920, height: 1080);
```

Frames are 8-bit BGRA, as every Syphon server's are. The server is announced when it is created and
retired when it is disposed (or the process exits). `HasClients` and `ClientsChanged` tell whether
anyone is watching; setting `Name` renames it for everyone. In OBS, add a **Syphon Client** source and
choose "My Output".

## Find servers

```csharp
using SyphonServerDirectory directory = new();

foreach (SyphonServerDescription s in directory.Servers)
    Console.WriteLine($"{s.Name} ({s.AppName})");

SyphonServerDescription obs = await directory.WaitForServerAsync(s => s.AppName == "OBS", cancellationToken);

await foreach (var servers in directory.WatchAsync(cancellationToken))
    Show(servers); // each time a server starts, stops or is renamed
```

`ServerAnnounced`, `ServerUpdated` and `ServerRetired` report the same changes as events, on the main
thread. A server made with `IsPrivate` stays out of directories; hand its `Description.ToPropertyList()`
to the client, which reads it back with `SyphonServerDescription.FromPropertyList`.

## Console hosts

macOS delivers the notifications Syphon finds servers with only through the main thread's run loop.
An AppKit or MAUI application serves it already. A console or server host hands its main thread to
`SyphonMainLoop`, which runs the host's work on the thread pool meanwhile:

```csharp
return SyphonMainLoop.Run(async () =>
{
    using SyphonServerDirectory directory = new();
    SyphonServerDescription obs = await directory.WaitForServerAsync(s => s.AppName == "OBS");
    // ...
    return 0;
});
```

Without it, a directory sees no servers, and a server is found only by directories that were already
running when it started. Frames and client connections need no run loop.

## Receive

Frames are borrowed: `SyphonFrame` is the server's surface, valid until disposed. Read it
(`CopyPixelsTo`), use it on the GPU (`CreateTexture`), or keep a copy (`Retain`):

```csharp
using SyphonClient client = new(obs);

await client.RunAsync((in SyphonFrame frame) =>
{
    IMTLTexture texture = frame.CreateTexture(device); // the server's surface, no copy
    Console.WriteLine($"#{frame.FrameNumber} {frame.Width}x{frame.Height}");
}, cancellationToken);
```

`RunAsync` delivers each new frame as the server announces it, on a thread of its own, and returns when
the server retires. For a loop of your own, `TryReceive` borrows the current frame:

```csharp
if (client.HasNewFrame && client.TryReceive(out SyphonFrame frame) == SyphonReceiveResult.Received)
{
    using (frame)
    {
        using SyphonFrameLease kept = frame.Retain(); // a copy that outlives the borrow
    }
}
```

A frame is new once the server announces it complete. A server writes every frame into the same
surface, so a frame read while the server writes the next one can already show it, as in every Syphon
client. `frame.Surface` is Microsoft's `IOSurface` binding, ready for VideoToolbox or Core Image.

## Coming from the Syphon framework

| Syphon framework | Syphon.NET |
| --- | --- |
| `SyphonMetalServer initWithName:device:options:` | `new SyphonServer(name, options)` |
| `publishFrameTexture:onCommandBuffer:imageRegion:flipped:` | `PublishTexture` |
| `newFrameImage` on the server's side (rendering into the surface) | `BeginFrame`, `SyphonServerFrame.Publish` |
| `hasClients`, `serverDescription`, `stop` | `HasClients`, `Description`, `Dispose` |
| `SyphonServerDirectory sharedDirectory`, `servers` | `new SyphonServerDirectory()`, `Servers` |
| `SyphonServerAnnounceNotification` and friends | `ServerAnnounced`, `ServerUpdated`, `ServerRetired` |
| `SyphonMetalClient initWithServerDescription:device:options:newFrameHandler:` | `new SyphonClient(description)`, `RunAsync` |
| `newFrameImage`, `hasNewFrame`, `isValid` | `TryReceive`, `HasNewFrame`, `IsValid` |
| `SyphonServerOptionIsPrivate` | `SyphonServerOptions.IsPrivate` |

## Building from source

```sh
git clone --recursive https://github.com/Agash/Syphon.NET
cd Syphon.NET
dotnet build Syphon.NET.slnx
dotnet test --solution Syphon.NET.slnx
```

The interop tests run the Syphon framework through [syphon-python](https://github.com/cansik/syphon-python);
see [CONTRIBUTING.md](CONTRIBUTING.md). The Syphon framework in `external/Syphon-Framework` is the
protocol reference; nothing from it ships.

## License

MIT. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
