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

## Transform surfaces on the GPU (`SurfaceEffect`)

`SurfaceEffect` is a small general-purpose helper (not part of the Syphon protocol) for running a
Metal fragment shader over one or more input `IOSurface` planes into a BGRA output surface — useful
for colour conversion or channel folding before publishing or after receiving, all zero-copy on the
same shared Metal device. You supply the shader; it provides the full-screen vertex stage, the
stage-in struct `VOut { float4 pos; float2 uv; }` (with `uv` in `[0,1]`), and a linear clamped
sampler `sy_samp`.

```csharp
using Syphon.NET;

using var effect = new SurfaceEffect(
    "fragment float4 invert(VOut in [[stage_in]], texture2d<float> src [[texture(0)]]) {\n" +
    "    float4 c = src.sample(sy_samp, in.uv); return float4(1.0 - c.rgb, c.a);\n" +
    "}\n", "invert");

// Returns a view over an effect-owned surface, valid until the next Render call.
IOSurface output = effect.Render(width, height, [new SurfaceInput(input, MetalPixelFormat.Bgra8Unorm)]);
```

Multiple inputs bind at fragment texture indices `0..n-1`, and an input can view a specific plane of
a planar surface (e.g. an NV12 luma plane as `R8Unorm` and the CbCr plane as `Rg8Unorm`).

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
