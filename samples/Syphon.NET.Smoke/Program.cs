// Publishes a frame, finds the server in the directory, receives the frame byte-exact and exits 0:
// run as Native AOT, it shows the library works trimmed and without the JIT. A console host serves the
// main run loop, which Syphon's discovery goes through, with SyphonMainLoop.
using Syphon.NET;

return SyphonMainLoop.Run(RunAsync);

static async Task<int> RunAsync()
{
    const int Width = 64;
    const int Height = 32;

    byte[] pixels = new byte[Width * Height * 4];
    for (int i = 0; i < pixels.Length; i++)
    {
        pixels[i] = (byte)(i * 7);
    }

    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
    using SyphonServer server = new($"Syphon.NET smoke {Environment.ProcessId}");
    server.PublishPixels(pixels, Width, Height);

    using SyphonServerDirectory directory = new();
    SyphonServerDescription found = await directory.WaitForServerAsync(
        s => s.Uuid == server.Description.Uuid,
        timeout.Token
    );

    using SyphonClient client = new(found);
    byte[] received = new byte[pixels.Length];
    while (!Receive(client, received))
    {
        await Task.Delay(10, timeout.Token);
    }

    if (!received.AsSpan().SequenceEqual(pixels))
    {
        Console.Error.WriteLine("fail: the received frame differs from the published one");
        return 1;
    }

    Console.WriteLine(
        $"ok: Syphon.NET smoke {found.Name} {Width}x{Height}, {directory.Servers.Length} server(s)"
    );
    return 0;
}

static bool Receive(SyphonClient client, Span<byte> destination)
{
    if (client.TryReceive(out SyphonFrame frame) != SyphonReceiveResult.Received)
    {
        return false;
    }

    using (frame)
    {
        return frame.CopyPixelsTo(destination) == destination.Length;
    }
}
