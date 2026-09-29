# Third-party notices

The Syphon.NET package contains only Syphon.NET's own code. The repository also carries the following
for development; none of it ships in the package.

## Syphon framework

- Project: https://github.com/Syphon/Syphon-Framework
- License: Simplified BSD (2-clause)
- Location: `external/Syphon-Framework` (git submodule)

The protocol reference Syphon.NET implements. Its license text is the submodule's `License.txt`.

## syphon-python

- Project: https://github.com/cansik/syphon-python
- License: MIT

Installed by developers and CI (not vendored) to run the Syphon framework as the peer of the interop
tests in `tests/interop/syphon_peer.py`.
