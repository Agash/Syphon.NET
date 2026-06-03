#!/usr/bin/env python3
"""Foreign-implementation peer for Syphon.NET interop tests, built on syphon-python.

Modes:
  server  publish a deterministic pattern under a name (pumps its run loop so it stays
          discoverable).
  client  discover a named server, receive a frame, and verify its content byte-exact
          (allowing an R/B channel swap, which Syphon's BGRA canonicalisation can introduce).

The pattern matches the C# peer's Pattern(): per pixel (x, y) the four channels are
[x, y, x^y, (x+y)] mod 256.
"""

import argparse
import sys
import time

import numpy as np
import syphon
from syphon.utils.numpy import copy_image_to_mtl_texture, copy_mtl_texture_to_image
from syphon.utils.raw import create_mtl_texture

from Foundation import (
    NSRunLoop,
    NSDate,
    NSDefaultRunLoopMode,
    NSData,
    NSPropertyListSerialization,
)


def pattern(w: int, h: int) -> np.ndarray:
    xs = np.arange(w, dtype=np.uint16)
    ys = np.arange(h, dtype=np.uint16)
    grid_x, grid_y = np.meshgrid(xs, ys)  # (h, w)
    img = np.zeros((h, w, 4), dtype=np.uint8)
    img[..., 0] = (grid_x & 0xFF).astype(np.uint8)
    img[..., 1] = (grid_y & 0xFF).astype(np.uint8)
    img[..., 2] = ((grid_x ^ grid_y) & 0xFF).astype(np.uint8)
    img[..., 3] = ((grid_x + grid_y) & 0xFF).astype(np.uint8)
    return img


def pump(seconds: float) -> None:
    NSRunLoop.currentRunLoop().runMode_beforeDate_(
        NSDefaultRunLoopMode, NSDate.dateWithTimeIntervalSinceNow_(seconds)
    )


def run_server(name: str, w: int, h: int, seconds: int) -> int:
    server = syphon.SyphonMetalServer(name)
    texture = create_mtl_texture(server.device, w, h)
    copy_image_to_mtl_texture(pattern(w, h), texture)
    print(f"[py-server] publishing '{name}' {w}x{h} for {seconds}s", flush=True)
    end = time.time() + seconds
    while time.time() < end:
        server.publish_frame_texture(texture)
        pump(0.016)  # keep responding to directory announce requests
    server.stop()
    return 0


def description_from_file(path: str) -> syphon.SyphonServerDescription:
    raw = open(path, "rb").read()
    data = NSData.dataWithBytes_length_(raw, len(raw))
    plist, _fmt, _err = NSPropertyListSerialization.propertyListWithData_options_format_error_(
        data, 0, None, None
    )
    uuid = plist.objectForKey_("SyphonServerDescriptionUUIDKey")
    name = plist.objectForKey_("SyphonServerDescriptionNameKey")
    app = plist.objectForKey_("SyphonServerDescriptionAppNameKey")
    return syphon.SyphonServerDescription(
        str(uuid) if uuid else "",
        str(name) if name else "",
        str(app) if app else "",
        None,
        plist,
    )


def run_client(name: str, timeout: int, desc_file: str) -> int:
    if desc_file:
        # Connect via an exported description (bypasses the directory, whose syphon-python wrapper
        # assumes an icon key that an iconless console server does not advertise).
        description = description_from_file(desc_file)
        print(f"[py-client] connecting to '{name}' via exported description", flush=True)
    else:
        directory = syphon.SyphonServerDirectory()
        description = None
        end = time.time() + timeout
        while time.time() < end and description is None:
            matches = [s for s in directory.servers if s.name == name]  # .servers pumps internally
            if matches:
                description = matches[0]
            else:
                time.sleep(0.1)
        if description is None:
            print(f"[py-client] foreign server '{name}' not discovered", flush=True)
            return 3
        print(f"[py-client] discovered '{name}'; connecting", flush=True)

    client = syphon.SyphonMetalClient(description)
    frame = None
    end = time.time() + timeout
    while time.time() < end and frame is None:
        if client.has_new_frame:
            frame = copy_mtl_texture_to_image(client.new_frame_image)
        else:
            time.sleep(0.016)
    client.stop()

    if frame is None:
        print("[py-client] no frame received", flush=True)
        return 4

    h, w = frame.shape[0], frame.shape[1]
    expected = pattern(w, h)
    print(f"[py-client] recv {w}x{h} shape {frame.shape}", flush=True)
    print(f"[py-client] expected[0,0:2]={expected[0, 0:2].tolist()}", flush=True)
    print(f"[py-client] got     [0,0:2]={frame[0, 0:2].tolist()}", flush=True)

    if np.array_equal(expected, frame):
        print("[py-client] PASS byte-exact", flush=True)
        return 0
    if np.array_equal(expected, frame[..., [2, 1, 0, 3]]):
        print("[py-client] PASS (R/B swapped)", flush=True)
        return 0
    print("[py-client] MISMATCH", flush=True)
    return 1


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["server", "client"])
    parser.add_argument("--name", default="PyPeer")
    parser.add_argument("--w", type=int, default=64)
    parser.add_argument("--h", type=int, default=64)
    parser.add_argument("--seconds", type=int, default=40)
    parser.add_argument("--timeout", type=int, default=20)
    parser.add_argument("--desc-file", default="")
    args = parser.parse_args()

    if args.mode == "server":
        return run_server(args.name, args.w, args.h, args.seconds)
    return run_client(args.name, args.timeout, args.desc_file)


if __name__ == "__main__":
    sys.exit(main())
