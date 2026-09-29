using CoreFoundation;

namespace Syphon.NET;

/// <summary>
/// Serves the main thread's run loop for a console or server host while its work runs. Syphon finds
/// servers through distributed notifications, which macOS delivers only through the main thread's run
/// loop: without it a <see cref="SyphonServerDirectory"/> sees no servers, and a
/// <see cref="SyphonServer"/> does not answer the directories of applications started after it. An
/// AppKit or MAUI application's main thread already serves its run loop and needs none of this.
/// </summary>
/// <example>
/// <code>
/// return SyphonMainLoop.Run(async () =>
/// {
///     using SyphonServerDirectory directory = new();
///     SyphonServerDescription obs = await directory.WaitForServerAsync(s => s.AppName == "OBS");
///     // ...
///     return 0;
/// });
/// </code>
/// </example>
public static class SyphonMainLoop
{
    /// <summary>
    /// Runs <paramref name="work"/> on the thread pool and serves the main run loop until it completes.
    /// </summary>
    /// <param name="work">The host's work.</param>
    /// <returns>What <paramref name="work"/> returned.</returns>
    /// <exception cref="InvalidOperationException">Not called on the main thread.</exception>
    public static int Run(Func<Task<int>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        RequireMainThread();
        Task<int> task = Task.Run(work);
        Serve(task);

        // Completed by Serve; this only unwraps the result or rethrows the work's exception.
        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the thread pool and serves the main run loop until it completes.
    /// </summary>
    /// <param name="work">The host's work.</param>
    /// <exception cref="InvalidOperationException">Not called on the main thread.</exception>
    public static void Run(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        RequireMainThread();
        Task task = Task.Run(work);
        Serve(task);

        // Completed by Serve; this only rethrows the work's exception.
        task.GetAwaiter().GetResult();
    }

    private static void RequireMainThread()
    {
        if (!NSThread.IsMain)
        {
            throw new InvalidOperationException(
                "The main run loop can only be served from the main thread."
            );
        }
    }

    // The work runs on the thread pool, so its awaits never resume on the main thread, which is busy
    // serving the run loop until the work completes.
    private static void Serve(Task task)
    {
        CFRunLoop main = CFRunLoop.Main;
        _ = task.ContinueWith(
            _ =>
            {
                main.Stop();
                main.WakeUp();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

        // A run loop with no source returns at once; the timer keeps each turn waiting. A turn returns
        // when the work completes (Stop) or after a second, which bounds a stop that lands just before
        // the turn starts.
        using NSTimer keepAlive = NSTimer.CreateRepeatingTimer(
            TimeSpan.FromHours(1),
            static _ => { }
        );
        NSRunLoop.Main.AddTimer(keepAlive, NSRunLoopMode.Default);
        try
        {
            while (!task.IsCompleted)
            {
                _ = main.RunInMode(CFRunLoop.ModeDefault, 1, false);
            }
        }
        finally
        {
            keepAlive.Invalidate();
        }
    }
}
