using Metal;

namespace Syphon.NET.Tests;

[TestClass]
public sealed class ServerClientTests
{
    private const int Width = 64;
    private const int Height = 48;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task PublishPixels_ReachesTheClientByteExact()
    {
        using SyphonServer server = new(Frames.UniqueName("pixels"));
        server.PublishPixels(Frames.Pattern(1, Width, Height), Width, Height);
        using SyphonClient client = new(server.Description);
        await WaitUntilAsync(() =>
            client.TryReceive(out SyphonFrame probe) is SyphonReceiveResult.Received
            && Dispose(probe)
        );

        Assert.AreEqual(SyphonReceiveResult.Received, client.TryReceive(out SyphonFrame frame));
        using (frame)
        {
            Assert.AreEqual(Width, frame.Width);
            Assert.AreEqual(Height, frame.Height);
            CollectionAssert.AreEqual(Frames.Pattern(1, Width, Height), Frames.Read(frame));
            Assert.IsGreaterThan(0L, frame.ObservedAtNanoseconds);
        }

        Assert.IsTrue(client.IsValid);
        Assert.AreEqual(server.Description, client.Server);
    }

    [TestMethod]
    public async Task Client_TellsNewFramesFromRepeats()
    {
        using SyphonServer server = new(Frames.UniqueName("repeat"));
        server.PublishPixels(Frames.Pattern(1, Width, Height), Width, Height);
        using SyphonClient client = new(server.Description);
        await WaitUntilAsync(() => client.HasNewFrame);

        long first = ReceiveOne(client, static frame => Assert.IsTrue(frame.IsNew));
        Assert.IsFalse(client.HasNewFrame);
        _ = ReceiveOne(client, static frame => Assert.IsFalse(frame.IsNew));

        server.PublishPixels(Frames.Pattern(2, Width, Height), Width, Height);
        await WaitUntilAsync(() => client.HasNewFrame);
        long second = ReceiveOne(
            client,
            frame =>
            {
                Assert.IsTrue(frame.IsNew);
                Assert.AreEqual(2u, Frames.Index(Frames.Read(frame)));
            }
        );
        Assert.IsGreaterThan(first, second);
    }

    [TestMethod]
    public async Task BeginFrame_RendersIntoTheServersSurfaceDirectly()
    {
        using SyphonServer server = new(Frames.UniqueName("zero copy"));
        using SyphonClient client = new(server.Description);
        using (SyphonServerFrame frame = server.BeginFrame(Width, Height))
        {
            Assert.AreSame(server.Surface, frame.Surface);
            Assert.AreEqual(Width, frame.Width);
            Assert.AreEqual(Height, frame.Height);
            Write(frame.Surface, Frames.Pattern(9, Width, Height));
            frame.Publish();
        }

        await WaitUntilAsync(() => client.HasNewFrame);
        _ = ReceiveOne(
            client,
            static frame =>
                CollectionAssert.AreEqual(Frames.Pattern(9, Width, Height), Frames.Read(frame))
        );
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => server.BeginFrame(0, Height));
    }

    [TestMethod]
    public async Task PublishTexture_CopiesAMetalTextureOnTheGpu()
    {
        IMTLDevice device =
            MTLDevice.SystemDefault ?? throw new AssertInconclusiveException("No Metal device.");
        using IMTLCommandQueue queue = device.CreateCommandQueue()!;
        using SyphonServer server = new(Frames.UniqueName("texture"));
        using SyphonClient client = new(server.Description);
        using IMTLTexture texture = device.CreateTexture(
            MTLTextureDescriptor.CreateTexture2DDescriptor(
                MTLPixelFormat.BGRA8Unorm,
                Width,
                Height,
                false
            )
        )!;
        byte[] pixels = Frames.Pattern(4, Width, Height);
        unsafe
        {
            fixed (byte* data = pixels)
            {
                texture.ReplaceRegion(
                    new MTLRegion(new MTLOrigin(0, 0, 0), new MTLSize(Width, Height, 1)),
                    0,
                    (nint)data,
                    Width * 4
                );
            }
        }

        IMTLCommandBuffer buffer = queue.CommandBuffer()!;
        server.PublishTexture(texture, buffer);
        buffer.Commit();
        buffer.WaitUntilCompleted();

        await WaitUntilAsync(() => client.HasNewFrame);
        _ = ReceiveOne(client, frame => CollectionAssert.AreEqual(pixels, Frames.Read(frame)));

        using IMTLTexture rgba = device.CreateTexture(
            MTLTextureDescriptor.CreateTexture2DDescriptor(
                MTLPixelFormat.RGBA8Unorm,
                Width,
                Height,
                false
            )
        )!;
        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            server.PublishTexture(rgba, queue.CommandBuffer()!)
        );
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PublishTexture_PublishesARegion_FlippedWhenAsked(bool flipped)
    {
        IMTLDevice device =
            MTLDevice.SystemDefault ?? throw new AssertInconclusiveException("No Metal device.");
        using IMTLCommandQueue queue = device.CreateCommandQueue()!;
        using SyphonServer server = new(Frames.UniqueName("region"));
        using SyphonClient client = new(server.Description);
        const int Outer = 96;
        using IMTLTexture texture = device.CreateTexture(
            MTLTextureDescriptor.CreateTexture2DDescriptor(
                MTLPixelFormat.BGRA8Unorm,
                Outer,
                Outer,
                false
            )
        )!;
        byte[] whole = Frames.Pattern(6, Outer, Outer);
        unsafe
        {
            fixed (byte* data = whole)
            {
                texture.ReplaceRegion(
                    new MTLRegion(new MTLOrigin(0, 0, 0), new MTLSize(Outer, Outer, 1)),
                    0,
                    (nint)data,
                    Outer * 4
                );
            }
        }

        // The frame is the region at (8, 16), Width x Height, rows reversed when flipped.
        byte[] expected = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            int from = flipped ? 16 + Height - 1 - y : 16 + y;
            whole
                .AsSpan(((from * Outer) + 8) * 4, Width * 4)
                .CopyTo(expected.AsSpan(y * Width * 4));
        }

        IMTLCommandBuffer buffer = queue.CommandBuffer()!;
        server.PublishTexture(
            texture,
            buffer,
            new MTLRegion(new MTLOrigin(8, 16, 0), new MTLSize(Width, Height, 1)),
            flipped
        );
        buffer.Commit();
        buffer.WaitUntilCompleted();

        await WaitUntilAsync(() => client.HasNewFrame);
        _ = ReceiveOne(
            client,
            frame =>
            {
                Assert.AreEqual(Width, frame.Width);
                Assert.AreEqual(Height, frame.Height);
                CollectionAssert.AreEqual(expected, Frames.Read(frame));
            }
        );
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            server.PublishTexture(
                texture,
                queue.CommandBuffer()!,
                new MTLRegion(new MTLOrigin(90, 0, 0), new MTLSize(Width, Height, 1))
            )
        );
    }

    [TestMethod]
    public async Task Resize_GivesTheClientTheNewSurface()
    {
        using SyphonServer server = new(Frames.UniqueName("resize"));
        using SyphonClient client = new(server.Description);
        server.PublishPixels(Frames.Pattern(1, Width, Height), Width, Height);
        await WaitUntilAsync(() => client.HasNewFrame);
        _ = ReceiveOne(client, static frame => Assert.AreEqual(Width, frame.Width));

        server.PublishPixels(Frames.Pattern(2, 128, 72), 128, 72);
        await WaitUntilAsync(() =>
            client.HasNewFrame
            && client.TryReceive(out SyphonFrame probe) is SyphonReceiveResult.Received
            && Width != SizeAndDispose(probe)
        );
        _ = ReceiveOne(
            client,
            static frame =>
            {
                Assert.AreEqual(128, frame.Width);
                Assert.AreEqual(72, frame.Height);
                CollectionAssert.AreEqual(Frames.Pattern(2, 128, 72), Frames.Read(frame));
            }
        );
    }

    [TestMethod]
    public async Task Retain_KeepsTheFrameAfterTheServerMovesOn()
    {
        using SyphonServer server = new(Frames.UniqueName("retain"));
        using SyphonClient client = new(server.Description);
        server.PublishPixels(Frames.Pattern(1, Width, Height), Width, Height);
        await WaitUntilAsync(() => client.HasNewFrame);

        SyphonFrameLease? lease = null;
        _ = ReceiveOne(client, frame => lease = frame.Retain());
        using (lease)
        {
            server.PublishPixels(Frames.Pattern(2, Width, Height), Width, Height);
            await WaitUntilAsync(() => client.HasNewFrame);
            _ = ReceiveOne(
                client,
                static frame => Assert.AreEqual(2u, Frames.Index(Frames.Read(frame)))
            );

            byte[] kept = new byte[Width * Height * 4];
            Assert.AreEqual(kept.Length, lease!.CopyPixelsTo(kept));
            CollectionAssert.AreEqual(Frames.Pattern(1, Width, Height), kept);
            Assert.AreEqual(Width, lease.Width);
        }

        _ = Assert.ThrowsExactly<ObjectDisposedException>(() => lease.Surface);
    }

    [TestMethod]
    public async Task Clients_AreCounted_AndARenameReachesThem()
    {
        using SyphonServer server = new(Frames.UniqueName("clients"));
        TaskCompletionSource<bool> arrived = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        TaskCompletionSource<bool> left = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientsChanged += (_, has) => (has ? arrived : left).TrySetResult(has);
        Assert.IsFalse(server.HasClients);

        SyphonClient client = new(server.Description);
        _ = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
        Assert.IsTrue(server.HasClients);

        string renamed = Frames.UniqueName("renamed");
        server.Name = renamed;
        Assert.AreEqual(renamed, server.Description.Name);
        await WaitUntilAsync(() => client.ServerName == renamed);

        client.Dispose();
        _ = await left.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
        Assert.IsFalse(server.HasClients);
    }

    [TestMethod]
    public async Task Retire_ReachesTheClient()
    {
        SyphonServer server = new(Frames.UniqueName("retire"));
        server.PublishPixels(Frames.Pattern(1, Width, Height), Width, Height);
        using SyphonClient client = new(server.Description);
        TaskCompletionSource retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ServerRetired += (_, _) => retired.TrySetResult();
        await WaitUntilAsync(() => client.HasNewFrame);

        server.Dispose();
        await retired.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken);
        Assert.IsFalse(client.IsValid);
        Assert.AreEqual(SyphonReceiveResult.ServerRetired, client.TryReceive(out _));
        _ = Assert.ThrowsExactly<ObjectDisposedException>(() =>
            server.PublishPixels(new byte[4], 1, 1)
        );
    }

    [TestMethod]
    public async Task RunAsync_DeliversNewFramesInOrderWithoutRepeats_UntilTheServerStops()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        SyphonServer server = new(Frames.UniqueName("run"));
        using SyphonClient client = new(server.Description);
        List<uint> delivered = [];
        TaskCompletionSource last = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run = client.RunAsync(
            (in SyphonFrame frame) =>
            {
                uint index = Frames.Index(Frames.Read(frame));
                delivered.Add(index);
                if (index == 10)
                {
                    last.TrySetResult();
                }
            },
            cancellationToken
        );
        for (uint i = 1; i <= 10; i++)
        {
            server.PublishPixels(Frames.Pattern(i, Width, Height), Width, Height);
            await Task.Delay(20, cancellationToken);
        }

        await last.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        server.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        CollectionAssert.AllItemsAreUnique(delivered);
        CollectionAssert.AreEqual(delivered.Order().ToList(), delivered);
        Assert.AreEqual(10u, delivered[^1]);
    }

    [TestMethod]
    public void TryReceive_BeforeTheFirstFrame_ReportsNone()
    {
        using SyphonServer server = new(Frames.UniqueName("empty"));
        using SyphonClient client = new(server.Description);
        Assert.AreEqual(SyphonReceiveResult.NoFrame, client.TryReceive(out _));
        Assert.IsNull(server.Surface);
    }

    [TestMethod]
    public void PrivateServer_IsReachedThroughItsDescription()
    {
        using SyphonServer server = new(Frames.UniqueName("private"), new() { IsPrivate = true });
        byte[] exported = server.Description.ToPropertyList();
        SyphonServerDescription imported = SyphonServerDescription.FromPropertyList(exported);
        Assert.AreEqual(server.Description, imported);
        Assert.AreEqual(server.Name, imported.Name);
        Assert.IsTrue(imported.SharesIOSurfaces);
        Assert.IsFalse(string.IsNullOrEmpty(imported.AppName));
        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            SyphonServerDescription.FromPropertyList("not a plist"u8)
        );
    }

    private static long ReceiveOne(SyphonClient client, Action<SyphonFrame> check)
    {
        Assert.AreEqual(SyphonReceiveResult.Received, client.TryReceive(out SyphonFrame frame));
        using (frame)
        {
            check(frame);
            return frame.FrameNumber;
        }
    }

    private static bool Dispose(SyphonFrame frame)
    {
        frame.Dispose();
        return true;
    }

    private static int SizeAndDispose(SyphonFrame frame)
    {
        int width = frame.Width;
        frame.Dispose();
        return width;
    }

    private static unsafe void Write(IOSurface.IOSurface surface, byte[] pixels)
    {
        _ = surface.Lock(IOSurface.IOSurfaceLockOptions.AvoidSync);
        try
        {
            int rowBytes = (int)surface.Width * 4;
            for (int y = 0; y < (int)surface.Height; y++)
            {
                pixels
                    .AsSpan(y * rowBytes, rowBytes)
                    .CopyTo(
                        new Span<byte>(
                            (byte*)surface.BaseAddress + (y * (int)surface.BytesPerRow),
                            rowBytes
                        )
                    );
            }
        }
        finally
        {
            _ = surface.Unlock(IOSurface.IOSurfaceLockOptions.AvoidSync);
        }
    }

    // Messages cross the message ports asynchronously; a test waits for their effect.
    private async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken
        );
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }
}
