namespace Syphon.NET.Tests;

/// <summary>
/// Syphon.NET against the Syphon framework in another process (syphon-python): frames must cross
/// byte-exact in both directions, found through each side's own directory.
/// </summary>
[TestClass]
public sealed class InteropTests
{
    private const int Width = 64;
    private const int Height = 48;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task FrameworkServer_IsReceivedByteExact()
    {
        SyphonPeer.Require();
        CancellationToken cancellationToken = TestContext.CancellationToken;
        string name = Frames.UniqueName("from framework");
        await using SyphonPeer peer = await SyphonPeer.StartServerAsync(
            name,
            Width,
            Height,
            cancellationToken
        );
        using SyphonServerDirectory directory = new();
        SyphonServerDescription server = await directory
            .WaitForServerAsync(s => s.Name == name, cancellationToken)
            .WaitAsync(Timeout, cancellationToken);

        using SyphonClient client = new(server);
        List<uint> checkedFrames = [];
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (checkedFrames.Count < 5)
        {
            Assert.IsLessThan(deadline, DateTime.UtcNow, $"received {checkedFrames.Count} frames");
            if (client.HasNewFrame && TryCheck(client, out uint index))
            {
                checkedFrames.Add(index);
            }

            await Task.Delay(5, cancellationToken);
        }

        // A server writes every frame into one surface, so a frame read while it writes the next can
        // already hold the next one, which its own announcement then repeats: frames never go back and
        // do advance, but one index can be seen twice.
        CollectionAssert.AreEqual(checkedFrames.Order().ToList(), checkedFrames);
        Assert.IsGreaterThan(checkedFrames[0], checkedFrames[^1]);
    }

    [TestMethod]
    public async Task Server_IsReceivedByTheFrameworkByteExact()
    {
        SyphonPeer.Require();
        CancellationToken cancellationToken = TestContext.CancellationToken;
        using SyphonServer server = new(Frames.UniqueName("to framework"));
        server.PublishPixels(Frames.Pattern(1, Width, Height), Width, Height);

        Task<(
            int ExitCode,
            List<(uint Index, int Width, int Height, ulong Hash)> Frames,
            string Output
        )> receiving = SyphonPeer.ReceiveAsync(server.Name, 4, Timeout, cancellationToken);
        uint frame = 1;
        while (!receiving.IsCompleted)
        {
            await Task.Delay(30, cancellationToken);
            server.PublishPixels(Frames.Pattern(++frame, Width, Height), Width, Height);
        }

        var (exitCode, received, output) = await receiving;
        Assert.AreEqual(0, exitCode, output);
        Assert.HasCount(4, received);
        foreach ((uint index, int width, int height, ulong hash) in received)
        {
            Assert.AreEqual(Width, width);
            Assert.AreEqual(Height, height);
            Assert.AreEqual(
                Frames.Fnv1a(Frames.Pattern(index, Width, Height)),
                hash,
                $"frame {index}"
            );
        }
    }

    // Borrows the current frame and checks its pixels are the pattern the frame index they carry names.
    private static bool TryCheck(SyphonClient client, out uint index)
    {
        index = 0;
        if (client.TryReceive(out SyphonFrame frame) != SyphonReceiveResult.Received)
        {
            return false;
        }

        using (frame)
        {
            Assert.AreEqual(Width, frame.Width);
            Assert.AreEqual(Height, frame.Height);
            byte[] pixels = Frames.Read(frame);
            index = Frames.Index(pixels);
            CollectionAssert.AreEqual(
                Frames.Pattern(index, Width, Height),
                pixels,
                $"frame {index}"
            );
            return frame.IsNew;
        }
    }
}
