using Avalonia;
using Avalonia.Headless;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.AppModel.Avalonia.Tests;

[TestClass]
public sealed class HeadlessTestHost
{
    private static readonly HeadlessUnitTestSession Session =
        HeadlessUnitTestSession.StartNew(typeof(HeadlessTestHost));

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions()).WithInterFont();

    internal static void Run(Action action) => Session.Dispatch(action, CancellationToken.None)
        .WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

    [AssemblyCleanup]
    public static void Cleanup() => Session.Dispose();
}
