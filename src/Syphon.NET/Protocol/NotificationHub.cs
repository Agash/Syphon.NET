namespace Syphon.NET.Protocol;

// Syphon's distributed notifications (announce, update, retire, announce request) for the whole
// process: the four observers are registered once, and every notification is handed to the
// subscribers here. macOS delivers distributed notifications through the main thread's run loop,
// whichever thread registered for them, so they arrive only while the main thread serves it: an
// AppKit application's does, and a console host's does inside SyphonMainLoop.Run.
internal static class NotificationHub
{
    private static readonly Lock s_gate = new();
    private static readonly List<Action<string, NSDictionary?>> s_subscribers = [];
    private static bool s_registered;

    // Receives every Syphon notification: its name and user info (a server description), on the main
    // thread.
    public static IDisposable Subscribe(Action<string, NSDictionary?> subscriber)
    {
        lock (s_gate)
        {
            Register();
            s_subscribers.Add(subscriber);
        }

        return new Subscription(subscriber);
    }

    public static void Post(string name, string? objectName, NSDictionary? userInfo) =>
        NSDistributedNotificationCenter.DefaultCenter.PostNotificationName(
            name,
            objectName,
            userInfo,
            deliverImmediately: true
        );

    private static void Register()
    {
        if (s_registered)
        {
            return;
        }

        NSDistributedNotificationCenter center = NSDistributedNotificationCenter.DefaultCenter;
        foreach (
            string name in new[]
            {
                SyphonProtocol.Announce,
                SyphonProtocol.Update,
                SyphonProtocol.Retire,
                SyphonProtocol.AnnounceRequest,
            }
        )
        {
            _ = center.AddObserver(new NSString(name), notification => Deliver(name, notification));
        }

        s_registered = true;
    }

    private static void Deliver(string name, NSNotification notification)
    {
        Action<string, NSDictionary?>[] subscribers;
        lock (s_gate)
        {
            subscribers = [.. s_subscribers];
        }

        foreach (Action<string, NSDictionary?> subscriber in subscribers)
        {
            try
            {
                subscriber(name, notification.UserInfo);
            }
            catch (Exception)
            {
                // Deliberately not rethrown: one subscriber's failure must not stop delivery to the
                // others, which serve every server and directory in the process, or unwind into the
                // main run loop. Subscribers report their own failures.
            }
        }
    }

    private sealed class Subscription(Action<string, NSDictionary?> subscriber) : IDisposable
    {
        public void Dispose()
        {
            lock (s_gate)
            {
                _ = s_subscribers.Remove(subscriber);
            }
        }
    }
}
