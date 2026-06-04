#!/usr/bin/env bash
#
# Build the self-contained native shim dylib (libsyphon_shim.dylib).
#
# The Syphon framework sources are vendored as a git submodule and compiled directly into the
# shim, so the result has no external Syphon.framework dependency. Produces a universal
# (arm64 + x86_64) dylib at native/build/libsyphon_shim.dylib.
#
# macOS only. Requires the Xcode command line tools (clang).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VENDOR="$SCRIPT_DIR/vendor/Syphon-Framework"
BUILD="$SCRIPT_DIR/build"
OUT="$BUILD/libsyphon_shim.dylib"

if [[ ! -d "$VENDOR" ]]; then
  echo "error: Syphon framework submodule missing at $VENDOR" >&2
  echo "run: git submodule update --init --recursive" >&2
  exit 1
fi

mkdir -p "$BUILD"

# The vendored Syphon headers import each other framework-style (#import <Syphon/Foo.h>).
# A symlink makes that resolve to the submodule directory for the direct (non-framework) build.
mkdir -p "$BUILD/headers"
ln -sfn "$VENDOR" "$BUILD/headers/Syphon"

# Syphon's own sources plus our shim. Compiling all of Syphon (including the legacy OpenGL path)
# keeps the source list robust to upstream changes; only the Metal path is exercised at runtime.
SOURCES=()
while IFS= read -r f; do SOURCES+=("$f"); done < <(find "$VENDOR" \( -name '*.m' -o -name '*.c' \))
SOURCES+=("$SCRIPT_DIR/src/syphon_shim.m")

clang \
  -dynamiclib \
  -arch arm64 -arch x86_64 \
  -mmacosx-version-min=11.0 \
  -fobjc-arc \
  -fvisibility=hidden \
  -O2 \
  -Wno-deprecated-declarations \
  -Wno-c23-extensions \
  -include "$SCRIPT_DIR/include/shim_prefix.h" \
  -include "$VENDOR/Syphon_Prefix.pch" \
  -I"$BUILD/headers" \
  -I"$VENDOR" \
  -I"$SCRIPT_DIR/include" \
  -framework Foundation \
  -framework Cocoa \
  -framework OpenGL \
  -framework Metal \
  -framework IOSurface \
  -framework QuartzCore \
  -framework CoreVideo \
  -framework IOKit \
  -install_name "@rpath/libsyphon_shim.dylib" \
  -o "$OUT" \
  "${SOURCES[@]}"

echo "built $OUT"
lipo -info "$OUT"

# Compile Syphon's Metal shaders into default.metallib, placed beside the dylib. The Metal
# server renderer loads this via -[MTLDevice newDefaultLibraryWithBundle:] using the bundle of
# the dylib that defines its class, i.e. this directory. Without it the render pipeline's
# vertex function is nil and Metal asserts at server creation. metallib is arch-independent.
xcrun -sdk macosx metal -O -mmacosx-version-min=11.0 \
  -c "$VENDOR/SyphonMetalShaders.metal" -o "$BUILD/SyphonMetalShaders.air"
xcrun -sdk macosx metallib "$BUILD/SyphonMetalShaders.air" -o "$BUILD/default.metallib"
echo "built $BUILD/default.metallib"
