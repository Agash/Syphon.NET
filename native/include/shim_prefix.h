// Forced-include prefix for the Syphon shim build.
//
// The vendored Syphon sources are written for the Xcode framework build, which supplies these
// system umbrella headers (and CoreVideo symbols such as kCVPixelFormatType_32BGRA) through the
// project configuration rather than per-file imports. Force-including this header reproduces that
// environment for the direct clang build in build-native.sh.

// Guarded so this prefix is also safe to force-include into the plain C sources
// (SyphonDispatch.c, SyphonCGL.c, SyphonOpenGLFunctions.c), which must not pull Objective-C
// umbrella headers. Those C files bring their own includes.
#ifdef __OBJC__
#import <Cocoa/Cocoa.h>
#import <CoreVideo/CoreVideo.h>
#import <IOSurface/IOSurface.h>
#import <Metal/Metal.h>
#endif
