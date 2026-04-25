using System.Text.Json;
using System.Threading;
using System.Runtime.InteropServices;
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
    private readonly System.Windows.Forms.Timer _metricsPushTimer = new() { Interval = 100 };
    private readonly NotifyIcon _trayIcon = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly Icon _trayAppIcon;

    private bool _locked;
    private bool _allowRealClose;
    private readonly bool _forceStartMinimized;
    private bool _startMinimizedFromConfig;
    private bool _startupAtBoot;
    private string _uiPreferencesJson = "";
    private const string StartupScriptName = "AudioMatrixRouter-startup.cmd";

    // Cached enumeration of system devices. WASAPI device enumeration + AudioClient.MixFormat
    // queries are slow (COM activation per endpoint); refresh only on hot-plug events,
    // not every metrics tick.
    private List<DeviceInfo> _cachedAvailableInputs = new();
    private List<DeviceInfo> _cachedAvailableOutputs = new();
    private bool _availableDevicesDirty = true;
    // Whether to include the bulky available-device list in the next push. Hot metric
    // pushes (peaks/latency) skip it to keep the JSON small and React work cheap.
    private bool _pendingFullStatePush = true;
    private bool _webViewReady;


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
            _availableDevicesDirty = true;
            _pendingFullStatePush = true;
            SyncDevicesWithSystem();
            await PushStateToUiAsync();
        };

        _engine.Init();
        _engine.DevicesChanged += () =>
        {
            if (IsDisposed) return;
            _availableDevicesDirty = true;
            _pendingFullStatePush = true;
            if (InvokeRequired)
            {
                BeginInvoke(() => _deviceRefreshTimer.Start());
            }
            else
            {
                _deviceRefreshTimer.Start();
            }
        };
        _engine.StateChanged += () =>
        {
            if (IsDisposed) return;
            _pendingFullStatePush = true;
            if (InvokeRequired)
            {
                BeginInvoke(() => { _ = PushStateToUiAsync(); });
            }
            else
            {
                _ = PushStateToUiAsync();
            }
        };
        _metricsPushTimer.Tick += (_, _) =>
        {
            // Fire-and-forget; serialize off the UI thread isn't safe (touches engine state),
            // but the work is tiny in hot mode (no device enum) so it's fine on UI thread.
            _ = PushStateToUiAsync();
        };
        _metricsPushTimer.Start();

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
        _webViewReady = true;

        _webView.CoreWebView2.NavigationStarting += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[WebView] Navigation starting: {e.Uri}");
            if (e.Uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                var cacheBuster = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _webView.Source = new Uri($"https://appassets.local/index.html?v={cacheBuster}");
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

        var startupCacheBuster = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _webView.Source = new Uri($"https://appassets.local/index.html?v={startupCacheBuster}");
        System.Diagnostics.Debug.WriteLine("[WebView] Navigating to https://appassets.local/index.html?v=<ts>");
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
            _startupAtBoot = loadedConfig.StartupAtBoot;

            if (loadedConfig.Window.Width > 0 && loadedConfig.Window.Height > 0)
            {
                Size = new System.Drawing.Size(loadedConfig.Window.Width, loadedConfig.Window.Height);
                if (loadedConfig.Window.X >= 0)
                {
                    Location = new System.Drawing.Point(loadedConfig.Window.X, loadedConfig.Window.Y);
                }
            }

            _startMinimizedFromConfig = loadedConfig.Window.StartMinimized;
            _startupAtBoot = ApplyStartupAtBoot(_startupAtBoot) ? _startupAtBoot : false;

            SyncDevicesWithSystem(addAllAvailableIfEmpty: false);

            // First launch fallback: if saved config has no valid devices on this machine, bootstrap with current active devices.
            if (_engine.InputDevices.Count == 0 || _engine.OutputDevices.Count == 0)
            {
                SyncDevicesWithSystem(addAllAvailableIfEmpty: true);
            }

            // If saved routes exist, start the engine immediately so audio flows from launch.
            if (!_engine.IsRunning
                && _engine.InputDevices.Count > 0
                && _engine.OutputDevices.Count > 0
                && _engine.RoutingMatrix.HasAnyCrosspoints())
            {
                _engine.Start();
            }

            return;
        }

        SyncDevicesWithSystem(addAllAvailableIfEmpty: true);
    }

    private void SyncDevicesWithSystem(bool addAllAvailableIfEmpty = false)
    {
        _availableDevicesDirty = true;
        _engine.RefreshDevices();

        // Note: capture endpoints are exposed to the UI via
        // GetAvailableInputDevices(includeCapture, includeLoopback) — they are NOT
        // auto-added as active inputs here. Auto-adding every endpoint would spin up
        // a WasapiCapture per device on Start(), which pegs CPU and hangs the engine.
        var captureDevices = _engine.GetAvailableDevices(DataFlow.Capture);
        var renderDevices = _engine.GetAvailableDevices(DataFlow.Render);

        if (addAllAvailableIfEmpty && _engine.InputDevices.Count == 0)
        {
            foreach (var device in captureDevices)
            {
                _engine.AddInputDevice(device.Id);
            }
        }

        if (addAllAvailableIfEmpty && _engine.OutputDevices.Count == 0)
        {
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
        var config = AppConfig.FromEngine(_engine, bounds.X, bounds.Y, bounds.Width, bounds.Height, _locked, startMinimized, _startupAtBoot, _uiPreferencesJson);
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
                    await SendResultAsync(request.Id, BuildUiState(true));
                    return;

                case "refreshDevices":
                    _availableDevicesDirty = true;
                    _pendingFullStatePush = true;
                    SyncDevicesWithSystem(addAllAvailableIfEmpty: false);
                    await SendResultAsync(request.Id, BuildUiState(true));
                    return;

                case "addInputDevice":
                    if (request.Params.TryGetProperty("deviceId", out var addInputId))
                    {
                        _engine.AddInputDevice(addInputId.GetString() ?? string.Empty);
                        ScheduleSave();
                    }
                    await SendResultAsync(request.Id, BuildUiState(true));
                    return;

                case "addOutputDevice":
                    if (request.Params.TryGetProperty("deviceId", out var addOutputId))
                    {
                        _engine.AddOutputDevice(addOutputId.GetString() ?? string.Empty);
                        ScheduleSave();
                    }
                    await SendResultAsync(request.Id, BuildUiState(true));
                    return;

                case "removeInputDevice":
                    if (request.Params.TryGetProperty("deviceId", out var removeInputId))
                    {
                        _engine.RemoveInputDevice(removeInputId.GetString() ?? string.Empty);
                        ScheduleSave();
                    }
                    await SendResultAsync(request.Id, BuildUiState(true));
                    return;

                case "removeOutputDevice":
                    if (request.Params.TryGetProperty("deviceId", out var removeOutputId))
                    {
                        _engine.RemoveOutputDevice(removeOutputId.GetString() ?? string.Empty);
                        ScheduleSave();
                    }
                    await SendResultAsync(request.Id, BuildUiState(true));
                    return;

                case "startEngine":
                    _engine.Start();
                    await SendResultAsync(request.Id, BuildUiState(true));
                    return;

                case "stopEngine":
                    _engine.Stop();
                    await SendResultAsync(request.Id, BuildUiState(true));
                    return;

                case "setLocked":
                    _locked = request.Params.TryGetProperty("locked", out var lockValue) && lockValue.GetBoolean();
                    ScheduleSave();
                    await SendResultAsync(request.Id, BuildUiState(true));
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
                    await SendResultAsync(request.Id, BuildUiState(true));
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

                    await SendResultAsync(request.Id, BuildUiState(true));
                    return;
                }

                case "setCrosspoints":
                {
                    if (!_locked && request.Params.TryGetProperty("routes", out var routesElem) && routesElem.ValueKind == JsonValueKind.Array)
                    {
                        // Two payload shapes are accepted:
                        //  (a) legacy: { inCh, outCh, active, gainDb } — pre-resolved global channel indices.
                        //  (b) deviceId form: { inDeviceId, inChannel, outDeviceId, outChannel, active, gainDb }.
                        // Form (b) auto-adds the referenced devices to the engine if they aren't already
                        // active, then resolves the global channel index. This is how user-selected
                        // devices end up in the engine — there is no separate "addInputDevice" step.
                        bool devicesChanged = false;
                        var pending = new List<(string? InId, int InCh, string? OutId, int OutCh, bool Active, float GainDb, int LegacyIn, int LegacyOut)>();
                        foreach (var route in routesElem.EnumerateArray())
                        {
                            string? inDeviceId = route.TryGetProperty("inDeviceId", out var inIdElem) ? inIdElem.GetString() : null;
                            string? outDeviceId = route.TryGetProperty("outDeviceId", out var outIdElem) ? outIdElem.GetString() : null;
                            int inChannel = route.TryGetProperty("inChannel", out var inChElem) ? inChElem.GetInt32() : 0;
                            int outChannel = route.TryGetProperty("outChannel", out var outChElem) ? outChElem.GetInt32() : 0;
                            int legacyIn = route.TryGetProperty("inCh", out var inElem) ? inElem.GetInt32() : -1;
                            int legacyOut = route.TryGetProperty("outCh", out var outElem) ? outElem.GetInt32() : -1;
                            bool active = route.TryGetProperty("active", out var activeElem) && activeElem.GetBoolean();
                            float gainDb = route.TryGetProperty("gainDb", out var gainElem) ? gainElem.GetSingle() : 0f;

                            if (!string.IsNullOrEmpty(inDeviceId) && active)
                            {
                                if (_engine.AddInputDevice(inDeviceId)) devicesChanged = true;
                            }
                            if (!string.IsNullOrEmpty(outDeviceId) && active)
                            {
                                if (_engine.AddOutputDevice(outDeviceId)) devicesChanged = true;
                            }

                            pending.Add((inDeviceId, inChannel, outDeviceId, outChannel, active, gainDb, legacyIn, legacyOut));
                        }

                        var updates = new List<(int InCh, int OutCh, bool Active, float GainDb)>();
                        foreach (var p in pending)
                        {
                            int inGlobal = p.LegacyIn;
                            int outGlobal = p.LegacyOut;
                            if (!string.IsNullOrEmpty(p.InId))
                            {
                                var inDev = _engine.InputDevices.FirstOrDefault(d => d.Info.Id == p.InId);
                                if (inDev != null && p.InCh >= 0 && p.InCh < inDev.Info.Channels)
                                    inGlobal = inDev.GlobalChannelOffset + p.InCh;
                                else if (!p.Active)
                                    continue;
                            }
                            if (!string.IsNullOrEmpty(p.OutId))
                            {
                                var outDev = _engine.OutputDevices.FirstOrDefault(d => d.Info.Id == p.OutId);
                                if (outDev != null && p.OutCh >= 0 && p.OutCh < outDev.Info.Channels)
                                    outGlobal = outDev.GlobalChannelOffset + p.OutCh;
                                else if (!p.Active)
                                    continue;
                            }
                            if (inGlobal < 0 || outGlobal < 0) continue;
                            updates.Add((inGlobal, outGlobal, p.Active, p.GainDb));
                        }

                        int changed = _engine.SetCrosspoints(updates);
                        if (changed > 0 || devicesChanged)
                        {
                            if (_engine.RoutingMatrix.HasAnyCrosspoints())
                            {
                                if (!_engine.IsRunning)
                                {
                                    _engine.Start();
                                }
                                else if (devicesChanged)
                                {
                                    // Restart so newly added devices are captured.
                                    _engine.Stop();
                                    _engine.Start();
                                }
                            }
                            else if (_engine.IsRunning)
                            {
                                _engine.Stop();
                            }

                            ScheduleSave();
                        }
                    }

                    await SendResultAsync(request.Id, BuildUiState(true));
                    return;
                }

                case "setInputMasterDevice":
                    if (request.Params.TryGetProperty("deviceId", out var inputMasterId))
                    {
                        _engine.SetInputMasterDevice(inputMasterId.GetString() ?? string.Empty);
                        ScheduleSave();
                    }
                    await SendResultAsync(request.Id, BuildUiState(true));
                    return;

                case "setOutputMasterDevice":
                    if (request.Params.TryGetProperty("deviceId", out var outputMasterId))
                    {
                        _engine.SetOutputMasterDevice(outputMasterId.GetString() ?? string.Empty);
                        ScheduleSave();
                    }
                    await SendResultAsync(request.Id, BuildUiState(true));
                    return;

                case "setOutputDelayMs":
                    if (request.Params.TryGetProperty("deviceId", out var outputDelayDeviceId) &&
                        request.Params.TryGetProperty("delayMs", out var outputDelayMs))
                    {
                        _engine.SetOutputDelayMs(outputDelayDeviceId.GetString() ?? string.Empty, outputDelayMs.GetInt32());
                        ScheduleSave();
                    }
                    await SendResultAsync(request.Id, BuildUiState(true));
                    return;

                case "setCaptureBufferMs":
                    if (request.Params.TryGetProperty("bufferMs", out var captureBufferMs))
                    {
                        _engine.SetCaptureBufferMs(captureBufferMs.GetInt32());
                        ScheduleSave();
                    }
                    await SendResultAsync(request.Id, BuildUiState(true));
                    return;

                case "getStartupAtBoot":
                    await SendResultAsync(request.Id, IsStartupAtBootEnabled());
                    return;

                case "setStartupAtBoot":
                {
                    var enabled = request.Params.TryGetProperty("enabled", out var startupEnabled) && startupEnabled.GetBoolean();
                    var applied = SetStartupAtBoot(enabled);
                    await SendResultAsync(request.Id, applied && _startupAtBoot);
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

    private void EnsureAvailableDevicesCached()
    {
        if (!_availableDevicesDirty) return;
        try
        {
            _cachedAvailableInputs = _engine.GetAvailableInputDevices(includeCapture: true, includeLoopback: false);
            _cachedAvailableOutputs = _engine.GetAvailableDevices(DataFlow.Render);
        }
        catch
        {
            // Fall back to whatever is currently cached.
        }
        _availableDevicesDirty = false;
    }

    private UiState BuildUiState(bool includeAvailableDevices)
    {
        var routes = new List<RouteState>();
        var matrix = _engine.RoutingMatrix;
        double? maxWorkingLatencyMs = null;
        for (int inCh = 0; inCh < matrix.InputChannels; inCh++)
        {
            for (int outCh = 0; outCh < matrix.OutputChannels; outCh++)
            {
                var cp = matrix.GetCrosspoint(inCh, outCh);
                if (!cp.Active) continue;

                double? workingLatencyMs = null;
                if (_engine.TryGetRouteWorkingLatencyMs(inCh, outCh, out var measuredLatencyMs))
                {
                    workingLatencyMs = Math.Round(measuredLatencyMs, 1);
                    maxWorkingLatencyMs = maxWorkingLatencyMs.HasValue
                        ? Math.Max(maxWorkingLatencyMs.Value, workingLatencyMs.Value)
                        : workingLatencyMs.Value;
                }

                routes.Add(new RouteState
                {
                    InCh = inCh,
                    OutCh = outCh,
                    GainDb = matrix.GetGainDb(inCh, outCh),
                    WorkingLatencyMs = workingLatencyMs
                });
            }
        }

        List<DeviceState>? availableInputs = null;
        List<DeviceState>? availableOutputs = null;
        if (includeAvailableDevices)
        {
            EnsureAvailableDevicesCached();
            availableInputs = _cachedAvailableInputs.Select(d => new DeviceState
            {
                DeviceId = d.Id,
                Label = d.Name,
                Channels = d.Channels,
                Offset = 0,
                IsMaster = false,
                DelayMs = 0,
                IsLoopback = false
            }).ToList();
            availableOutputs = _cachedAvailableOutputs.Select(d => new DeviceState
            {
                DeviceId = d.Id,
                Label = d.Name,
                Channels = d.Channels,
                Offset = 0,
                IsMaster = false,
                DelayMs = 0
            }).ToList();
        }

        return new UiState
        {
            Running = _engine.IsRunning,
            Locked = _locked,
            StartupAtBoot = _startupAtBoot,
            CaptureBufferMs = _engine.CaptureBufferMs,
            TotalLatencyMs = maxWorkingLatencyMs,
            HasFullDeviceLists = includeAvailableDevices,
            AvailableInputs = availableInputs,
            AvailableOutputs = availableOutputs,
            Inputs = _engine.InputDevices.Select(d => new DeviceState
            {
                DeviceId = d.Info.Id,
                Label = d.Info.Name,
                Channels = d.Info.Channels,
                Offset = d.GlobalChannelOffset,
                IsMaster = d.IsMasterDevice,
                DelayMs = 0,
                SampleRate = d.Info.SampleRate,
                DriverLatencyMs = d.CaptureLatencyMs,
                Overflows = Interlocked.Read(ref d.InputOverflowCount),
                DroppedFrames = d.RingBuffer?.TotalFramesDropped ?? 0,
                IsLoopback = d.IsLoopback,
                PeakLevels = SampleAndResetPeaks(d.PeakLevels)
            }).ToList(),
            Outputs = _engine.OutputDevices.Select(d => new DeviceState
            {
                DeviceId = d.Info.Id,
                Label = d.Info.Name,
                Channels = d.Info.Channels,
                Offset = d.GlobalChannelOffset,
                IsMaster = d.IsMasterDevice,
                DelayMs = d.OutputDelayMs,
                SampleRate = d.Info.SampleRate,
                DriverLatencyMs = d.RenderLatencyMs,
                Underruns = d.MixProvider?.UnderrunCount ?? 0,
                PeakLevels = d.MixProvider?.SamplePeakLevels() ?? Array.Empty<float>()
            }).ToList(),
            Routes = routes
        };
    }

    private static float[] SampleAndResetPeaks(float[]? peaks)
    {
        if (peaks == null || peaks.Length == 0) return Array.Empty<float>();
        var snapshot = new float[peaks.Length];
        for (int i = 0; i < peaks.Length; i++)
        {
            snapshot[i] = peaks[i];
            peaks[i] = 0f;
        }
        return snapshot;
    }

    private bool IsStartupAtBootEnabled()
    {
        return _startupAtBoot;
    }

    private bool SetStartupAtBoot(bool enabled)
    {
        if (!ApplyStartupAtBoot(enabled)) return false;
        _startupAtBoot = enabled;
        ScheduleSave();
        return true;
    }

    private static string GetStartupScriptPath()
    {
        var startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        return Path.Combine(startupDir, StartupScriptName);
    }

    private static bool ApplyStartupAtBoot(bool enabled)
    {
        try
        {
            var scriptPath = GetStartupScriptPath();
            if (enabled)
            {
                var content = "@echo off\r\n" +
                              "start \"\" \"" + Application.ExecutablePath + "\" --startup\r\n";
                File.WriteAllText(scriptPath, content);
            }
            else if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private Task PushStateToUiAsync()
    {
        if (!_webViewReady || _webView.CoreWebView2 == null) return Task.CompletedTask;

        bool full = _pendingFullStatePush;
        _pendingFullStatePush = false;

        try
        {
            var stateJson = JsonSerializer.Serialize(BuildUiState(full), JsonOptions);
            // PostWebMessageAsJson is fire-and-forget and ~10x cheaper than ExecuteScriptAsync
            // (no script compile, no V8 promise round-trip back to .NET).
            _webView.CoreWebView2.PostWebMessageAsJson("{\"kind\":\"native-state\",\"state\":" + stateJson + "}");
        }
        catch
        {
            // WebView may have torn down between checks.
        }
        return Task.CompletedTask;
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
        public int DelayMs { get; set; }
        public int SampleRate { get; set; }
        public int DriverLatencyMs { get; set; }
        public long Underruns { get; set; }
        public long Overflows { get; set; }
        public long DroppedFrames { get; set; }
        public bool IsLoopback { get; set; }
        public float[] PeakLevels { get; set; } = Array.Empty<float>();
    }

    private sealed class RouteState
    {
        public int InCh { get; set; }
        public int OutCh { get; set; }
        public float GainDb { get; set; }
        public double? WorkingLatencyMs { get; set; }
    }

    private sealed class UiState
    {
        public bool Running { get; set; }
        public bool Locked { get; set; }
        public bool StartupAtBoot { get; set; }
        public int CaptureBufferMs { get; set; }
        public double? TotalLatencyMs { get; set; }
        public bool HasFullDeviceLists { get; set; }
        public List<DeviceState>? AvailableInputs { get; set; }
        public List<DeviceState>? AvailableOutputs { get; set; }
        public List<DeviceState> Inputs { get; set; } = [];
        public List<DeviceState> Outputs { get; set; } = [];
        public List<RouteState> Routes { get; set; } = [];
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
