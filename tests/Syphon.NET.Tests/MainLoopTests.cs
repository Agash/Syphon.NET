namespace Syphon.NET.Tests;

// SyphonMainLoop.Run serves the main thread, which the test host's AppKit run loop already owns, so
// its real path is covered by the Native AOT smoke sample, a console host. These cover the guards.
[TestClass]
public sealed class MainLoopTests
{
    [TestMethod]
    public void Run_OffTheMainThread_Throws()
    {
        Assert.IsFalse(NSThread.IsMain);
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            SyphonMainLoop.Run(static () => Task.FromResult(0))
        );
        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            SyphonMainLoop.Run(static () => Task.CompletedTask)
        );
    }

    [TestMethod]
    public void Run_WithoutWork_Throws()
    {
        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
            SyphonMainLoop.Run((Func<Task<int>>)null!)
        );
        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
            SyphonMainLoop.Run((Func<Task>)null!)
        );
    }
}
