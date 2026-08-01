using Avalonia;

namespace AudioMatrixRouter.App;

internal static class Program
{
    // Scaffold note: the single-instance mutex, ShowWindow event, Velopack bootstrap,
    // timeBeginPeriod and priority setup port here from the WinForms Program.cs when
    // this app takes over as the shipping host (see docs/AVALONIA-MIGRATION.md §2.5).
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
