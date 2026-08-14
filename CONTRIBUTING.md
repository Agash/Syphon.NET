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

The build treats warnings as errors and targets `net11.0-macos`, using Microsoft's macOS framework
bindings. You need the .NET 11 SDK with the `macos` workload installed.

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

## House rules

- **Warnings are errors.** `TreatWarningsAsErrors` is on. Fix the diagnostic rather than suppressing
  it; a `NoWarn` or `#pragma` needs a comment saying why the rule genuinely does not apply.
- **Nullable reference types are enabled** everywhere. No `!` without a reason.
- **All I/O is async**, with a `CancellationToken` accepted and propagated. No `.Result`,
  `.GetAwaiter().GetResult()`, or `Thread.Sleep`.
- **Public API carries XML documentation.**
- **The package is trim- and AOT-clean.** `IsAotCompatible` is set, so the trim and AOT analyzers run
  on every build. Serialization goes through a source-generated `JsonSerializerContext`, never the
  reflection-based `JsonSerializer` overloads.

## Tests

- Name tests `{Method}_{Scenario}_{ExpectedResult}`.
- Prefer the purpose-built MSTest assertions (`Assert.HasCount`, `Assert.Contains`,
  `Assert.AreSequenceEqual`) over hand-rolled equality checks — the analyzers will point you at them.
- No `Thread.Sleep`. Use `TaskCompletionSource`, channels, or a fake clock.
- New behaviour needs a test. Bug fixes need a test that fails before the fix.

## Commits and pull requests

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/):

```
fix(webhooks): reject a signature computed over the decoded body
```

Keep the subject under 50 characters and in the imperative mood. Add a body only when the reason for
the change would not be obvious to the next reader — explain *why*, not *what*.

One logical change per commit. Rebase rather than merge when updating a branch.

## Code of conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). By participating you are
expected to uphold it.

## Reporting security issues

Please do not open a public issue. See [SECURITY.md](SECURITY.md).
