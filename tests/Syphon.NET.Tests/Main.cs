using Microsoft.Testing.Platform.Builder;

namespace Syphon.NET.Tests;

// The Apple SDK does not generate an entry point for test modules on this target, because an app
// here is expected to start AppKit itself. That is not boilerplate we can skip: the library binds
// IOSurface/CoreVideo/Metal and the server directory rides on distributed notifications, so the
// tests need a real NSApplication and its main run loop. Running the module as a bare managed dll
// starts neither, and the IOSurface tests fail with zero-sized surfaces.
//
// The extension registrations still come from the platform: AddSelfRegisteredExtensions is
// generated from the same hook items the generated entry point would have used, so the runner,
// the code-coverage collector and anything added later stay wired up without being named here.
internal static class Program
{
    private static int Main()
    {
        NSApplication.Init();

        NSApplication application = NSApplication.SharedApplication;
        application.Delegate = new TestRunnerDelegate();
        application.Run();

        // NSApplication.Run does not return; the delegate exits the process with the run's result.
        return 0;
    }
}

[Register("TestRunnerDelegate")]
internal sealed class TestRunnerDelegate : NSApplicationDelegate
{
    public override void DidFinishLaunching(NSNotification notification) =>
        _ = Task.Run(RunTestsAsync);

    private static async Task RunTestsAsync()
    {
        try
        {
            // dotnet test passes the platform's protocol configuration (the server type and the
            // test host's pipe) on the command line, so the run has to forward them. Element 0 is
            // the executable path rather than an argument.
            string[] commandLine = Environment.GetCommandLineArgs();
            string[] arguments = commandLine.Length > 1 ? commandLine[1..] : [];

            ITestApplicationBuilder builder = await TestApplication.CreateBuilderAsync(arguments).ConfigureAwait(false);
            builder.AddSelfRegisteredExtensions(arguments);

            using ITestApplication application = await builder.BuildAsync().ConfigureAwait(false);
            Environment.Exit(await application.RunAsync().ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            // Last resort: nothing above this frame can report a failure once the run loop owns the
            // process, and an unobserved exception here would leave the runner hanging silently.
            Console.Error.WriteLine($"The test run failed to start: {ex}");
            Environment.Exit(1);
        }
    }
}
