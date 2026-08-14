// Syphon.NET probe: a small diagnostic that reports, in plain stdout, exactly which layers
// work on the current host. Used to settle whether end-to-end frame transport (and directory
// discovery) can be verified on a headless CI runner or only on a real Mac session.
//
// Output is line-flushed so that if a native call aborts the process (SIGABRT), the last line
// printed pinpoints the failing step.

using System.Diagnostics;
using CoreVideo;
using Syphon.NET;

// Force auto-flush: when stdout is redirected (CI) it is otherwise block-buffered and a native
// abort would discard everything after the first newline.
var flushing = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
Console.SetOut(flushing);

string mode = args.Length > 0 ? args[0] : "probe";
return mode switch
{
    "probe" => Probe(),
    "server" => ServerMode(args),
    "crosstest" => CrossTest(),
    "foreign-client" => ForeignClient(args),
    _ => Unknown(mode),
};

static int Probe()
{
    Log("== Syphon.NET probe ==");

    Log("step: create server (sy_init -> MTLCreateSystemDefaultDevice, SyphonMetalServer init)");
    SyphonServer server;
    try
    {
        server = new SyphonServer("Syphon.NET Probe");
    }
    catch (DllNotFoundException)
    {
        Log("NATIVE: shim not found");
        Log("SUMMARY: no-native");
        return 3;
    }
    catch (PlatformNotSupportedException)
    {
        Log("METAL: unavailable");
        Log("SUMMARY: no-metal");
        return 2;
    }

    server.Dispose();
    Log("METAL: ok (device + server created)");

    // BGRA is Syphon's canonical surface format, so a BGRA round-trip must be byte-exact.
    // Test both a 256-aligned width (64) and a non-aligned one (48) to confirm row stride.
    bool bgra64 = Loopback(64, 64, out string d1);
    Log($"LOOPBACK BGRA 64x64: {(bgra64 ? "PASS" : "FAIL")} ({d1})");

    bool bgra48 = Loopback(48, 32, out string d2);
    Log($"LOOPBACK BGRA 48x32: {(bgra48 ? "PASS" : "FAIL")} ({d2})");

    DirectoryProbe("Syphon.NET DirProbe");

    bool ok = bgra64 && bgra48;
    Log($"SUMMARY: {(ok ? "transport-ok" : "transport-failed")}");
    return ok ? 0 : 1;
}

static bool Loopback(int w, int h, out string detail)
{
    byte[] expected = Pattern(w, h);

    // Fresh server per test so a prior resolution does not leave a stale surface.
    using var server = new SyphonServer($"Syphon.NET Probe {w}x{h}");
    using SyphonClient client = server.CreateLoopbackClient();

    // Publish repeatedly and keep the most recent delivered frame; discard an initial stale one
    // by requiring a few publishes before accepting. Frames belong to the client - never dispose one.
    IOSurface.IOSurface? frame = null;
    var sw = Stopwatch.StartNew();
    int published = 0;
    while (sw.Elapsed < TimeSpan.FromSeconds(5))
    {
        server.PublishPixels(expected, w, h, CVPixelFormatType.CV32BGRA);
        published++;
        IOSurface.IOSurface? f = client.TryGetFrame();
        if (f is not null)
        {
            frame = f;
            if (published >= 4) break;
        }
        Thread.Sleep(16);
    }

    if (frame is null) { detail = "no frame delivered"; return false; }

    {
        (int gotW, int gotH) = frame.PixelSize();
        if (gotW != w || gotH != h)
        {
            detail = $"dimension {gotW}x{gotH} != {w}x{h}";
            return false;
        }

        byte[] got = new byte[w * h * 4];
        frame.CopyTightlyPacked(got);
        int mismatch = FirstMismatch(expected, got);
        if (mismatch >= 0)
        {
            using (var locked = frame.LockBytes(readOnly: true))
                Log($"    stride={locked.BytesPerRow} (tight={w * 4}), recv format 0x{frame.PixelFormat:X8}");
            Log($"    expected[0..32]: {Hex(expected, 32)}");
            Log($"    got     [0..32]: {Hex(got, 32)}");
            detail = $"mismatch at {mismatch} (expected {expected[mismatch]}, got {got[mismatch]})";
            return false;
        }

        detail = $"{w}x{h} exact incl. alpha, recv format 0x{frame.PixelFormat:X8}";
        return true;
    }
}

static byte[] SwapRedBlue(byte[] rgba)
{
    byte[] bgra = new byte[rgba.Length];
    for (int i = 0; i < rgba.Length; i += 4)
    {
        bgra[i] = rgba[i + 2];     // B <- R
        bgra[i + 1] = rgba[i + 1]; // G
        bgra[i + 2] = rgba[i];     // R <- B
        bgra[i + 3] = rgba[i + 3]; // A
    }
    return bgra;
}

static void DirectoryProbe(string name)
{
    Log("step: directory discovery (live server + pumped run loop)");
    SyphonServer server;
    try { server = new SyphonServer(name); }
    catch (DllNotFoundException) { Log("DIRECTORY: native shim missing"); return; }
    catch (PlatformNotSupportedException) { Log("DIRECTORY: no Metal device"); return; }

    using (server)
    {
        byte[] src = Pattern(32, 32);
        server.PublishPixels(src, 32, 32);

        using var directory = new SyphonServerDirectory();
        var sw = Stopwatch.StartNew();
        int count = 0;
        bool found = false;
        while (sw.Elapsed < TimeSpan.FromSeconds(6) && !found)
        {
            // Pump the run loop so distributed-notification handlers fire on this thread.
            directory.PumpEvents(TimeSpan.FromMilliseconds(200));
            IReadOnlyList<SyphonServerDescription> servers = directory.GetServers();
            count = servers.Count;
            found = servers.Any(s => s.Name == name);
        }
        Log($"DIRECTORY: {count} server(s) visible, own server {(found ? "FOUND" : "NOT found")}");
    }
}

// Publishes a deterministic BGRA pattern under a name, writing its description and a ready marker
// for a parent process to pick up. Used as the child in the cross-process test.
static int ServerMode(string[] cmdArgs)
{
    var ci = System.Globalization.CultureInfo.InvariantCulture;
    string name = Arg(cmdArgs, "--name", "Syphon.NET XProc");
    int w = int.Parse(Arg(cmdArgs, "--w", "64"), ci);
    int h = int.Parse(Arg(cmdArgs, "--h", "64"), ci);
    int seconds = int.Parse(Arg(cmdArgs, "--seconds", "30"), ci);
    string descFile = Arg(cmdArgs, "--desc-file", "");
    string readyFile = Arg(cmdArgs, "--ready-file", "");

    SyphonServer server;
    try { server = new SyphonServer(name); }
    catch (DllNotFoundException) { Log("server: native shim missing"); return 3; }
    catch (PlatformNotSupportedException) { Log("server: no Metal device"); return 2; }

    using (server)
    {
        if (descFile.Length > 0) File.WriteAllBytes(descFile, server.ExportDescription());
        if (readyFile.Length > 0) File.WriteAllText(readyFile, "ready");
        Log($"[server] publishing '{name}' {w}x{h} for {seconds}s");
        byte[] src = Pattern(w, h);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            server.PublishPixels(src, w, h, CVPixelFormatType.CV32BGRA);
            // Pump the run loop (also paces ~60fps) so the server answers directory announce
            // requests and stays discoverable by foreign clients.
            SyphonServer.PumpEvents(TimeSpan.FromMilliseconds(16));
        }
    }
    return 0;
}

// Spawns a separate server process, connects to it via its exported description (no directory),
// and verifies a published frame arrives byte-exact across the process boundary.
static int CrossTest()
{
    Log("== cross-process transport ==");
    const int w = 64, h = 64;
    string dir = Path.Combine(Path.GetTempPath(), "syphon-xtest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string descFile = Path.Combine(dir, "desc.bin");
    string readyFile = Path.Combine(dir, "ready");
    var ci = System.Globalization.CultureInfo.InvariantCulture;

    Process child = SpawnSelf("server", "--name", "Syphon.NET XProc",
        "--w", w.ToString(ci), "--h", h.ToString(ci), "--seconds", "30",
        "--desc-file", descFile, "--ready-file", readyFile);
    try
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10) && !File.Exists(readyFile) && !child.HasExited)
            Thread.Sleep(50);

        if (child.HasExited)
        {
            Log($"CROSS: server process exited early (code {child.ExitCode})");
            return child.ExitCode == 2 ? 2 : 1;
        }
        if (!File.Exists(readyFile)) { Log("CROSS: server not ready in time"); return 1; }

        byte[] desc = File.ReadAllBytes(descFile);
        Log($"CROSS: received description ({desc.Length} bytes); connecting client");

        SyphonClient client;
        try { client = SyphonClient.Connect(desc); }
        catch (PlatformNotSupportedException) { Log("CROSS: no Metal device"); return 2; }

        using (client)
        {
            byte[] expected = Pattern(w, h);
            IOSurface.IOSurface? frame = null;
            var psw = Stopwatch.StartNew();
            int got = 0;
            while (psw.Elapsed < TimeSpan.FromSeconds(8))
            {
                IOSurface.IOSurface? f = client.TryGetFrame();
                if (f is not null) { frame = f; if (++got >= 3) break; }
                Thread.Sleep(16);
            }

            if (frame is null) { Log("CROSS: no frame delivered across processes"); return 1; }
            {
                (int gotW, int gotH) = frame.PixelSize();
                if (gotW != w || gotH != h)
                {
                    Log($"CROSS: wrong dimension {gotW}x{gotH}");
                    return 1;
                }
                byte[] buf = new byte[w * h * 4];
                frame.CopyTightlyPacked(buf);
                int mm = FirstMismatch(expected, buf);
                if (mm >= 0)
                {
                    Log($"CROSS: byte mismatch at {mm} (expected {expected[mm]}, got {buf[mm]})");
                    return 1;
                }
                Log($"CROSS: PASS {w}x{h} byte-exact across separate processes");
                return 0;
            }
        }
    }
    finally
    {
        try { if (!child.HasExited) child.Kill(true); } catch (InvalidOperationException) { }
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}

// Discovers a foreign Syphon server (e.g. syphon-python) via the directory and verifies a frame
// arrives with the expected content. Dumps the first pixels so the channel mapping is visible.
static int ForeignClient(string[] cmdArgs)
{
    var ci = System.Globalization.CultureInfo.InvariantCulture;
    string name = Arg(cmdArgs, "--name", "PyForeign");
    int timeout = int.Parse(Arg(cmdArgs, "--timeout", "20"), ci);

    using var directory = new SyphonServerDirectory();
    int idx = -1;
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed < TimeSpan.FromSeconds(timeout) && idx < 0)
    {
        directory.PumpEvents(TimeSpan.FromMilliseconds(200));
        IReadOnlyList<SyphonServerDescription> servers = directory.GetServers();
        for (int i = 0; i < servers.Count; i++)
        {
            if (servers[i].Name == name) { idx = i; break; }
        }
    }
    if (idx < 0) { Log($"[cs-client] foreign server '{name}' not discovered"); return 3; }
    Log($"[cs-client] discovered '{name}' at index {idx}; connecting");

    using SyphonClient client = directory.CreateClient(idx);
    IOSurface.IOSurface? frame = null;
    var psw = Stopwatch.StartNew();
    while (psw.Elapsed < TimeSpan.FromSeconds(timeout) && frame is null)
    {
        frame = client.TryGetFrame();
        if (frame is null) directory.PumpEvents(TimeSpan.FromMilliseconds(50));
    }
    if (frame is null) { Log("[cs-client] no frame received from foreign server"); return 4; }

    {
        (int w, int h) = frame.PixelSize();
        byte[] got = new byte[w * h * 4];
        frame.CopyTightlyPacked(got);
        byte[] expected = Pattern(w, h);
        Log($"[cs-client] recv {w}x{h} format 0x{frame.PixelFormat:X8}");
        Log($"[cs-client] expected[0..8] {Hex(expected, 8)}");
        Log($"[cs-client] got     [0..8] {Hex(got, 8)}");
        if (FirstMismatch(expected, got) < 0) { Log("[cs-client] PASS byte-exact"); return 0; }
        if (FirstMismatch(SwapRedBlue(expected), got) < 0) { Log("[cs-client] PASS (R/B swapped)"); return 0; }
        Log("[cs-client] MISMATCH");
        return 1;
    }
}

static Process SpawnSelf(params string[] childArgs)
{
    string exe = Environment.ProcessPath ?? "dotnet";
    var psi = new ProcessStartInfo { UseShellExecute = false };
    if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    {
        psi.FileName = exe;
        psi.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
    }
    else
    {
        psi.FileName = exe;
    }
    foreach (string a in childArgs) psi.ArgumentList.Add(a);
    return Process.Start(psi) ?? throw new InvalidOperationException("failed to spawn child process");
}

static string Arg(string[] a, string key, string fallback)
{
    for (int i = 0; i < a.Length - 1; i++)
    {
        if (a[i] == key) return a[i + 1];
    }
    return fallback;
}

static int Unknown(string m)
{
    Log($"unknown mode: {m}");
    return 64;
}

static byte[] Pattern(int w, int h)
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

static string Hex(byte[] data, int count) =>
    string.Join(' ', data.Take(count).Select(b => b.ToString("X2")));

static int FirstMismatch(byte[] a, byte[] b)
{
    for (int i = 0; i < a.Length; i++)
    {
        if (a[i] != b[i]) return i;
    }
    return -1;
}

static void Log(string message) => Console.WriteLine(message);
