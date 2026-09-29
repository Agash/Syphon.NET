using System.Diagnostics;

namespace Syphon.NET.Tests;

// Frame content shared with the syphon-python peer (tests/interop/syphon_peer.py pattern): BGRA bytes
// whose first pixel carries the frame index.
internal static class Frames
{
    public static byte[] Pattern(uint frame, int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int p = ((y * width) + x) * 4;
                pixels[p] = (byte)((x * 7) + (frame * 29));
                pixels[p + 1] = (byte)((y * 13) + (frame * 31));
                pixels[p + 2] = (byte)((x ^ y) + (frame * 37));
                pixels[p + 3] = 255;
            }
        }

        _ = BitConverter.TryWriteBytes(pixels, frame);
        return pixels;
    }

    public static uint Index(ReadOnlySpan<byte> pixels) => BitConverter.ToUInt32(pixels);

    public static ulong Fnv1a(ReadOnlySpan<byte> data)
    {
        ulong hash = 14695981039346656037;
        foreach (byte b in data)
        {
            hash = (hash ^ b) * 1099511628211;
        }

        return hash;
    }

    public static byte[] Read(in SyphonFrame frame)
    {
        byte[] pixels = new byte[frame.Width * frame.Height * 4];
        _ = frame.CopyPixelsTo(pixels);
        return pixels;
    }

    public static string UniqueName(string what) =>
        $"Syphon.NET {what} {Environment.ProcessId}-{Guid.NewGuid():N}";
}

// The Syphon framework in another process, through syphon-python (tests/interop/syphon_peer.py): the
// independent implementation the interop tests check Syphon.NET against. The Python is found through
// SYPHON_PYTHON, else ~/syphon-venv.
internal sealed class SyphonPeer : IAsyncDisposable
{
    private readonly Process _process;

    private SyphonPeer(Process process) => _process = process;

    public static string Python { get; } =
        Environment.GetEnvironmentVariable("SYPHON_PYTHON")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "syphon-venv",
            "bin",
            "python"
        );

    public static void Require()
    {
        if (!File.Exists(Python) || !File.Exists(Script))
        {
            Assert.Fail(
                $"The syphon-python peer is not available ({Python}, {Script}); see CONTRIBUTING.md."
            );
        }
    }

    public static async Task<SyphonPeer> StartServerAsync(
        string name,
        int width,
        int height,
        CancellationToken cancellationToken
    )
    {
        Process process = Start("server", name, width.ToString(), height.ToString());
        SyphonPeer peer = new(process);
        string? line = await process.StandardOutput.ReadLineAsync(cancellationToken);
        if (line != "READY")
        {
            await peer.DisposeAsync();
            Assert.Fail(
                $"The Syphon framework server did not start: {line} {await process.StandardError.ReadToEndAsync(cancellationToken)}"
            );
        }

        // Keep reading so the pipe never fills and blocks the server.
        _ = Task.Run(
            () => process.StandardOutput.ReadToEndAsync(CancellationToken.None),
            CancellationToken.None
        );
        return peer;
    }

    public static async Task<(
        int ExitCode,
        List<(uint Index, int Width, int Height, ulong Hash)> Frames,
        string Output
    )> ReceiveAsync(string name, int frames, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using Process process = Start(
            "client",
            name,
            frames.ToString(),
            timeout.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string errors = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        List<(uint, int, int, ulong)> received = [];
        foreach (
            string line in output.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            string[] parts = line.Split(' ');
            if (parts[0] == "FRAME")
            {
                received.Add(
                    (
                        uint.Parse(parts[1]),
                        int.Parse(parts[2]),
                        int.Parse(parts[3]),
                        ulong.Parse(parts[4])
                    )
                );
            }
        }

        return (process.ExitCode, received, output + errors);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.StandardInput.Close();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            try
            {
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // The server did not leave on its own; a test must not hang on it.
                _process.Kill();
            }
        }

        _process.Dispose();
    }

    private static string Script { get; } = FindScript();

    private static Process Start(params string[] arguments)
    {
        ProcessStartInfo start = new(Python)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Script);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start)
            ?? throw new InvalidOperationException("The syphon-python peer did not start.");
    }

    private static string FindScript()
    {
        for (
            string? dir = AppContext.BaseDirectory;
            dir is not null;
            dir = Path.GetDirectoryName(dir)
        )
        {
            string candidate = Path.Combine(dir, "tests", "interop", "syphon_peer.py");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "syphon_peer.py";
    }
}
