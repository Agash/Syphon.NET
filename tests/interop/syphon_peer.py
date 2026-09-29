#!/usr/bin/env python3
"""The Syphon framework, through syphon-python, as an independent peer for Syphon.NET's interop tests.

syphon-python ships the upstream Syphon framework, so this is the reference implementation talking to
Syphon.NET across processes.

  syphon_peer.py server <name> <width> <height>
      publishes frames until stdin closes; prints READY, then SENT <frame> for every frame
  syphon_peer.py client <name> <frames> <seconds>
      finds the server by name, receives <frames> new frames or gives up; prints
      FRAME <index> <width> <height> <fnv1a> per frame, <index> being the frame index the pixels carry

Frame content is pattern() below; the tests compute the same bytes (BGRA, the frame index in the first
pixel).
"""

import struct
import sys
import threading
import time

import Metal
import numpy as np
import objc
import syphon
from Foundation import NSDate, NSDefaultRunLoopMode, NSRunLoop
from syphon.utils.numpy import copy_image_to_mtl_texture, copy_mtl_texture_to_image
from syphon.utils.raw import create_mtl_texture


def pattern(frame: int, width: int, height: int) -> np.ndarray:
    xs = np.arange(width, dtype=np.uint32)
    ys = np.arange(height, dtype=np.uint32)
    x, y = np.meshgrid(xs, ys)
    image = np.zeros((height, width, 4), dtype=np.uint8)
    image[..., 0] = ((x * 7 + frame * 29) & 0xFF).astype(np.uint8)
    image[..., 1] = ((y * 13 + frame * 31) & 0xFF).astype(np.uint8)
    image[..., 2] = (((x ^ y) + frame * 37) & 0xFF).astype(np.uint8)
    image[..., 3] = 255
    image.reshape(-1)[:4] = np.frombuffer(struct.pack("<I", frame), dtype=np.uint8)
    return image


def fnv1a(data: bytes) -> int:
    value = 14695981039346656037
    for byte in data:
        value = ((value ^ byte) * 1099511628211) & 0xFFFFFFFFFFFFFFFF
    return value


def pump(seconds: float) -> None:
    NSRunLoop.currentRunLoop().runMode_beforeDate_(
        NSDefaultRunLoopMode, NSDate.dateWithTimeIntervalSinceNow_(seconds)
    )


def serve(name: str, width: int, height: int) -> int:
    server = syphon.SyphonMetalServer(name)
    texture = create_mtl_texture(server.device, width, height, Metal.MTLPixelFormatBGRA8Unorm)
    stop = threading.Event()
    threading.Thread(target=lambda: (sys.stdin.read(), stop.set()), daemon=True).start()
    frame = 0
    print("READY", flush=True)
    while not stop.is_set():
        frame += 1
        copy_image_to_mtl_texture(pattern(frame, width, height), texture)
        # The next frame overwrites the texture from the CPU, so this frame's GPU copy out of it must
        # finish first, or it would publish the next frame's pixels.
        command_buffer = server.command_queue.commandBuffer()
        server.publish_frame_texture(texture, command_buffer=command_buffer)
        command_buffer.waitUntilCompleted()
        print(f"SENT {frame}", flush=True)
        pump(0.016)  # also answers directory announce requests
    server.stop()
    return 0


def find(name: str, deadline: float):
    # The framework's own directory. syphon-python's wrapper reads each server's icon unconditionally,
    # and a server hosted by a console process (as the tests' are) has none.
    directory = objc.lookUpClass("SyphonServerDirectory").sharedDirectory()
    while time.time() < deadline:
        pump(0.05)
        for raw in directory.servers():
            if str(raw.get("SyphonServerDescriptionNameKey", "")) == name:
                return syphon.SyphonServerDescription(
                    str(raw["SyphonServerDescriptionUUIDKey"]),
                    name,
                    str(raw.get("SyphonServerDescriptionAppNameKey", "")),
                    None,
                    raw,
                )
    return None


def receive(name: str, frames: int, seconds: float) -> int:
    deadline = time.time() + seconds
    description = find(name, deadline)
    if description is None:
        print("ERROR no server", flush=True)
        return 1

    client = syphon.SyphonMetalClient(description)
    received = 0
    while received < frames and time.time() < deadline:
        pump(0.002)
        if not client.has_new_frame:
            continue
        texture = client.new_frame_image
        image = copy_mtl_texture_to_image(texture)
        data = image.tobytes()
        index = struct.unpack("<I", data[:4])[0]
        print(f"FRAME {index} {texture.width()} {texture.height()} {fnv1a(data)}", flush=True)
        received += 1
    client.stop()
    return 0 if received == frames else 1


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    if mode == "server" and len(sys.argv) == 5:
        sys.exit(serve(sys.argv[2], int(sys.argv[3]), int(sys.argv[4])))
    if mode == "client" and len(sys.argv) == 5:
        sys.exit(receive(sys.argv[2], int(sys.argv[3]), float(sys.argv[4])))
    print(__doc__)
    sys.exit(64)
