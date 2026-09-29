using CoreFoundation;

namespace Syphon.NET.Protocol;

// Syphon's point-to-point messages: CFMessagePort requests whose message id is the message type and
// whose data is the payload archived with NSKeyedArchiver (SyphonCFMessageSender.m,
// SyphonCFMessageReceiver.m). Every server and client owns a local port named after its UUID and
// sends to the other side's port by name.
internal static class MessageArchive
{
    // Payloads are strings (client names, server names) and numbers (surface IDs); nothing else is
    // accepted from another process.
    private static readonly Type[] s_allowed = [typeof(NSString), typeof(NSNumber)];

    public static NSData? Encode(NSObject? payload)
    {
        if (payload is null)
        {
            return null;
        }

        NSData? data = NSKeyedArchiver.GetArchivedData(payload, true, out NSError? error);
        return data
            ?? throw new SyphonException(
                $"A Syphon message could not be archived: {error?.LocalizedDescription}"
            );
    }

    public static NSObject? Decode(NSData? data) =>
        data is null || data.Length == 0
            ? null
            : NSKeyedUnarchiver.GetUnarchivedObject(s_allowed, data, out _);
}

// A local port that receives messages on one serial dispatch queue shared by every receiver in the
// process, as the SDK's "info.v002.syphon.messaging" queue is.
internal sealed class MessageReceiver : IDisposable
{
    private static readonly DispatchQueue s_queue = new(
        "info.v002.syphon.messaging",
        concurrent: false
    );

    private readonly CFMessagePort _port;

    // Held for as long as the port: the port calls back into it.
    private readonly CFMessagePort.CFMessagePortCallBack _callback;

    private MessageReceiver(CFMessagePort port, CFMessagePort.CFMessagePortCallBack callback)
    {
        _port = port;
        _callback = callback;
    }

    public static MessageReceiver? TryCreate(string name, Action<uint, NSObject?> handler)
    {
        NSData Receive(int type, NSData data)
        {
            handler((uint)type, MessageArchive.Decode(data));
            return null!;
        }

        CFMessagePort.CFMessagePortCallBack callback = Receive;
        CFMessagePort? port = CFMessagePort.CreateLocalPort(name, callback, null);
        if (port is null)
        {
            return null;
        }

        port.SetDispatchQueue(s_queue);
        return new MessageReceiver(port, callback);
    }

    public void Dispose()
    {
        GC.KeepAlive(_callback);
        _port.Invalidate();
        _port.Dispose();
    }
}

// A remote port that sends messages in order on a serial queue of its own, so a slow receiver never
// blocks the sender's thread (the SDK's dispatch source per sender). A send that finds the port
// invalid, because the other side went away, marks the sender invalid and calls the handler.
internal sealed class MessageSender : IDisposable
{
    // The SDK's send timeout; there is no reply to wait for.
    private const double SendTimeout = 60;

    // A message without a payload carries empty data; the receiver treats empty and absent alike.
    private static readonly NSData s_empty = new();

    private readonly CFMessagePort _port;
    private readonly DispatchQueue _queue;
    private readonly Action? _invalidated;
    private volatile bool _valid = true;

    private MessageSender(CFMessagePort port, string name, Action? invalidated)
    {
        _port = port;
        _queue = new DispatchQueue($"info.v002.syphon.send.{name}", concurrent: false);
        _invalidated = invalidated;
    }

    public bool IsValid => _valid;

    public static MessageSender? TryCreate(string name, Action? invalidated = null)
    {
        CFMessagePort? port = CFMessagePort.CreateRemotePort(null, name);
        return port is null ? null : new MessageSender(port, name, invalidated);
    }

    public void Send(uint type, NSObject? payload)
    {
        NSData? data = MessageArchive.Encode(payload);
        _queue.DispatchAsync(() =>
        {
            if (!_valid)
            {
                return;
            }

            CFMessagePortSendRequestStatus status = _port.SendRequest(
                (int)type,
                data ?? s_empty,
                SendTimeout,
                0,
                null!,
                out _
            );
            if (status is CFMessagePortSendRequestStatus.IsInvalid)
            {
                _valid = false;

                // Off this queue: the handler takes its owner's lock, and the owner may be waiting on
                // this queue while holding it (a last message before disposal).
                if (_invalidated is { } invalidated)
                {
                    _ = ThreadPool.UnsafeQueueUserWorkItem(
                        static handler => handler(),
                        invalidated,
                        preferLocal: false
                    );
                }
            }
        });
    }

    // Sends and waits until the message has left, for a last message before the sender goes away.
    public void SendAndFlush(uint type, NSObject? payload)
    {
        Send(type, payload);
        _queue.DispatchSync(static () => { });
    }

    public void Dispose()
    {
        _queue.DispatchSync(() =>
        {
            _valid = false;
            _port.Invalidate();
            _port.Dispose();
        });
        _queue.Dispose();
    }
}
