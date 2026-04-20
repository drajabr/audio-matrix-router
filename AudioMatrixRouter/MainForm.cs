using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using AudioMatrixRouter.Audio;
using AudioMatrixRouter.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NAudio.CoreAudioApi;

namespace AudioMatrixRouter;

public sealed class MainForm : Form
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    // COLORREF format: 0x00bbggrr
    private const int DARK_CAPTION_COLOR = 0x001d1a17;
    private const int LIGHT_TEXT_COLOR = 0x00f0f0f0;

    private readonly AudioEngine _engine = new();
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _deviceRefreshTimer = new() { Interval = 250 };
    private readonly NotifyIcon _trayIcon = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly Icon _trayAppIcon;

    private bool _locked;
    private bool _allowRealClose;
    private readonly bool _forceStartMinimized;
    private bool _startMinimizedFromConfig;
    private string _uiPreferencesJson = "";
    private const string StartupRunEntryName = "AudioMatrixRouter";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MainForm(bool forceStartMinimized = false)
    {
        _forceStartMinimized = forceStartMinimized;
        Text = "Audio Router Matrix";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new System.Drawing.Size(1100, 750);
        Size = new System.Drawing.Size(1600, 1000);
        _trayAppIcon = LoadAppIcon();
        try
        {
            Icon = (Icon)_trayAppIcon.Clone();
        }
        catch
        {
            // Keep default icon if extraction fails.
        }

        InitializeTrayIcon();
        Controls.Add(_webView);

        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveConfig();
        };

        _deviceRefreshTimer.Tick += async (_, _) =>
        {
            _deviceRefreshTimer.Stop();
            SyncDevicesWithSystem();
            await PushStateToUiAsync();
        };

        _engine.Init();
        _engine.DevicesChanged += () =>
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(() => _deviceRefreshTimer.Start());
            }
            else
            {
                _deviceRefreshTimer.Start();
            }
        };

        LoadConfigAndDevices();

        Shown += async (_, _) =>
        {
            ApplyDarkTitleBar();
            _trayIcon.Visible = true;
            await InitializeWebViewAsync();
            await PushStateToUiAsync();

            if (_forceStartMinimized || _startMinimizedFromConfig)
            {
                BeginInvoke(() => MinimizeToTray(showBalloon: false));
            }
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDarkTitleBar();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowRealClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            MinimizeToTray(showBalloon: true);
            SaveConfig();
            return;
        }

        SaveConfig();
        _engine.Stop();
        _engine.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayMenu.Dispose();
        _trayAppIcon.Dispose();
        base.OnFormClosing(e);
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            using var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("AudioMatrixRouter.app.ico");
            if (iconStream != null)
            {
                return new Icon(iconStream);
            }
        }
        catch
        {
            // Fallback below.
        }

        try
        {
            var extracted = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (extracted != null)
            {
                return (Icon)extracted.Clone();
            }
        }
        catch
        {
            // Fallback below.
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private void InitializeTrayIcon()
    {
        _trayMenu.Items.Add("Show", null, (_, _) => RestoreFromTray());
        _trayMenu.Items.Add("Quit", null, (_, _) => QuitFromTray());

        _trayIcon.Text = "Audio Router Matrix";
        _trayIcon.Icon = _trayAppIcon;
        _trayIcon.Visible = true;
        _trayIcon.ContextMenuStrip = _trayMenu;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        _trayIcon.Visible = true;
        Show();
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Activate();
        ScheduleSave();
    }

    private void MinimizeToTray(bool showBalloon)
    {
        Hide();
        ShowInTaskbar = false;

        if (showBalloon && _trayIcon.Visible)
        {
            _trayIcon.BalloonTipTitle = "Audio Router Matrix";
            _trayIcon.BalloonTipText = "Still running in system tray.";
            _trayIcon.ShowBalloonTip(1200);
        }
    }

    private void QuitFromTray()
    {
        _allowRealClose = true;
        Close();
    }

    private async Task InitializeWebViewAsync()
    {
        var envOptions = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = string.Join(" ",
                "--disable-renderer-backgrounding",
                "--disable-background-timer-throttling",
                "--disable-backgrounding-occluded-windows",
                "--enable-gpu-rasterization",
                "--enable-zero-copy")
        };

        var webViewEnv = await CoreWebView2Environment.CreateAsync(options: envOptions);
        await _webView.EnsureCoreWebView2Async(webViewEnv);

        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
        _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        _webView.CoreWebView2.NavigationStarting += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[WebView] Navigation starting: {e.Uri}");
            if (e.Uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                _webView.Source = new Uri("https://appassets.local/index.html");
                System.Diagnostics.Debug.WriteLine("[WebView] Blocked file:// navigation and redirected to virtual host");
            }
        };

        _webView.CoreWebView2.NavigationCompleted += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[WebView] Navigation completed: IsSuccess={e.IsSuccess}");
            if (!e.IsSuccess)
            {
                System.Diagnostics.Debug.WriteLine($"[WebView] Navigation error: {e.WebErrorStatus}");
            }
        };

        var uiDistPath = ResolveUiDistPath();
        if (uiDistPath == null)
        {
            var errorHtml = "<html><body style='background:#111;color:#eee;font-family:Segoe UI;padding:16px;'><h2>Web UI Not Found</h2><p>Expected WebUI/dist beside executable or in project path.</p></body></html>";
            _webView.CoreWebView2.NavigateToString(errorHtml);
            return;
        }

        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "appassets.local",
            uiDistPath,
            CoreWebView2HostResourceAccessKind.Allow);

        _webView.Source = new Uri("https://appassets.local/index.html");
        System.Diagnostics.Debug.WriteLine("[WebView] Navigating to https://appassets.local/index.html");
    }

    private static string? ResolveUiDistPath()
    {
        var candidatePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "WebUI", "dist"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "WebUI", "dist"))
        };

        foreach (var path in candidatePaths)
        {
            if (Directory.Exists(path) && File.Exists(Path.Combine(path, "index.html")))
            {
                return path;
            }
        }

        return null;
    }

    private void LoadConfigAndDevices()
    {
        var loadedConfig = AppConfig.Load();
        if (loadedConfig != null)
        {
            loadedConfig.ApplyToEngine(_engine);
            _locked = loadedConfig.Locked;
            _uiPreferencesJson = loadedConfig.UiPreferencesJson ?? "";

            if (loadedConfig.Window.Width > 0 && loadedConfig.Window.Height > 0)
            {
                Size = new System.Drawing.Size(loadedConfig.Window.Width, loadedConfig.Window.Height);
                if (loadedConfig.Window.X >= 0)
                {
                    Location = new System.Drawing.Point(loadedConfig.Window.X, loadedConfig.Window.Y);
                }
            }

            _startMinimizedFromConfig = loadedConfig.Window.StartMinimized;

            SyncDevicesWithSystem(addAllAvailableIfEmpty: false);

            // First launch fallback: if saved config has no valid devices on this machine, bootstrap with current active devices.
            if (_engine.InputDevices.Count == 0 || _engine.OutputDevices.Count == 0)
            {
                SyncDevicesWithSystem(addAllAvailableIfEmpty: true);
            }

            return;
        }

        SyncDevicesWithSystem(addAllAvailableIfEmpty: true);
    }

    private void SyncDevicesWithSystem(bool addAllAvailableIfEmpty = false)
    {
        _engine.RefreshDevices();

        if (addAllAvailableIfEmpty && _engine.InputDevices.Count == 0)
        {
            var captureDevices = _engine.GetAvailableDevices(DataFlow.Capture);
            foreach (var device in captureDevices)
            {
                _engine.AddInputDevice(device.Id);
            }
        }

        if (addAllAvailableIfEmpty && _engine.OutputDevices.Count == 0)
        {
            var renderDevices = _engine.GetAvailableDevices(DataFlow.Render);
            foreach (var device in renderDevices)
            {
                _engine.AddOutputDevice(device.Id);
            }
        }
    }

    private void SaveConfig()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            bounds = Bounds;
        }

        var startMinimized = WindowState == FormWindowState.Minimized || !Visible || !ShowInTaskbar;
        var config = AppConfig.FromEngine(_engine, bounds.X, bounds.Y, bounds.Width, bounds.Height, _locked, startMinimized, _uiPreferencesJson);
        config.Save();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void ApplyDarkTitleBar()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) || !IsHandleCreated)
        {
            return;
        }

        try
        {
            var darkModeEnabled =
                TrySetDwmIntAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE, 1) ||
                TrySetDwmIntAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20, 1);

            if (darkModeEnabled)
            {
                TrySetDwmIntAttribute(DWMWA_CAPTION_COLOR, DARK_CAPTION_COLOR);
                TrySetDwmIntAttribute(DWMWA_TEXT_COLOR, LIGHT_TEXT_COLOR);
            }
        }
        catch
        {
            // Leave default title bar rendering if the DWM attribute is unavailable.
        }
    }

    private bool TrySetDwmIntAttribute(int attribute, int value)
    {
        var attributeValue = value;
        return DwmSetWindowAttribute(Handle, attribute, ref attributeValue, sizeof(int)) == 0;
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_webView.CoreWebView2 == null) return;

        try
        {
            var payload = e.TryGetWebMessageAsString();
            var request = JsonSerializer.Deserialize<UiRequest>(payload, JsonOptions);
            if (request == null || string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Method))
            {
                return;
            }

            switch (request.Method)
            {
                case "getState":
                    await SendResultAsync(request.Id, BuildUiState());
                    return;

                case "refreshDevices":
                    SyncDevicesWithSystem(addAllAvailableIfEmpty: false);
                    await SendResultAsync(request.Id, BuildUiState());
                    return;

                case "addInputDevice":
                    if (request.Params.TryGetProperty("deviceId", out var addInputId))
                    {
                        _engine.AddInputDevice(addInputId.GetString() ?? string.Empty);
                        ScheduleSave();
                    }
                    await SendResultAsync(request.Id, BuildUiState());
                    return;

                case "addOutputDevice":
                    if (request.Params.TryGetProperty("deviceId", out var addOutputId))
                    {
                        _engine.AddOutputDevice(addOutputId.GetString() ?? string.Empty);
                        ScheduleSave();
                    }
                    await SendResultAsync(request.Id, BuildUiState());
                    return;

                case "removeInputDevice":
                    if (request.Params.TryGetProperty("deviceId", out var removeInputId))
                    {
                        _engine.RemoveInputDevice(removeInputId.GetString() ?? string.Empty);
                        ScheduleSave();
                    }
                    await SendResultAsync(request.Id, BuildUiState());
                    return;

                case "removeOutputDevice":
                    if (request.Params.TryGetProperty("deviceId", out var removeOutputId))
                    {
                        _engine.RemoveOutputDevice(removeOutputId.GetString() ?? string.Empty);
                        ScheduleSave();
                    }
                    await SendResultAsync(request.Id, BuildUiState());
                    return;

                case "startEngine":
                    _engine.Start();
                    await SendResultAsync(request.Id, BuildUiState());
                    return;

                case "stopEngine":
                    _engine.Stop();
                    await SendResultAsync(request.Id, BuildUiState());
                    return;

                case "setLocked":
                    _locked = request.Params.TryGetProperty("locked", out var lockValue) && lockValue.GetBoolean();
                    ScheduleSave();
                    await SendResultAsync(request.Id, BuildUiState());
                    return;

                case "getUiPreferences":
                    await SendResultAsync(request.Id, _uiPreferencesJson);
                    return;

                case "setUiPreferences":
                    _uiPreferencesJson = request.Params.TryGetProperty("json", out var uiJsonValue)
                        ? uiJsonValue.GetString() ?? string.Empty
                        : string.Empty;
                    ScheduleSave();
                    await SendResultAsync(request.Id, true);
                    return;

                case "clearRoutes":
                    if (!_locked)
                    {
                        _engine.ClearCrosspoints();
                        ScheduleSave();
                    }
                    await SendResultAsync(request.Id, BuildUiState());
                    return;

                case "setCrosspoint":
                {
                    int inCh = request.Params.TryGetProperty("inCh", out var inElem) ? inElem.GetInt32() : -1;
                    int outCh = request.Params.TryGetProperty("outCh", out var outElem) ? outElem.GetInt32() : -1;
                    bool active = request.Params.TryGetProperty("active", out var activeElem) && activeElem.GetBoolean();
                    float gainDb = request.Params.TryGetProperty("gainDb", out var gainElem) ? gainElem.GetSingle() : 0f;

                    if (!_locked)
                    {
                        _engine.SetCrosspoint(inCh, outCh, active, gainDb);

                        if (active && !_engine.IsRunning)
                        {
                            _engine.Start();
                        }
                        else if (!active && _engine.IsRunning && !_engine.RoutingMatrix.HasAnyCrosspoints())
                        {
                            _engine.Stop();
                        }

                        ScheduleSave();
                    }

                    await SendResultAsync(request.Id, BuildUiState());
                    return;
                }

                case "setInputMasterDevice":
                    if (request.Params.TryGetProperty("deviceId", out var inputMasterId))
                    {
                        _engine.SetInputMasterDevice(inputMasterId.GetString() ?? string.Empty);
                        ScheduleSave();
                    }
                    await SendResultAsync(request.Id, BuildUiState());
                    return;

                case "setOutputMasterDevice":
                    if (request.Params.TryGetProperty("deviceId", out var outputMasterId))
                    {
                        _engine.SetOutputMasterDevice(outputMasterId.GetString() ?? string.Empty);
                        ScheduleSave();
                    }
                    await SendResultAsync(request.Id, BuildUiState());
                    return;

                case "getStartupAtBoot":
                    await SendResultAsync(request.Id, IsStartupAtBootEnabled());
                    return;

                case "setStartupAtBoot":
                {
                    var enabled = request.Params.TryGetProperty("enabled", out var startupEnabled) && startupEnabled.GetBoolean();
                    var applied = SetStartupAtBoot(enabled);
                    await SendResultAsync(request.Id, applied && IsStartupAtBootEnabled());
                    return;
                }

                case "quitApplication":
                    await SendResultAsync(request.Id, true);
                    BeginInvoke(() =>
                    {
                        _allowRealClose = true;
                        Close();
                    });
                    return;

                default:
                    await SendErrorAsync(request.Id, $"Unknown method: {request.Method}");
                    return;
            }
        }
        catch (Exception ex)
        {
            var safeId = Guid.NewGuid().ToString("N");
            await SendErrorAsync(safeId, ex.Message);
        }
    }

    private UiState BuildUiState()
    {
        var routes = new List<RouteState>();
        var matrix = _engine.RoutingMatrix;
        for (int inCh = 0; inCh < matrix.InputChannels; inCh++)
        {
            for (int outCh = 0; outCh < matrix.OutputChannels; outCh++)
            {
                var cp = matrix.GetCrosspoint(inCh, outCh);
                if (!cp.Active) continue;
                routes.Add(new RouteState
                {
                    InCh = inCh,
                    OutCh = outCh,
                    GainDb = matrix.GetGainDb(inCh, outCh)
                });
            }
        }

        return new UiState
        {
            Running = _engine.IsRunning,
            Locked = _locked,
            StartupAtBoot = IsStartupAtBootEnabled(),
            AvailableInputs = _engine.GetAvailableDevices(DataFlow.Capture).Select(d => new DeviceState
            {
                DeviceId = d.Id,
                Label = d.Name,
                Channels = d.Channels,
                Offset = 0,
                IsMaster = false
            }).ToList(),
            AvailableOutputs = _engine.GetAvailableDevices(DataFlow.Render).Select(d => new DeviceState
            {
                DeviceId = d.Id,
                Label = d.Name,
                Channels = d.Channels,
                Offset = 0,
                IsMaster = false
            }).ToList(),
            Inputs = _engine.InputDevices.Select(d => new DeviceState
            {
                DeviceId = d.Info.Id,
                Label = d.Info.Name,
                Channels = d.Info.Channels,
                Offset = d.GlobalChannelOffset,
                IsMaster = d.IsMasterDevice
            }).ToList(),
            Outputs = _engine.OutputDevices.Select(d => new DeviceState
            {
                DeviceId = d.Info.Id,
                Label = d.Info.Name,
                Channels = d.Info.Channels,
                Offset = d.GlobalChannelOffset,
                IsMaster = d.IsMasterDevice
            }).ToList(),
            Routes = routes
        };
    }

    private bool IsStartupAtBootEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            var value = key?.GetValue(StartupRunEntryName) as string;
            if (string.IsNullOrWhiteSpace(value)) return false;

            var exePath = Path.GetFullPath(Application.ExecutablePath).Trim().Trim('"');
            return value.Contains(exePath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool SetStartupAtBoot(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
                ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");

            if (key == null) return false;

            if (enabled)
            {
                key.SetValue(StartupRunEntryName, $"\"{Application.ExecutablePath}\" --startup");
            }
            else
            {
                key.DeleteValue(StartupRunEntryName, false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task PushStateToUiAsync()
    {
        if (_webView.CoreWebView2 == null) return;

        var stateJson = JsonSerializer.Serialize(BuildUiState(), JsonOptions);
        var script = $"window.dispatchEvent(new CustomEvent('native-state', {{ detail: {stateJson} }}));";
        await _webView.CoreWebView2.ExecuteScriptAsync(script);
    }

    private async Task SendResultAsync(string requestId, object result)
    {
        if (_webView.CoreWebView2 == null) return;

        var idJson = JsonSerializer.Serialize(requestId);
        var resultJson = JsonSerializer.Serialize(result, JsonOptions);
        var script = $"window.__nativeBridgeResolve?.({idJson}, {resultJson}, null);";
        await _webView.CoreWebView2.ExecuteScriptAsync(script);
    }

    private async Task SendErrorAsync(string requestId, string message)
    {
        if (_webView.CoreWebView2 == null) return;

        var idJson = JsonSerializer.Serialize(requestId);
        var errorJson = JsonSerializer.Serialize(message);
        var script = $"window.__nativeBridgeResolve?.({idJson}, null, {errorJson});";
        await _webView.CoreWebView2.ExecuteScriptAsync(script);
    }

    private sealed class UiRequest
    {
        public string Id { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public JsonElement Params { get; set; }
    }

    private sealed class DeviceState
    {
        public string DeviceId { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public int Channels { get; set; }
        public int Offset { get; set; }
        public bool IsMaster { get; set; }
    }

    private sealed class RouteState
    {
        public int InCh { get; set; }
        public int OutCh { get; set; }
        public float GainDb { get; set; }
    }

    private sealed class UiState
    {
        public bool Running { get; set; }
        public bool Locked { get; set; }
        public bool StartupAtBoot { get; set; }
        public List<DeviceState> AvailableInputs { get; set; } = [];
        public List<DeviceState> AvailableOutputs { get; set; } = [];
        public List<DeviceState> Inputs { get; set; } = [];
        public List<DeviceState> Outputs { get; set; } = [];
        public List<RouteState> Routes { get; set; } = [];
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
