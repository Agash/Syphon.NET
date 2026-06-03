// syphon_shim.h
//
// Flat C ABI over the Syphon Metal framework. Everything the managed Syphon.NET
// layer needs is expressed here in terms of IOSurface handles, so the managed side
// never touches Objective-C or Metal directly. The shim owns a shared MTLDevice and
// command queue, performs the Metal command-buffer/blit work, manages Objective-C
// memory, and bridges the client's new-frame block to a C callback.
//
// All handles are opaque. Strings are UTF-8. Functions returning int use 0 for success
// and non-zero for failure. IOSurfaceRef values are plain CoreFoundation handles
// (uintptr_t here) that the managed side reads via the IOSurface framework and releases
// with CFRelease.

#ifndef SYPHON_SHIM_H
#define SYPHON_SHIM_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// The shim is built with -fvisibility=hidden so none of the statically linked Syphon symbols
// leak; the C ABI below is the only exported surface.
#define SY_EXPORT __attribute__((visibility("default")))

typedef void* sy_server;
typedef void* sy_client;
typedef void* sy_directory;

// IOSurfaceRef as an integer handle. 0 means "none".
typedef uintptr_t sy_iosurface;

// Invoked (on an arbitrary thread) when a client has a new frame available.
// ctx is the opaque pointer passed to sy_client_create.
typedef void (*sy_new_frame_cb)(void* ctx);

// ---- Global ---------------------------------------------------------------

// Initialise the shared Metal device and command queue. Safe to call repeatedly.
// Returns 0 on success, non-zero if no Metal device is available.
SY_EXPORT int sy_init(void);

// Library/build identifier, for diagnostics.
SY_EXPORT const char* sy_version(void);

// Run the current thread's CFRunLoop for the given duration so distributed-notification handlers
// (server directory discovery) get dispatched. Needed in hosts that do not run a Cocoa run loop.
SY_EXPORT void sy_pump(double seconds);

// ---- Server (publish frames for other apps, e.g. OBS) ---------------------

// Create a server advertised under name (may be NULL for an unnamed server).
SY_EXPORT sy_server sy_server_create(const char* name);

// Stop and release the server.
SY_EXPORT void sy_server_destroy(sy_server server);

// 1 if at least one client is connected, else 0.
SY_EXPORT int sy_server_has_clients(sy_server server);

// Publish an externally owned IOSurface directly (zero-copy path for GPU producers,
// e.g. a VideoToolbox CVPixelBuffer's surface). The shim wraps it in a transient
// Metal texture and publishes on its own command buffer. flipped is 0 or 1.
// Returns 0 on success.
SY_EXPORT int sy_server_publish_surface(sy_server server, sy_iosurface surface, int flipped);

// CPU-producer path. Ensure the server owns a writable IOSurface of the given size and
// pixel format (recreated when the dimensions or format change) and return it. Lock and
// write pixels into it on the managed side, then call sy_server_publish_current.
// pixel_format is an IOSurface FourCC, e.g. 'BGRA' (0x42475241). Returns 0 (none) on failure.
SY_EXPORT sy_iosurface sy_server_acquire_surface(sy_server server,
                                                 uint32_t width,
                                                 uint32_t height,
                                                 uint32_t pixel_format);

// Publish the surface previously returned by sy_server_acquire_surface. flipped is 0 or 1.
// Returns 0 on success.
SY_EXPORT int sy_server_publish_current(sy_server server, int flipped);

// ---- Directory (discover servers published by other apps) -----------------

SY_EXPORT sy_directory sy_directory_create(void);
SY_EXPORT void sy_directory_destroy(sy_directory dir);

// Number of servers currently known.
SY_EXPORT int sy_directory_count(sy_directory dir);

// Copy the UTF-8 description fields of server at index into the caller's buffers
// (each at least buf_len bytes). Any of uuid/app_name/name may be NULL to skip.
// Returns 0 on success, non-zero if the index is out of range.
SY_EXPORT int sy_directory_get(sy_directory dir, int index,
                               char* uuid, char* app_name, char* name, int buf_len);

// ---- Client (receive frames from a discovered server) ---------------------

// Create a client for the server at the given directory index. cb (may be NULL) is
// invoked when a new frame is available; ctx is passed back to it. Poll frames with
// sy_client_copy_new_frame. Returns NULL on failure (e.g. stale index).
SY_EXPORT sy_client sy_client_create(sy_directory dir, int index, sy_new_frame_cb cb, void* ctx);

// Create a client connected directly to the given server via its serverDescription, bypassing
// the distributed-notification directory. Works without a running CFRunLoop (useful for
// loopback/self-preview and for hosts that do not pump a Cocoa run loop).
SY_EXPORT sy_client sy_client_create_for_server(sy_server server, sy_new_frame_cb cb, void* ctx);

// Serialize the server's description (a property list) into buf. Returns the number of bytes
// needed; pass buf=NULL/buf_len=0 to query the size first. The bytes can be handed to another
// process, which connects with sy_client_create_from_description - no directory required.
SY_EXPORT int sy_server_copy_description(sy_server server, char* buf, int buf_len);

// Create a client from a server description previously obtained via sy_server_copy_description.
SY_EXPORT sy_client sy_client_create_from_description(const char* desc_bytes, int desc_len, sy_new_frame_cb cb, void* ctx);

SY_EXPORT void sy_client_destroy(sy_client client);

// 1 while the client is connected to a live server, else 0.
SY_EXPORT int sy_client_is_valid(sy_client client);

// Return the backing IOSurface of the latest frame, retained (caller releases with
// CFRelease via the managed IOSurface wrapper), or 0 if no new frame is available.
SY_EXPORT sy_iosurface sy_client_copy_new_frame(sy_client client);

#ifdef __cplusplus
}
#endif

#endif // SYPHON_SHIM_H
