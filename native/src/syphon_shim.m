// syphon_shim.m
//
// Implementation of the flat C ABI declared in syphon_shim.h, over the Syphon Metal
// framework. Compiled with ARC. Built on macOS only; see native/build-native.sh.

#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#import <IOSurface/IOSurface.h>
#import <CoreFoundation/CoreFoundation.h>

#import "SyphonServerBase.h"
#import "SyphonMetalServer.h"
#import "SyphonMetalClient.h"
#import "SyphonServerDirectory.h"

#include "syphon_shim.h"

// Server-description dictionary keys (SyphonServerDescriptionUUIDKey, NameKey, AppNameKey)
// are declared by SyphonServerDirectory.h.

// ---- Shared Metal objects -------------------------------------------------

static id<MTLDevice>       g_device = nil;
static id<MTLCommandQueue> g_queue  = nil;

int sy_init(void)
{
    if (g_device != nil) { return 0; }
    g_device = MTLCreateSystemDefaultDevice();
    if (g_device == nil) { return 1; }
    g_queue = [g_device newCommandQueue];
    return g_queue != nil ? 0 : 1;
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

// ---- Helpers --------------------------------------------------------------

static MTLPixelFormat pixel_format_from_fourcc(uint32_t fourcc)
{
    switch (fourcc)
    {
        case 'BGRA': return MTLPixelFormatBGRA8Unorm; // 0x42475241
        case 'RGBA': return MTLPixelFormatRGBA8Unorm; // 0x52474241
        default:     return MTLPixelFormatBGRA8Unorm;
    }
}

static void dict_set_int(CFMutableDictionaryRef d, CFStringRef key, int32_t value)
{
    CFNumberRef n = CFNumberCreate(kCFAllocatorDefault, kCFNumberSInt32Type, &value);
    CFDictionarySetValue(d, key, n);
    CFRelease(n);
}

static IOSurfaceRef create_iosurface(uint32_t width, uint32_t height, uint32_t fourcc)
{
    uint32_t bytesPerElement = 4;
    // Use IOSurface's own alignment for the row and allocation sizes. A hand-rolled alignment
    // (e.g. 16 bytes) is too small for Metal's IOSurface-backed textures, which misreads every
    // row after the first when width*4 is not on the device's required stride.
    size_t bytesPerRow = IOSurfaceAlignProperty(kIOSurfaceBytesPerRow, (size_t)width * bytesPerElement);
    size_t allocSize = IOSurfaceAlignProperty(kIOSurfaceAllocSize, (size_t)height * bytesPerRow);

    CFMutableDictionaryRef props = CFDictionaryCreateMutable(
        kCFAllocatorDefault, 0,
        &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);

    dict_set_int(props, kIOSurfaceWidth, (int32_t)width);
    dict_set_int(props, kIOSurfaceHeight, (int32_t)height);
    dict_set_int(props, kIOSurfaceBytesPerElement, (int32_t)bytesPerElement);
    dict_set_int(props, kIOSurfaceBytesPerRow, (int32_t)bytesPerRow);
    dict_set_int(props, kIOSurfaceAllocSize, (int32_t)allocSize);
    dict_set_int(props, kIOSurfacePixelFormat, (int32_t)fourcc);

    IOSurfaceRef surface = IOSurfaceCreate(props);
    CFRelease(props);
    return surface;
}

static id<MTLTexture> texture_from_iosurface(IOSurfaceRef surface, MTLPixelFormat fmt)
{
    if (surface == NULL) { return nil; }
    MTLTextureDescriptor* desc = [MTLTextureDescriptor
        texture2DDescriptorWithPixelFormat:fmt
                                     width:IOSurfaceGetWidth(surface)
                                    height:IOSurfaceGetHeight(surface)
                                 mipmapped:NO];
    desc.usage = MTLTextureUsageShaderRead | MTLTextureUsageRenderTarget;
    desc.storageMode = MTLStorageModeShared;
    return [g_device newTextureWithDescriptor:desc iosurface:surface plane:0];
}

// ---- Server ---------------------------------------------------------------

typedef struct sy_server_t {
    SyphonMetalServer* server;
    IOSurfaceRef       surface;   // server-owned writable surface (CPU producer path)
    id<MTLTexture>     texture;   // texture bound to `surface`
    uint32_t           width;
    uint32_t           height;
    uint32_t           fourcc;
} sy_server_t;

sy_server sy_server_create(const char* name)
{
    if (sy_init() != 0) { return NULL; }
    sy_server_t* s = calloc(1, sizeof(sy_server_t));
    NSString* nsName = name != NULL ? [NSString stringWithUTF8String:name] : nil;
    s->server = [[SyphonMetalServer alloc] initWithName:nsName device:g_device options:nil];
    return s;
}

void sy_server_destroy(sy_server server)
{
    if (server == NULL) { return; }
    sy_server_t* s = (sy_server_t*)server;
    [s->server stop];
    s->server = nil;
    s->texture = nil;
    if (s->surface) { CFRelease(s->surface); s->surface = NULL; }
    free(s);
}

int sy_server_has_clients(sy_server server)
{
    if (server == NULL) { return 0; }
    return ((sy_server_t*)server)->server.hasClients ? 1 : 0;
}

static int publish_texture(sy_server_t* s, id<MTLTexture> tex, IOSurfaceRef surf, int flipped)
{
    if (tex == nil) { return 1; }

    IOSurfaceIncrementUseCount(surf);
    id<MTLCommandBuffer> cmd = [g_queue commandBuffer];

    [s->server publishFrameTexture:tex
                   onCommandBuffer:cmd
                       imageRegion:NSMakeRect(0, 0, tex.width, tex.height)
                           flipped:(flipped != 0)];

    [cmd addCompletedHandler:^(id<MTLCommandBuffer> _Nonnull c) {
        (void)c;
        IOSurfaceDecrementUseCount(surf);
    }];
    [cmd commit];
    return 0;
}

int sy_server_publish_surface(sy_server server, sy_iosurface surface, int flipped)
{
    if (server == NULL || surface == 0) { return 1; }
    sy_server_t* s = (sy_server_t*)server;
    IOSurfaceRef surf = (IOSurfaceRef)surface;

    OSType fourcc = IOSurfaceGetPixelFormat(surf);
    id<MTLTexture> tex = texture_from_iosurface(surf, pixel_format_from_fourcc(fourcc));
    return publish_texture(s, tex, surf, flipped);
}

sy_iosurface sy_server_acquire_surface(sy_server server, uint32_t width, uint32_t height, uint32_t pixel_format)
{
    if (server == NULL || width == 0 || height == 0) { return 0; }
    sy_server_t* s = (sy_server_t*)server;

    if (s->surface == NULL || s->width != width || s->height != height || s->fourcc != pixel_format)
    {
        if (s->surface) { CFRelease(s->surface); s->surface = NULL; }
        s->texture = nil;
        s->surface = create_iosurface(width, height, pixel_format);
        if (s->surface == NULL) { return 0; }
        s->texture = texture_from_iosurface(s->surface, pixel_format_from_fourcc(pixel_format));
        s->width = width; s->height = height; s->fourcc = pixel_format;
    }
    return (sy_iosurface)s->surface;
}

int sy_server_publish_current(sy_server server, int flipped)
{
    if (server == NULL) { return 1; }
    sy_server_t* s = (sy_server_t*)server;
    if (s->surface == NULL || s->texture == nil) { return 1; }
    return publish_texture(s, s->texture, s->surface, flipped);
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
