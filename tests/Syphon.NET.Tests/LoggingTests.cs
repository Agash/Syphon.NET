using Microsoft.Extensions.Logging;

namespace Syphon.NET.Tests;

/// <summary>
/// What servers, clients and directories log through the logger factory they are given: the lifecycle
/// an operator needs to follow, and the failures.
/// </summary>
[TestClass]
public sealed class LoggingTests
{
    private const int Width = 32;
    private const int Height = 16;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ServerClientAndDirectory_LogTheirLifecycle()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        CapturingLoggerFactory logs = new();
        string name = Frames.UniqueName("logging");
        using SyphonServerDirectory directory = new(logs);
        SyphonServer server = new(name, new() { LoggerFactory = logs });
        _ = await directory
            .WaitForServerAsync(s => s.Name == name, cancellationToken)
            .WaitAsync(Timeout, cancellationToken);

        server.PublishPixels(Frames.Pattern(1, Width, Height), Width, Height);
        SyphonClient client = new(server.Description, new() { LoggerFactory = logs });
        TaskCompletionSource retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ServerRetired += (_, _) => retired.TrySetResult();
        server.PublishPixels(Frames.Pattern(2, Width, Height), Width, Height);
        server.Dispose();
        await retired.Task.WaitAsync(Timeout, cancellationToken);
        client.Dispose();
        await WaitUntilAsync(() => logs.Has(92), cancellationToken);

        LogEntry started = logs.Single(50);
        Assert.AreEqual(LogLevel.Information, started.Level);
        StringAssert.Contains(started.Message, name);
        Assert.IsTrue(logs.Has(51));
        StringAssert.Contains(logs.Single(54).Message, "2 frames");
        StringAssert.Contains(logs.Single(70).Message, name);
        Assert.AreEqual(LogLevel.Information, logs.Single(72).Level);
        StringAssert.Contains(logs.Single(90).Message, name);
        Assert.IsFalse(logs.Entries.Any(static e => e.Level >= LogLevel.Warning));
    }

    [TestMethod]
    public void Client_OfAServerThatIsNotRunning_LogsIt()
    {
        CapturingLoggerFactory logs = new();
        SyphonServerDescription gone;
        using (SyphonServer server = new(Frames.UniqueName("gone")))
        {
            gone = server.Description;
        }

        using SyphonClient client = new(gone, new() { LoggerFactory = logs });
        Assert.IsFalse(client.IsValid);
        Assert.AreEqual(LogLevel.Warning, logs.Single(71).Level);
    }

    [TestMethod]
    public async Task RunAsync_LogsTheFailureItFaultsWith()
    {
        CapturingLoggerFactory logs = new();
        using SyphonServer server = new(Frames.UniqueName("logging run"));
        server.PublishPixels(Frames.Pattern(1, Width, Height), Width, Height);
        using SyphonClient client = new(server.Description, new() { LoggerFactory = logs });
        await WaitUntilAsync(() => client.HasNewFrame, TestContext.CancellationToken);

        Task run = client.RunAsync(
            static (in SyphonFrame _) => throw new InvalidDataException("handler"),
            TestContext.CancellationToken
        );
        _ = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => run);

        LogEntry failed = logs.Single(74);
        Assert.AreEqual(LogLevel.Error, failed.Level);
        Assert.IsInstanceOfType<InvalidDataException>(failed.Exception);
    }

    [TestMethod]
    public async Task Directory_LogsAFailingEventHandler_AndKeepsServing()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        CapturingLoggerFactory logs = new();
        using SyphonServerDirectory directory = new(logs);
        directory.ServerAnnounced += static (_, _) => throw new InvalidDataException("handler");
        string first = Frames.UniqueName("failing handler");
        string second = Frames.UniqueName("after the failure");
        using SyphonServer one = new(first);
        await WaitUntilAsync(() => logs.Has(95), cancellationToken);
        using SyphonServer two = new(second);
        _ = await directory
            .WaitForServerAsync(s => s.Name == second, cancellationToken)
            .WaitAsync(Timeout, cancellationToken);

        LogEntry failed = logs.Entries.First(static e => e.EventId == 95);
        Assert.AreEqual(LogLevel.Error, failed.Level);
        Assert.IsInstanceOfType<InvalidDataException>(failed.Exception);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken
    )
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.IsLessThan(deadline, DateTime.UtcNow, "timed out");
            await Task.Delay(20, cancellationToken);
        }
    }
}
