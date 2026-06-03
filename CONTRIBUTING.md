# Contributing

Thanks for your interest in Syphon.NET.

## Building

```sh
git clone --recursive https://github.com/Agash/Syphon.NET
cd Syphon.NET
bash native/build-native.sh
dotnet build Syphon.NET.slnx
dotnet test --filter "TestCategory!=RequiresMetal"
```

The build treats warnings as errors and targets .NET 10 (and .NET 11 preview). If you do not
have the .NET 11 preview SDK, the `net11.0` target framework will not build; install it or
build the `net10.0` target only.

## Native helper

The Objective-C and Metal work lives in a small native shim under `native/`, which statically
links the Syphon framework (a git submodule). Its flat C ABI is declared in
`native/include/syphon_shim.h`; the managed side P/Invokes it. If you change the native
surface, update the header, the implementation, the managed `SyphonNative` declarations, and
rebuild with `native/build-native.sh`. The native helper only builds on macOS.

## Tests

Value-type tests run anywhere. Tests tagged `RequiresMetal` exercise the native helper and a
real Metal device; they report Inconclusive when neither is available.

## Pull requests

Keep changes focused. Make sure the build is clean and the non-Metal tests pass.

## License

By contributing you agree that your contributions are licensed under the MIT License.
