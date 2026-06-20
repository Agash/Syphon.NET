// syphon_shim.m
//
// Implementation of the flat C ABI declared in syphon_shim.h, over the Syphon Metal
// framework. Compiled with ARC. Built on macOS only; see native/build-native.sh.

#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#import <IOSurface/IOSurface.h>
#import <CoreFoundation/CoreFoundation.h>

#import "SyphonServerBase.h"
#import "SyphonSubclassing.h"
#import "SyphonServerConnectionManager.h"
#import "SyphonMetalClient.h"
#import "SyphonServerDirectory.h"

#include "syphon_shim.h"

// Server-description dictionary keys (SyphonServerDescriptionUUIDKey, NameKey, AppNameKey)
// are declared by SyphonServerDirectory.h.

// ---- Shared Metal objects -------------------------------------------------

// The server announces IOSurfaces directly (zero-copy, no GPU pass), so only the client needs a
// Metal device - SyphonMetalClient wraps the received IOSurface as an MTLTexture.
static id<MTLDevice> g_device = nil;

int sy_init(void)
{
    if (g_device != nil) { return 0; }
    g_device = MTLCreateSystemDefaultDevice();
    return g_device != nil ? 0 : 1;
}

const char* sy_version(void)
{
    return "Syphon.NET native shim 1.0";
}

void sy_pump(double seconds)
{
    // Run the current thread's CFRunLoop so NSDistributedNotificationCenter handlers (the server
    // directory's announce/retire/update) get dispatched. Hosts without a Cocoa run loop (plain
    // console/server apps) must call this for discovery to make progress.
    CFAbsoluteTime end = CFAbsoluteTimeGetCurrent() + seconds;
    do
    {
        CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.02, true);
    }
    while (CFAbsoluteTimeGetCurrent() < end);
}

void sy_pump_once(void)
{
    // Drain all immediately-pending sources without blocking (0 timeout). Unlike sy_pump, this never
    // waits when the run loop is idle, so it is safe to call once per published frame.
    while (CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0, true) == kCFRunLoopRunHandledSource)
    {
        // keep draining until nothing is left
    }
}


// ---- Server ---------------------------------------------------------------

// The server uses the renderer-free SyphonServerBase: external surfaces are announced directly to
// clients by their IOSurfaceID (true zero-copy, no per-frame GPU pass), and the CPU-producer path
// draws into a server-owned BGRA surface obtained from the base. No Metal device, command queue or
// renderer (and therefore no Metal shader library) is needed on the publish side.
typedef struct sy_server_t {
    SyphonServerBase* server;
    IOSurfaceRef      owned;      // server-owned writable surface (CPU producer path), +1 retained
    IOSurfaceRef      announced;  // externally-announced surface we hold a use count on
} sy_server_t;

// Reach SyphonServerBase's connection manager (a private ivar) so we can announce an arbitrary
// IOSurface by ID without copying it into a server-owned surface. KVC resolves the `connectionManager`
// key to the `_connectionManager` ivar (accessInstanceVariablesDirectly is YES by default). The
// framework is a pinned submodule, so the ivar name is stable.
static SyphonServerConnectionManager* connection_manager(sy_server_t* s)
{
    return (SyphonServerConnectionManager*)[s->server valueForKey:@"connectionManager"];
}

sy_server sy_server_create(const char* name)
{
    sy_server_t* s = calloc(1, sizeof(sy_server_t));
    NSString* nsName = name != NULL ? [NSString stringWithUTF8String:name] : nil;
    s->server = [[SyphonServerBase alloc] initWithName:nsName options:nil];
    if (s->server == nil) { free(s); return NULL; }
    return s;
}

void sy_server_destroy(sy_server server)
{
    if (server == NULL) { return; }
    sy_server_t* s = (sy_server_t*)server;
    if (s->announced) { IOSurfaceDecrementUseCount(s->announced); s->announced = NULL; }
    if (s->owned) { CFRelease(s->owned); s->owned = NULL; }
    [s->server stop];
    s->server = nil;
    free(s);
}

int sy_server_has_clients(sy_server server)
{
    if (server == NULL) { return 0; }
    return ((sy_server_t*)server)->server.hasClients ? 1 : 0;
}

int sy_server_publish_surface(sy_server server, sy_iosurface surface, int flipped)
{
    // Zero-copy: announce the surface by ID. There is no GPU pass, so `flipped` cannot be applied
    // here - the surface is presented as-is (callers pass already-upright surfaces).
    (void)flipped;
    if (server == NULL || surface == 0) { return 1; }
    sy_server_t* s = (sy_server_t*)server;
    IOSurfaceRef surf = (IOSurfaceRef)surface;

    SyphonServerConnectionManager* cm = connection_manager(s);
    if (cm == nil) { return 1; }

    // Hold exactly one use count on the live surface so it stays valid while clients read it,
    // releasing the previously-announced one.
    if (s->announced != surf)
    {
        IOSurfaceIncrementUseCount(surf);
        if (s->announced) { IOSurfaceDecrementUseCount(s->announced); }
        s->announced = surf;
    }

    [cm setSurfaceID:IOSurfaceGetID(surf)];
    [cm publishNewFrame];
    return 0;
}

sy_iosurface sy_server_acquire_surface(sy_server server, uint32_t width, uint32_t height, uint32_t pixel_format)
{
    // SyphonServerBase surfaces are always BGRA8; the format argument is accepted for ABI symmetry
    // with the GPU path but ignored.
    (void)pixel_format;
    if (server == NULL || width == 0 || height == 0) { return 0; }
    sy_server_t* s = (sy_server_t*)server;

    // newSurfaceForWidth:height: returns a +1-retained surface (reused when the size is unchanged);
    // release our previous retain and keep the new one until the next acquire or destroy.
    IOSurfaceRef surf = [s->server newSurfaceForWidth:width height:height options:nil];
    if (surf == NULL) { return 0; }
    if (s->owned) { CFRelease(s->owned); }
    s->owned = surf;
    return (sy_iosurface)surf;
}

int sy_server_publish_current(sy_server server, int flipped)
{
    // The CPU producer wrote into the server-owned surface; `flipped` is not applied (no GPU pass).
    (void)flipped;
    if (server == NULL) { return 1; }
    sy_server_t* s = (sy_server_t*)server;
    if (s->owned == NULL) { return 1; }
    [s->server publish];
    return 0;
}

// ---- Directory ------------------------------------------------------------

typedef struct sy_directory_t {
    SyphonServerDirectory* directory;
} sy_directory_t;

sy_directory sy_directory_create(void)
{
    sy_directory_t* d = calloc(1, sizeof(sy_directory_t));
    d->directory = [SyphonServerDirectory sharedDirectory];
    return d;
}

void sy_directory_destroy(sy_directory dir)
{
    if (dir == NULL) { return; }
    sy_directory_t* d = (sy_directory_t*)dir;
    d->directory = nil; // shared singleton; not owned
    free(d);
}

int sy_directory_count(sy_directory dir)
{
    if (dir == NULL) { return 0; }
    return (int)[[((sy_directory_t*)dir)->directory servers] count];
}

static void copy_string(NSString* value, char* buf, int buf_len)
{
    if (buf == NULL || buf_len <= 0) { return; }
    const char* utf8 = value != nil ? [value UTF8String] : "";
    strlcpy(buf, utf8 != NULL ? utf8 : "", (size_t)buf_len);
}

int sy_directory_get(sy_directory dir, int index, char* uuid, char* app_name, char* name, int buf_len)
{
    if (dir == NULL) { return 1; }
    NSArray* servers = [((sy_directory_t*)dir)->directory servers];
    if (index < 0 || index >= (int)servers.count) { return 1; }
    NSDictionary* desc = servers[index];
    copy_string(desc[SyphonServerDescriptionUUIDKey], uuid, buf_len);
    copy_string(desc[SyphonServerDescriptionAppNameKey], app_name, buf_len);
    copy_string(desc[SyphonServerDescriptionNameKey], name, buf_len);
    return 0;
}

// ---- Client ---------------------------------------------------------------

typedef struct sy_client_t {
    SyphonMetalClient* client;
} sy_client_t;

sy_client sy_client_create(sy_directory dir, int index, sy_new_frame_cb cb, void* ctx)
{
    if (dir == NULL) { return NULL; }
    if (sy_init() != 0) { return NULL; }

    NSArray* servers = [((sy_directory_t*)dir)->directory servers];
    if (index < 0 || index >= (int)servers.count) { return NULL; }
    NSDictionary* desc = servers[index];

    sy_client_t* c = calloc(1, sizeof(sy_client_t));
    void (^handler)(SyphonMetalClient*) = nil;
    if (cb != NULL)
    {
        handler = ^(SyphonMetalClient* _Nonnull client) {
            (void)client;
            cb(ctx);
        };
    }
    c->client = [[SyphonMetalClient alloc] initWithServerDescription:desc
                                                             device:g_device
                                                            options:nil
                                                    newFrameHandler:handler];
    return c;
}

sy_client sy_client_create_for_server(sy_server server, sy_new_frame_cb cb, void* ctx)
{
    if (server == NULL) { return NULL; }
    if (sy_init() != 0) { return NULL; }

    sy_server_t* s = (sy_server_t*)server;
    NSDictionary* desc = [s->server serverDescription];
    if (desc == nil) { return NULL; }

    sy_client_t* c = calloc(1, sizeof(sy_client_t));
    void (^handler)(SyphonMetalClient*) = nil;
    if (cb != NULL)
    {
        handler = ^(SyphonMetalClient* _Nonnull client) {
            (void)client;
            cb(ctx);
        };
    }
    c->client = [[SyphonMetalClient alloc] initWithServerDescription:desc
                                                             device:g_device
                                                            options:nil
                                                    newFrameHandler:handler];
    return c;
}

int sy_server_copy_description(sy_server server, char* buf, int buf_len)
{
    if (server == NULL) { return -1; }
    sy_server_t* s = (sy_server_t*)server;
    NSDictionary* desc = [s->server serverDescription];
    if (desc == nil) { return -1; }

    NSError* error = nil;
    NSData* data = [NSPropertyListSerialization dataWithPropertyList:desc
                                                             format:NSPropertyListBinaryFormat_v1_0
                                                            options:0
                                                              error:&error];
    if (data == nil) { return -1; }

    int needed = (int)data.length;
    if (buf != NULL && buf_len >= needed)
    {
        memcpy(buf, data.bytes, (size_t)needed);
    }
    return needed;
}

sy_client sy_client_create_from_description(const char* desc_bytes, int desc_len, sy_new_frame_cb cb, void* ctx)
{
    if (desc_bytes == NULL || desc_len <= 0) { return NULL; }
    if (sy_init() != 0) { return NULL; }

    NSData* data = [NSData dataWithBytes:desc_bytes length:(NSUInteger)desc_len];
    NSError* error = nil;
    id plist = [NSPropertyListSerialization propertyListWithData:data
                                                        options:NSPropertyListImmutable
                                                         format:NULL
                                                          error:&error];
    if (![plist isKindOfClass:[NSDictionary class]]) { return NULL; }

    sy_client_t* c = calloc(1, sizeof(sy_client_t));
    void (^handler)(SyphonMetalClient*) = nil;
    if (cb != NULL)
    {
        handler = ^(SyphonMetalClient* _Nonnull client) {
            (void)client;
            cb(ctx);
        };
    }
    c->client = [[SyphonMetalClient alloc] initWithServerDescription:(NSDictionary*)plist
                                                             device:g_device
                                                            options:nil
                                                    newFrameHandler:handler];
    return c;
}

void sy_client_destroy(sy_client client)
{
    if (client == NULL) { return; }
    sy_client_t* c = (sy_client_t*)client;
    [c->client stop];
    c->client = nil;
    free(c);
}

int sy_client_is_valid(sy_client client)
{
    if (client == NULL) { return 0; }
    return ((sy_client_t*)client)->client.isValid ? 1 : 0;
}

sy_iosurface sy_client_copy_new_frame(sy_client client)
{
    if (client == NULL) { return 0; }
    sy_client_t* c = (sy_client_t*)client;
    id<MTLTexture> tex = [c->client newFrameImage];
    if (tex == nil) { return 0; }
    IOSurfaceRef surf = tex.iosurface;
    if (surf == NULL) { return 0; }
    CFRetain(surf); // keep alive after the texture is released; managed side CFReleases
    return (sy_iosurface)surf;
}
