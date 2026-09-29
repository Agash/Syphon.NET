# Contributing

Thanks for your interest in Syphon.NET.

## Building

```sh
git clone --recursive https://github.com/Agash/Syphon.NET
cd Syphon.NET
dotnet build Syphon.NET.slnx
dotnet test --solution Syphon.NET.slnx
```

The build treats warnings as errors and targets `net11.0-macos`. You need macOS 15 or later, the .NET 11
SDK with the `macos` workload, and the Xcode the workload asks for. With a newer Xcode than the workload
knows, build with `-p:ValidateXcodeVersion=false`.

## How it is built

The library implements the Syphon protocol in C# on Microsoft's macOS bindings: `Protocol/` holds the
wire format (message types, keys, archived payloads), the message ports, the notification thread and
each side's connection; the public types sit on top. `external/Syphon-Framework` is the upstream
framework, kept as the protocol reference. When a behaviour is in question, the framework's source is
the answer.

## Tests

The tests run in an AppKit host (`tests/Syphon.NET.Tests/Main.cs`) and need a Metal GPU. The interop
tests exchange frames with the Syphon framework in a separate process through
[syphon-python](https://github.com/cansik/syphon-python) and `tests/interop/syphon_peer.py`. They find
Python in `SYPHON_PYTHON`, else `~/syphon-venv/bin/python`, and fail when it is missing:

```sh
python3.12 -m venv ~/syphon-venv
~/syphon-venv/bin/pip install syphon-python numpy pyobjc-framework-Cocoa
```

syphon-python ships wheels for CPython 3.8 to 3.12.

## Native AOT

`samples/Syphon.NET.Smoke` publishes, discovers and receives a frame as a Native AOT app:

```sh
dotnet publish samples/Syphon.NET.Smoke -c Release
```

## Pull requests

Keep changes focused. Make sure the build is clean and the tests pass on a Mac.

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
  on every build. Native AOT is also proven by publishing and running the smoke sample.

## Tests

- Name tests `{Method}_{Scenario}_{ExpectedResult}`.
- Prefer the purpose-built MSTest assertions (`Assert.HasCount`, `Assert.Contains`,
  `Assert.AreSequenceEqual`) over hand-rolled equality checks; the analyzers point you at them.
- No `Thread.Sleep`. Use `TaskCompletionSource`, channels, or a fake clock.
- New behaviour needs a test. Bug fixes need a test that fails before the fix.

## Commits and pull requests

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/):

```
fix(webhooks): reject a signature computed over the decoded body
```

Keep the subject under 50 characters and in the imperative mood. Add a body only when the reason for
the change would not be obvious to the next reader: explain *why*, not *what*.

One logical change per commit. Rebase rather than merge when updating a branch.

## Code of conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). By participating you are
expected to uphold it.

## Reporting security issues

Please do not open a public issue. See [SECURITY.md](SECURITY.md).
