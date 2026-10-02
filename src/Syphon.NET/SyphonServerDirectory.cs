using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CoreFoundation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Syphon.NET.Protocol;

namespace Syphon.NET;

/// <summary>
/// The Syphon servers on this machine, kept current from the announcements servers make. The
/// announcements arrive through the main thread's run loop, which an AppKit or MAUI application serves
/// and a console host serves with <see cref="SyphonMainLoop"/>.
/// </summary>
/// <remarks>
/// As the Syphon framework's directory does, it checks that the servers it lists are alive whenever
/// any application asks servers to announce themselves, and drops those that do not answer.
/// </remarks>
public sealed partial class SyphonServerDirectory : IDisposable
{
    private readonly ILogger<SyphonServerDirectory> _logger;
    private readonly Lock _gate = new();
    private readonly IDisposable _subscription;
    private readonly List<Channel<ImmutableArray<SyphonServerDescription>>> _watchers = [];
    private ImmutableArray<SyphonServerDescription> _servers = [];
    private HashSet<string>? _pings;
    private bool _disposed;
    private readonly TimeProvider _time;

    /// <summary>Starts listening and asks every running server to announce itself.</summary>
    /// <param name="loggerFactory">Where the directory logs servers coming and going.</param>
    /// <param name="timeProvider">The clock announce timeouts run on; the system's when null.</param>
    public SyphonServerDirectory(
        ILoggerFactory? loggerFactory = null,
        TimeProvider? timeProvider = null
    )
    {
        _time = timeProvider ?? TimeProvider.System;
        _logger = (
            loggerFactory ?? NullLoggerFactory.Instance
        ).CreateLogger<SyphonServerDirectory>();
        _subscription = NotificationHub.Subscribe(OnNotification);
        Refresh();
    }

    /// <summary>A server announced itself, on the main thread.</summary>
    public event EventHandler<SyphonServerDescription>? ServerAnnounced;

    /// <summary>A server changed its description (its name), on the main thread.</summary>
    public event EventHandler<SyphonServerDescription>? ServerUpdated;

    /// <summary>A server stopped, on the main thread.</summary>
    public event EventHandler<SyphonServerDescription>? ServerRetired;

    /// <summary>The servers known now.</summary>
    public ImmutableArray<SyphonServerDescription> Servers
    {
        get
        {
            lock (_gate)
            {
                return _servers;
            }
        }
    }

    /// <summary>Asks every running server to announce itself, which also checks the listed ones are alive.</summary>
    public void Refresh()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NotificationHub.Post(SyphonProtocol.AnnounceRequest, null, null);
    }

    /// <summary>The servers each time the set changes; the first result is the current set.</summary>
    /// <param name="cancellationToken">Ends the sequence.</param>
    /// <returns>The servers, each time they change.</returns>
    public IAsyncEnumerable<ImmutableArray<SyphonServerDescription>> WatchAsync(
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Channel<ImmutableArray<SyphonServerDescription>> channel = Channel.CreateBounded<
            ImmutableArray<SyphonServerDescription>
        >(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate)
        {
            _watchers.Add(channel);
            _ = channel.Writer.TryWrite(_servers);
        }

        return Watch(channel, cancellationToken);
    }

    /// <summary>Waits for a server to be announced.</summary>
    /// <param name="match">Which server.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <returns>The first server that matches, already listed or announced later.</returns>
    public async Task<SyphonServerDescription> WaitForServerAsync(
        Func<SyphonServerDescription, bool> match,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(match);
        await foreach (
            ImmutableArray<SyphonServerDescription> servers in WatchAsync(cancellationToken)
                .ConfigureAwait(false)
        )
        {
            foreach (SyphonServerDescription server in servers)
            {
                if (match(server))
                {
                    return server;
                }
            }
        }

        throw new OperationCanceledException(cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (Channel<ImmutableArray<SyphonServerDescription>> watcher in _watchers)
            {
                _ = watcher.Writer.TryComplete();
            }

            _watchers.Clear();
        }

        _subscription.Dispose();
    }

    private async IAsyncEnumerable<ImmutableArray<SyphonServerDescription>> Watch(
        Channel<ImmutableArray<SyphonServerDescription>> channel,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        try
        {
            await foreach (
                ImmutableArray<SyphonServerDescription> servers in channel
                    .Reader.ReadAllAsync(cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                yield return servers;
            }
        }
        finally
        {
            lock (_gate)
            {
                _ = _watchers.Remove(channel);
            }
        }
    }

    private void OnNotification(string name, NSDictionary? userInfo)
    {
        try
        {
            Handle(name, userInfo);
        }
        catch (Exception error)
        {
            // Not rethrown: the notification arrives on the main run loop, which must keep serving
            // every other server and directory. The failure is an application's event handler or a
            // malformed announcement from another process.
            LogNotificationFailed(error, name);
        }
    }

    private void Handle(string name, NSDictionary? userInfo)
    {
        if (name == SyphonProtocol.AnnounceRequest)
        {
            StartPing();
            return;
        }

        if (userInfo is null)
        {
            LogMalformed(name);
            return;
        }

        SyphonServerDescription server = new(userInfo);
        if (server.Uuid.Length == 0)
        {
            LogMalformed(name);
            return;
        }

        switch (name)
        {
            case SyphonProtocol.Announce:
                bool added;
                lock (_gate)
                {
                    _ = _pings?.Add(server.Uuid);
                    added = !_servers.Contains(server);
                    if (added)
                    {
                        Changed(_servers.Add(server));
                    }
                }

                if (added)
                {
                    LogAnnounced(server.Name, server.AppName, server.Uuid);
                    ServerAnnounced?.Invoke(this, server);
                }

                break;
            case SyphonProtocol.Update:
                bool updated;
                lock (_gate)
                {
                    int index = _servers.IndexOf(server);
                    updated = index >= 0;
                    if (updated)
                    {
                        Changed(_servers.SetItem(index, server));
                    }
                }

                if (updated)
                {
                    LogUpdated(server.Name, server.Uuid);
                    ServerUpdated?.Invoke(this, server);
                }

                break;
            case SyphonProtocol.Retire:
                Retire([server]);
                break;
            default:
                break;
        }
    }

    // Any application's announce request is a liveness check: servers answer with an announce, and
    // those that have not answered by the timeout are gone (SyphonServerDirectory handleAnnounceRequest).
    private void StartPing()
    {
        lock (_gate)
        {
            if (_pings is not null || _disposed)
            {
                return;
            }

            _pings = [with(StringComparer.Ordinal)];
        }

        _ = Task.Delay(SyphonProtocol.AnnounceTimeout, _time)
            .ContinueWith(
                _ =>
                {
                    SyphonServerDescription[] silent;
                    lock (_gate)
                    {
                        HashSet<string> answered = _pings!;
                        _pings = null;
                        silent = [.. _servers.Where(s => !answered.Contains(s.Uuid))];
                    }

                    if (silent.Length == 0)
                    {
                        return;
                    }

                    foreach (SyphonServerDescription server in silent)
                    {
                        LogSilent(server.Name, server.Uuid);
                    }

                    // On the main thread, where the directory's other events are raised.
                    DispatchQueue.MainQueue.DispatchAsync(() => OnMain(() => Retire(silent)));
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
    }

    private void Retire(IReadOnlyCollection<SyphonServerDescription> servers)
    {
        List<SyphonServerDescription> removed = [];
        lock (_gate)
        {
            ImmutableArray<SyphonServerDescription> remaining = _servers;
            foreach (SyphonServerDescription server in servers)
            {
                int index = remaining.IndexOf(server);
                if (index >= 0)
                {
                    removed.Add(remaining[index]);
                    remaining = remaining.RemoveAt(index);
                }
            }

            if (removed.Count > 0)
            {
                Changed(remaining);
            }
        }

        foreach (SyphonServerDescription server in removed)
        {
            LogRetired(server.Name, server.Uuid);
            ServerRetired?.Invoke(this, server);
        }
    }

    private void OnMain(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            // Not rethrown, for the reason OnNotification gives.
            LogNotificationFailed(error, SyphonProtocol.Retire);
        }
    }

    [LoggerMessage(
        EventId = 90,
        Level = LogLevel.Debug,
        Message = "Syphon server \"{Name}\" of {AppName} announced ({Uuid})"
    )]
    private partial void LogAnnounced(string name, string appName, string uuid);

    [LoggerMessage(
        EventId = 91,
        Level = LogLevel.Debug,
        Message = "Syphon server {Uuid} is now \"{Name}\""
    )]
    private partial void LogUpdated(string name, string uuid);

    [LoggerMessage(
        EventId = 92,
        Level = LogLevel.Debug,
        Message = "Syphon server \"{Name}\" retired ({Uuid})"
    )]
    private partial void LogRetired(string name, string uuid);

    [LoggerMessage(
        EventId = 93,
        Level = LogLevel.Information,
        Message = "Syphon server \"{Name}\" did not answer an announce request and is dropped ({Uuid})"
    )]
    private partial void LogSilent(string name, string uuid);

    [LoggerMessage(
        EventId = 94,
        Level = LogLevel.Warning,
        Message = "Ignored a Syphon {Notification} notification without a server description"
    )]
    private partial void LogMalformed(string notification);

    [LoggerMessage(
        EventId = 95,
        Level = LogLevel.Error,
        Message = "Handling a Syphon {Notification} notification failed"
    )]
    private partial void LogNotificationFailed(Exception error, string notification);

    // Called holding the lock.
    private void Changed(ImmutableArray<SyphonServerDescription> servers)
    {
        _servers = servers;
        foreach (Channel<ImmutableArray<SyphonServerDescription>> watcher in _watchers)
        {
            _ = watcher.Writer.TryWrite(servers);
        }
    }
}
