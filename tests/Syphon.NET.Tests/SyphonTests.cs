using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Syphon.NET;

namespace Syphon.NET.Tests;

/// <summary>Pure value-type tests that need no native library and run on any host.</summary>
[TestClass]
public sealed class SyphonValueTypeTests
{
    [TestMethod]
    public void PixelFormat_FourCcValues_MatchIOSurfaceCodes()
    {
        Assert.AreEqual((uint)SyphonPixelFormat.Bgra, FourCc("BGRA"));
        Assert.AreEqual((uint)SyphonPixelFormat.Rgba, FourCc("RGBA"));
    }

    private static uint FourCc(string code) =>
        (uint)((code[0] << 24) | (code[1] << 16) | (code[2] << 8) | code[3]);

    [TestMethod]
    public void IOSurface_Equality_ComparesHandles()
    {
        IOSurface a = default;
        IOSurface b = default;
        Assert.IsTrue(a == b);
        Assert.IsFalse(a.IsValid);
    }

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
/// </summary>
[TestClass]
public sealed class SyphonTransportTests
{
    [TestMethod]
    [TestCategory("Transport")]
    public void Bgra_64x64_RoundTripsByteExact() =>
        AssertLoopback(SyphonPixelFormat.Bgra, 64, 64, swizzle: false);

    [TestMethod]
    [TestCategory("Transport")]
    public void Bgra_48x32_RoundTripsByteExact_WithRowPadding() =>
        AssertLoopback(SyphonPixelFormat.Bgra, 48, 32, swizzle: false);

    [TestMethod]
    [TestCategory("Transport")]
    public void Rgba_48x32_IsColourConvertedToBgra() =>
        AssertLoopback(SyphonPixelFormat.Rgba, 48, 32, swizzle: true);

    private static void AssertLoopback(SyphonPixelFormat format, int w, int h, bool swizzle)
    {
        SyphonServer server = null!;
        try
        {
            server = new SyphonServer($"Syphon.NET Test {format} {w}x{h}");
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
            byte[] src = Pattern(w, h);
            byte[] expected = swizzle ? SwapRedBlue(src) : src;

            using SyphonClient client = server.CreateLoopbackClient();
            using SyphonFrame? frame = PollLatest(server, client, src, w, h, format);

            Assert.IsNotNull(frame, "a published frame should be delivered to the loopback client");
            Assert.AreEqual(w, frame.Surface.Width);
            Assert.AreEqual(h, frame.Surface.Height);
            Assert.AreEqual(SyphonPixelFormat.Bgra, frame.Surface.PixelFormat,
                "Syphon delivers frames in its canonical BGRA surface format");

            byte[] got = new byte[w * h * 4];
            frame.CopyTo(got);
            CollectionAssert.AreEqual(expected, got, $"pixels must round-trip ({format} {w}x{h})");
        }
    }

    private static SyphonFrame? PollLatest(
        SyphonServer server, SyphonClient client, byte[] src, int w, int h, SyphonPixelFormat format)
    {
        // Publish repeatedly, keeping the most recent delivered frame and discarding an initial
        // stale one by requiring a few publishes before accepting.
        SyphonFrame? frame = null;
        var sw = Stopwatch.StartNew();
        int published = 0;
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            server.PublishPixels(src, w, h, format);
            published++;
            SyphonFrame? f = client.TryGetFrame();
            if (f is not null)
            {
                frame?.Dispose();
                frame = f;
                if (published >= 4) break;
            }
            Thread.Sleep(16);
        }
        return frame;
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

    private static byte[] SwapRedBlue(byte[] rgba)
    {
        byte[] bgra = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = rgba[i + 3];
        }
        return bgra;
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
