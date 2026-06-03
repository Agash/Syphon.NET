# Syphon.NET

.NET bindings for [Syphon](https://syphon.info), the macOS framework for sharing video frames
between applications in real time. Publish frames for other apps to pick up, or receive frames
another app is sharing, with no pixel copies in between. Works with OBS, Resolume, TouchDesigner,
and other tools that speak Syphon.

## Requirements

- macOS on Apple Silicon or Intel, with a Metal-capable GPU.
- .NET 10 (or .NET 11).

## Install

```sh
dotnet add package Syphon.NET
```

The package bundles the native helper it needs; there is nothing else to install.

## Publish

```csharp
using Syphon.NET;

using var server = new SyphonServer("My Output");

// From CPU pixels (BGRA):
server.PublishPixels(bgraPixels, width: 1920, height: 1080);

// Or from a GPU IOSurface you already hold (zero-copy), e.g. a VideoToolbox CVPixelBuffer:
server.Publish(surface);
```

In OBS, add a **Syphon Client** source and choose "My Output".

## Receive

```csharp
using Syphon.NET;

using var directory = new SyphonServerDirectory();

// An app with a Cocoa run loop (MAUI/AppKit) discovers automatically. A plain console or server
// process pumps the run loop so discovery notifications arrive:
directory.PumpEvents(TimeSpan.FromMilliseconds(200));

using var client = directory.CreateClient(index: 0);

using SyphonFrame? frame = client.TryGetFrame();
if (frame is not null)
{
    int w = frame.Surface.Width, h = frame.Surface.Height;
    byte[] pixels = new byte[w * h * 4];
    frame.CopyTo(pixels);
}
```

Each frame is backed by an `IOSurface` (`frame.Surface.Handle`), so you can read it on the CPU as
shown or hand it straight to VideoToolbox for a zero-copy hardware encode.

## Building from source

```sh
git clone --recursive https://github.com/Agash/Syphon.NET
cd Syphon.NET
bash native/build-native.sh
dotnet build Syphon.NET.slnx
dotnet test
```

The native helper compiles the Syphon framework (a git submodule) and a small shim into one
universal binary; the managed library is plain `net10.0` / `net11.0`.

## License

MIT. See [LICENSE](LICENSE). Bundled Syphon framework code is BSD licensed; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
