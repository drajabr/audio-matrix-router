using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AudioMatrixRouter.App;

public partial class App : Application
{
    private MainWindow? _mainWindow;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _startupItem;
    private bool _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the window with ✕ hides to tray; only the tray menu (or applying
            // an update) really quits — so the app must outlive its last window.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // The controller (engine autostart, config) initializes inside MainWindow's
            // constructor, so the window is always constructed — even for a --startup
            // launch that stays hidden in the tray.
            _mainWindow = new MainWindow();
            _mainWindow.QuitRequested += () => Quit(desktop);

            SetupTrayIcon(desktop);

            if (!Program.StartMinimized)
            {
                // Setting MainWindow lets the lifetime show it after initialization.
                // For a tray launch we deliberately leave it unset and unshown.
                desktop.MainWindow = _mainWindow;
            }

            desktop.ShutdownRequested += (_, _) => _mainWindow?.PrepareShutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Restore + foreground the main window (tray menu, second instance signal).</summary>
    public void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _trayIcon = new TrayIcon { ToolTipText = "Audio Matrix Router" };

        // The csproj stamps ..\AudioMatrixRouter\app.ico as ApplicationIcon; published
        // builds carry app.ico beside the exe. If it is not there (e.g. plain dev build)
        // we simply skip the image — tooltip + menu still work.
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(icoPath))
                _trayIcon.Icon = new WindowIcon(icoPath);
        }
        catch
        {
            // Icon decode failure must never block startup.
        }

        var showItem = new NativeMenuItem("Show");
        showItem.Click += (_, _) => ShowMainWindow();

        _startupItem = new NativeMenuItem("Start with Windows")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _mainWindow?.StartupAtBoot ?? false,
        };
        _startupItem.Click += (_, _) =>
        {
            if (_mainWindow is not null && _startupItem is not null)
                _startupItem.IsChecked = _mainWindow.ToggleStartupAtBoot();
        };

        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (_, _) => Quit(desktop);

        var menu = new NativeMenu();
        menu.Items.Add(showItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quitItem);
        _trayIcon.Menu = menu;

        _trayIcon.Clicked += (_, _) => ShowMainWindow();
        _trayIcon.IsVisible = true;

        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private void Quit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_quitting) return;
        _quitting = true;

        try { _trayIcon?.Dispose(); }
        catch { /* tray teardown is best-effort */ }

        _mainWindow?.PrepareShutdown();
        desktop.Shutdown();
    }
}
