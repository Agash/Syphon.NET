using System.Diagnostics;
using CoreVideo;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Syphon.NET;

namespace Syphon.NET.Tests;

/// <summary>Pure value-type tests that need no native library and run on any host.</summary>
[TestClass]
public sealed class SyphonValueTypeTests
{
    [TestMethod]
    public void ServerDescription_Equality_IsByValue()
    {
        var x = new SyphonServerDescription("uuid", "App", "Main");
        var y = new SyphonServerDescription("uuid", "App", "Main");
        Assert.AreEqual(x, y);
    }
}

/// <summary>
/// End-to-end frame transport: a server publishes a known pattern and a client connected via the
/// server description (no distributed-notification directory) receives it. These are gating - they
/// run wherever a Metal device is present (including headless CI) and assert byte-exact delivery.
/// They report Inconclusive only when there is no Metal device or native library at all.
/// Surfaces are the Microsoft <see cref="IOSurface"/> bindings directly - the helpers come from
/// <see cref="IOSurfaceExtensions"/>.
/// </summary>
[TestClass]
public sealed class SyphonTransportTests
{
    [TestMethod]
    [TestCategory("Transport")]
    public void Bgra_64x64_RoundTripsByteExact() => AssertLoopback(64, 64);

    [TestMethod]
    [TestCategory("Transport")]
    public void Bgra_48x32_RoundTripsByteExact_WithRowPadding() => AssertLoopback(48, 32);

    private static void AssertLoopback(int w, int h)
    {
        SyphonServer server = null!;
        try
        {
            server = new SyphonServer($"Syphon.NET Test {w}x{h}");
        }
        catch (DllNotFoundException)
        {
            Assert.Inconclusive("Native Syphon shim not present on this host.");
        }
        catch (PlatformNotSupportedException)
        {
            Assert.Inconclusive("No Metal device available on this host.");
        }

        using (server)
        {
            byte[] expected = Pattern(w, h);

            using SyphonClient client = server.CreateLoopbackClient();
            // Not disposed: delivered frames belong to the client (see SyphonClient.TryGetFrame).
            IOSurface.IOSurface? surface = PollLatest(server, client, expected, w, h);

            Assert.IsNotNull(surface, "a published frame should be delivered to the loopback client");
            (int gotW, int gotH) = surface.PixelSize();
            Assert.AreEqual(w, gotW);
            Assert.AreEqual(h, gotH);
            Assert.IsTrue(surface.IsBgra(), "Syphon delivers frames in its canonical BGRA surface format");

            byte[] got = new byte[w * h * 4];
            surface.CopyTightlyPacked(got);
            CollectionAssert.AreEqual(expected, got, $"pixels must round-trip ({w}x{h})");
        }
    }

    private static IOSurface.IOSurface? PollLatest(
        SyphonServer server, SyphonClient client, byte[] src, int w, int h)
    {
        // Publish repeatedly, keeping the most recent delivered frame and discarding an initial
        // stale one by requiring a few publishes before accepting. The frames are the client's to
        // own - never dispose one here, or every later frame (the same managed peer) comes back with
        // a zeroed handle.
        IOSurface.IOSurface? frame = null;
        var sw = Stopwatch.StartNew();
        int published = 0;
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            server.PublishPixels(src, w, h, CVPixelFormatType.CV32BGRA);
            published++;
            IOSurface.IOSurface? f = client.TryGetFrame();
            if (f is not null)
            {
                frame = f;
                if (published >= 4) break;
            }
            Thread.Sleep(16);
        }
        return frame;
    }

    /// <summary>
    /// A server recycles one surface, so every delivered frame is the same native object and the
    /// bindings hand back the same managed peer. Polling a long run of frames has to keep yielding a
    /// readable surface - it did not while the loop disposed each frame, which zeroed that shared peer.
    /// </summary>
    [TestMethod]
    [TestCategory("Transport")]
    public void RepeatedFrames_StayReadable()
    {
        const int w = 32, h = 16;
        SyphonServer server = null!;
        try
        {
            server = new SyphonServer("Syphon.NET Repeat Test");
        }
        catch (DllNotFoundException)
        {
            Assert.Inconclusive("Native Syphon shim not present on this host.");
        }
        catch (PlatformNotSupportedException)
        {
            Assert.Inconclusive("No Metal device available on this host.");
        }

        using (server)
        {
            byte[] expected = Pattern(w, h);
            using SyphonClient client = server.CreateLoopbackClient();

            int received = 0;
            var sw = Stopwatch.StartNew();
            while (received < 10 && sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                server.PublishPixels(expected, w, h, CVPixelFormatType.CV32BGRA);
                IOSurface.IOSurface? frame = client.TryGetFrame();
                if (frame is null) { Thread.Sleep(16); continue; }

                received++;
                (int gotW, int gotH) = frame.PixelSize();
                Assert.AreEqual(w, gotW, $"frame {received} should still report its width");
                Assert.AreEqual(h, gotH, $"frame {received} should still report its height");

                byte[] got = new byte[w * h * 4];
                frame.CopyTightlyPacked(got);
                CollectionAssert.AreEqual(expected, got, $"frame {received} must round-trip");
            }

            Assert.AreEqual(10, received, "ten published frames should have been delivered");
        }
    }

    private static byte[] Pattern(int w, int h)
    {
        byte[] p = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                p[i] = (byte)x;
                p[i + 1] = (byte)y;
                p[i + 2] = (byte)(x ^ y);
                p[i + 3] = (byte)((x + y) & 0xFF);
            }
        }
        return p;
    }
}

/// <summary>
/// Exercises <see cref="IOSurfaceExtensions"/> directly against a server-owned surface: write
/// tightly-packed pixels in, read them back, and confirm format/plane predicates. Gating wherever a
/// Metal device is present; Inconclusive only without Metal or the native library.
/// </summary>
[TestClass]
public sealed class IOSurfaceExtensionsTests
{
    [TestMethod]
    [TestCategory("Transport")]
    public void WritePixels_ThenCopyTightlyPacked_RoundTripsByteExact()
    {
        const int w = 48, h = 32;
        SyphonServer server = null!;
        try
        {
            server = new SyphonServer("Syphon.NET Surface Test");
        }
        catch (DllNotFoundException)
        {
            Assert.Inconclusive("Native Syphon shim not present on this host.");
        }
        catch (PlatformNotSupportedException)
        {
            Assert.Inconclusive("No Metal device available on this host.");
        }

        using (server)
        {
            IOSurface.IOSurface surface = server.AcquireSurface(w, h, CVPixelFormatType.CV32BGRA);
            Assert.IsTrue(surface.IsBgra());
            Assert.AreEqual(1, surface.PlaneCount());
            (int pw, int ph) = surface.PixelSize();
            Assert.AreEqual(w, pw);
            Assert.AreEqual(h, ph);

            byte[] pattern = Pattern(w, h);
            surface.WritePixels(pattern);

            byte[] got = new byte[w * h * 4];
            int written = surface.CopyTightlyPacked(got);
            Assert.AreEqual(w * h * 4, written);
            CollectionAssert.AreEqual(pattern, got, "WritePixels/CopyTightlyPacked must round-trip byte-exact");
        }
    }

    /// <summary>
    /// IOSurface reports no planes for a packed surface and raises an Objective-C exception from its
    /// per-plane accessors; the helpers present that surface as the single plane it is.
    /// </summary>
    [TestMethod]
    [TestCategory("Transport")]
    public void PlaneInfo_OnPackedBgra_DescribesTheWholeSurface()
    {
        const int w = 48, h = 32;
        SyphonServer server = null!;
        try
        {
            server = new SyphonServer("Syphon.NET Plane Test");
        }
        catch (DllNotFoundException)
        {
            Assert.Inconclusive("Native Syphon shim not present on this host.");
        }
        catch (PlatformNotSupportedException)
        {
            Assert.Inconclusive("No Metal device available on this host.");
        }

        using (server)
        {
            IOSurface.IOSurface surface = server.AcquireSurface(w, h, CVPixelFormatType.CV32BGRA);
            Assert.AreEqual(1, surface.PlaneCount());

            (int pw, int ph, int stride) = surface.PlaneInfo(0);
            Assert.AreEqual(w, pw);
            Assert.AreEqual(h, ph);
            Assert.IsTrue(stride >= w * 4, "the stride must cover a row of pixels");

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => surface.PlaneInfo(1));
        }
    }

    /// <summary>
    /// The server recycles its surface, and the bindings keep one managed peer per native object, so
    /// repeated acquires are the same instance - which is why callers must not dispose it.
    /// </summary>
    [TestMethod]
    [TestCategory("Transport")]
    public void AcquireSurface_ReturnsTheRecycledInstance()
    {
        SyphonServer server = null!;
        try
        {
            server = new SyphonServer("Syphon.NET Recycle Test");
        }
        catch (DllNotFoundException)
        {
            Assert.Inconclusive("Native Syphon shim not present on this host.");
        }
        catch (PlatformNotSupportedException)
        {
            Assert.Inconclusive("No Metal device available on this host.");
        }

        using (server)
        {
            IOSurface.IOSurface first = server.AcquireSurface(64, 64);
            IOSurface.IOSurface second = server.AcquireSurface(64, 64);
            Assert.AreSame(first, second);
            Assert.AreEqual((64, 64), second.PixelSize());
        }
    }

    private static byte[] Pattern(int w, int h)
    {
        byte[] p = new byte[w * h * 4];
        for (int i = 0; i < p.Length; i++) p[i] = (byte)(i * 7 + 3);
        return p;
    }
}

/// <summary>
/// Directory discovery uses NSDistributedNotificationCenter, which needs a Cocoa run loop to be
/// pumped. With <see cref="SyphonServerDirectory.PumpEvents"/> it works anywhere a Metal device is
/// present - including headless CI - so these are gating. They report Inconclusive only when there
/// is no Metal device or native library.
/// </summary>
[TestClass]
public sealed class SyphonDirectoryTests
{
    [TestMethod]
    [TestCategory("Directory")]
    public void Directory_DiscoversAPublishedServer()
    {
        const string name = "Syphon.NET Directory Test";
        SyphonServer server = null!;
        try
        {
            server = new SyphonServer(name);
        }
        catch (DllNotFoundException)
        {
            Assert.Inconclusive("Native Syphon shim not present on this host.");
        }
        catch (PlatformNotSupportedException)
        {
            Assert.Inconclusive("No Metal device available on this host.");
        }

        using (server)
        {
            using var directory = new SyphonServerDirectory();
            var sw = Stopwatch.StartNew();
            bool found = false;
            while (sw.Elapsed < TimeSpan.FromSeconds(8) && !found)
            {
                // Pump the run loop so the directory receives the server's announce notification.
                directory.PumpEvents(TimeSpan.FromMilliseconds(200));
                found = directory.GetServers().Any(s => s.Name == name);
            }

            Assert.IsTrue(found, "the published server should be discovered via the directory");
        }
    }
}
