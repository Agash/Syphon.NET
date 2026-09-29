using System.Collections.Immutable;

namespace Syphon.NET.Tests;

[TestClass]
public sealed class DirectoryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Server_IsAnnounced_Renamed_AndRetired()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        using SyphonServerDirectory directory = new();
        string name = Frames.UniqueName("directory");
        TaskCompletionSource<SyphonServerDescription> announced = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        TaskCompletionSource<SyphonServerDescription> updated = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        TaskCompletionSource<SyphonServerDescription> retired = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        directory.ServerAnnounced += (_, s) => _ = s.Name == name && announced.TrySetResult(s);
        directory.ServerUpdated += (_, s) => _ = updated.TrySetResult(s);
        directory.ServerRetired += (_, s) => _ = retired.TrySetResult(s);

        SyphonServer server = new(name);
        SyphonServerDescription seen = await announced.Task.WaitAsync(Timeout, cancellationToken);
        Assert.AreEqual(server.Description, seen);
        Assert.AreEqual(name, seen.Name);
        Assert.IsTrue(seen.SharesIOSurfaces);
        Assert.Contains(server.Description, directory.Servers);

        string renamed = Frames.UniqueName("directory renamed");
        server.Name = renamed;
        SyphonServerDescription update = await WaitForAsync(
            updated,
            s => s.Name == renamed,
            cancellationToken
        );
        Assert.AreEqual(server.Description.Uuid, update.Uuid);
        Assert.AreEqual(
            renamed,
            directory.Servers.Single(s => s.Uuid == server.Description.Uuid).Name
        );

        server.Dispose();
        SyphonServerDescription gone = await WaitForAsync(
            retired,
            s => s.Uuid == seen.Uuid,
            cancellationToken
        );
        Assert.AreEqual(seen.Uuid, gone.Uuid);
        Assert.DoesNotContain(seen, directory.Servers);
    }

    [TestMethod]
    public async Task WaitForServerAsync_FindsListedAndLaterServers()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        using SyphonServer early = new(Frames.UniqueName("early"));
        using SyphonServerDirectory directory = new();
        SyphonServerDescription found = await directory
            .WaitForServerAsync(s => s.Name == early.Name, cancellationToken)
            .WaitAsync(Timeout, cancellationToken);
        Assert.AreEqual(early.Description, found);

        string lateName = Frames.UniqueName("late");
        Task<SyphonServerDescription> waiting = directory.WaitForServerAsync(
            s => s.Name == lateName,
            cancellationToken
        );
        Assert.IsFalse(waiting.IsCompleted);
        using SyphonServer late = new(lateName);
        Assert.AreEqual(late.Description, await waiting.WaitAsync(Timeout, cancellationToken));
    }

    [TestMethod]
    public async Task WatchAsync_StartsWithTheCurrentSet_AndFollowsChanges()
    {
        CancellationToken cancellationToken = TestContext.CancellationToken;
        using SyphonServerDirectory directory = new();
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        stop.CancelAfter(Timeout);
        string name = Frames.UniqueName("watch");
        SyphonServer? server = null;
        bool listed = false;
        try
        {
            await foreach (
                ImmutableArray<SyphonServerDescription> servers in directory.WatchAsync(stop.Token)
            )
            {
                bool present = servers.Any(s => s.Name == name);
                if (server is null)
                {
                    Assert.IsFalse(present);
                    server = new(name);
                }
                else if (present)
                {
                    listed = true;
                    server.Dispose();
                }
                else if (listed)
                {
                    break;
                }
            }
        }
        finally
        {
            server?.Dispose();
        }

        Assert.IsTrue(listed);
        Assert.IsFalse(stop.IsCancellationRequested, "the server's retirement never arrived");
    }

    [TestMethod]
    public async Task Disposal_EndsWatchers_AndRefusesUse()
    {
        SyphonServerDirectory directory = new();
        IAsyncEnumerator<ImmutableArray<SyphonServerDescription>> watcher = directory
            .WatchAsync(TestContext.CancellationToken)
            .GetAsyncEnumerator(TestContext.CancellationToken);
        Assert.IsTrue(await watcher.MoveNextAsync());
        directory.Dispose();
        directory.Dispose();
        Assert.IsFalse(
            await watcher.MoveNextAsync().AsTask().WaitAsync(Timeout, TestContext.CancellationToken)
        );
        await watcher.DisposeAsync();
        _ = Assert.ThrowsExactly<ObjectDisposedException>(directory.Refresh);
        _ = Assert.ThrowsExactly<ObjectDisposedException>(() => directory.WatchAsync());
    }

    [TestMethod]
    public async Task FrameworkServer_IsListed_AndLeavesWhenItStops()
    {
        SyphonPeer.Require();
        CancellationToken cancellationToken = TestContext.CancellationToken;
        using SyphonServerDirectory directory = new();
        string name = Frames.UniqueName("framework listed");
        SyphonServerDescription found;
        await using (
            SyphonPeer peer = await SyphonPeer.StartServerAsync(name, 32, 32, cancellationToken)
        )
        {
            found = await directory
                .WaitForServerAsync(s => s.Name == name, cancellationToken)
                .WaitAsync(Timeout, cancellationToken);
            Assert.IsTrue(found.SharesIOSurfaces);
            Assert.IsFalse(string.IsNullOrEmpty(found.Uuid));
        }

        await WaitUntilAsync(() => !directory.Servers.Contains(found), cancellationToken);
    }

    private static async Task<SyphonServerDescription> WaitForAsync(
        TaskCompletionSource<SyphonServerDescription> first,
        Func<SyphonServerDescription, bool> match,
        CancellationToken cancellationToken
    )
    {
        SyphonServerDescription seen = await first.Task.WaitAsync(Timeout, cancellationToken);
        Assert.IsTrue(match(seen), $"unexpected event for {seen}");
        return seen;
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
