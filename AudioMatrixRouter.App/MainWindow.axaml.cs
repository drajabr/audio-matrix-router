using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Threading;
using AudioMatrixRouter.App.Controls;
using AudioMatrixRouter.App.Services;
// "Theme" alone would resolve to StyledElement.Theme inside a Window subclass.
using AppTheme = AudioMatrixRouter.App.Theme;

namespace AudioMatrixRouter.App;

public partial class MainWindow : Window
{
    private const float DbMin = -60f;
    private const float DbMax = 12f;

    private readonly AppController _controller;
    private readonly UiPreferences _prefs;
    private readonly DispatcherTimer _metricsTimer;
    private readonly DispatcherTimer _boundsTimer;
    private readonly DispatcherTimer _bannerTimer;

    private UiSnapshot _snapshot = new();
    private MatrixModel? _model;
    private readonly Dictionary<string, float[]> _peaksByDevice = new();
    private readonly AutoScaleTracker _autoScale = new();
    private readonly LatencySmoother _inLatencySmooth = new();
    private readonly LatencySmoother _outLatencySmooth = new();
    private readonly LatencySmoother _totalLatencySmooth = new();
    private readonly JitterSmoother _jitterSmooth = new();

    // Cell state the snapshot cannot carry: gain/phase staged on cells that are
    // currently OFF (no route exists to derive them from).
    private readonly Dictionary<string, (float GainDb, bool Phase)> _pendingCellState = new();

    private string _viewMode = "device";       // "device" | "channel"
    private float _masterGainDb;
    private float _appliedMasterGainDb;        // master gain the snapshot's gains include
    private int? _pendingBufferMs;
    private int? _pendingInBufferMs;
    private readonly DispatcherTimer _gainApplyTimer;
    private readonly DispatcherTimer _bufferApplyTimer;
    private bool _showAll;
    private bool _powerOn = true;
    private bool _mutedAll;
    private double _labelSquare = AppTheme.LabelSquareDefault;
    private string? _hoverRowKey;
    private string? _hoverColKey;

    private bool _allowClose;
    private bool _shutdownDone;

    // Last NORMAL-state placement. SaveBounds persists THIS, never the live rect: the
    // live rect while maximized is the whole work area, and a session saved that way
    // used to reopen after reboot as a screen-sized non-maximized window.
    private PixelPoint _normalPosition;
    private Size _normalClientSize;
    private bool _hasNormalBounds;

    private enum UpdateState { Idle, Checking, Current, Available, Downloading, Ready, Portable, Error }
    private UpdateState _updateState = UpdateState.Idle;
    private string _updateVersion = "";
    private int _updatePercent;
    private long _updateBytes;          // size of what will be downloaded
    private double _updateSpeedBps;     // smoothed download rate
    private int _lastProgressPercent;
    private long _lastProgressAt;

    /// <summary>Raised when the app must really exit (update apply armed a restart).</summary>
    public event Action? QuitRequested;

    public bool StartupAtBoot => _controller.StartupAtBoot;

    public MainWindow()
    {
        _controller = new AppController(action => Dispatcher.UIThread.Post(action));
        _controller.UpdateDownloadProgress += OnUpdateDownloadProgress;
        // Initialize BEFORE subscribing StateChanged: it raises the event synchronously
        // and the handler needs _prefs/_model, which don't exist yet at this point.
        _controller.Initialize();

        _prefs = new UiPreferences(_controller);

        // THE ROOT FIX: honor the user's persisted theme presets BEFORE the first render.
        // This must precede InitializeComponent() so the custom controls' cached palettes
        // (and everything else that reads Theme.X) see the applied values, never defaults.
        AppTheme.Apply(_prefs.BackgroundKey, _prefs.AccentKey, _prefs.FontKey,
            _prefs.FontSizeKey, _prefs.UiScaleKey);

        InitializeComponent();
        ApplyThemeToWindow();

        VersionText.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

        ApplyPreferences();
        RestoreWindowPlacement();

        _controller.StateChanged += OnStateChanged;

        _snapshot = _controller.GetSnapshot();
        SyncFromSnapshot();

        WireHeader();
        WireCorner();
        WireMatrix();
        WireDockCards();

        RebuildModel();
        UpdateCornerVisuals();
        UpdateDockCards();

        _metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _metricsTimer.Tick += (_, _) => OnMetricsTick();
        _metricsTimer.Start();

        _gainApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _gainApplyTimer.Tick += (_, _) => { _gainApplyTimer.Stop(); ApplyPendingMasterGain(); };

        _bufferApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _bufferApplyTimer.Tick += (_, _) => { _bufferApplyTimer.Stop(); ApplyPendingBufferMs(); };

        _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _bannerTimer.Tick += (_, _) =>
        {
            _bannerTimer.Stop();
            Banner.IsVisible = false;
        };

        // Window bounds → controller config, debounced. Only the NORMAL-state rect is
        // tracked; maximize/minimize/hide never overwrite it.
        _boundsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _boundsTimer.Tick += (_, _) =>
        {
            _boundsTimer.Stop();
            SaveBounds();
        };
        // Events only re-arm the debounce; the normal-rect snapshot is taken inside
        // SaveBounds. Sampling per event would run a screen query ~60×/s during a drag,
        // and sampling while a maximize is still settling recorded the maximized rect as
        // the normal one.
        PositionChanged += (_, _) => KickBoundsSave();
        SizeChanged += (_, _) => KickBoundsSave();

        Closing += (_, e) =>
        {
            if (_allowClose) return;
            // ✕ hides to tray; real quit only from the tray menu or update apply.
            e.Cancel = true;
            Hide();
            SaveBounds();
            _prefs.Flush();
        };
    }

    // =====================================================================
    // startup / shutdown
    // =====================================================================

    /// <summary>
    /// Push the applied Theme into everything XAML declared statically: mutate the
    /// resource brushes IN PLACE (StaticResource consumers keep the same instances),
    /// overwrite the Fs* double resources (DynamicResource consumers re-resolve), set
    /// the window font, and apply the whole-UI zoom.
    /// </summary>
    private void ApplyThemeToWindow()
    {
        // typography
        FontFamily = AppTheme.FontFamily;
        FontSize = AppTheme.FontSize;
        Resources["Fs2xs"] = AppTheme.Fs2xs;
        Resources["FsXs"] = AppTheme.FsXs;
        Resources["FsSm"] = AppTheme.FsSm;
        Resources["FsMd"] = AppTheme.FsMd;
        Resources["FsLg"] = AppTheme.FsLg;
        Resources["FsXl"] = AppTheme.FsXl;
        Resources["FsPill"] = AppTheme.Fs2xs * 0.92; // CSS .brand-version-pill: 2xs × 0.92
        // OVERFLOWS/UNDERRUNS must fit the fixed metric tile at every font-size preset.
        Resources["FsMetricLabel"] = AppTheme.Fs2xs * 0.85;
        TitleText.LetterSpacing = AppTheme.FsMd * 0.06; // 0.06em

        Background = AppTheme.BgBrush;

        // solid tokens
        SetBrush("Bg", AppTheme.Bg);
        SetBrush("Surface", AppTheme.Surface);
        SetBrush("Panel", AppTheme.Panel);
        SetBrush("Line", AppTheme.Line);
        SetBrush("LineStrong", AppTheme.LineStrong);
        SetBrush("Text", AppTheme.Text);
        SetBrush("TextStrong", AppTheme.TextStrong);
        SetBrush("Muted", AppTheme.Muted);
        SetBrush("Accent", AppTheme.Accent);
        SetBrush("AccentHl", AppTheme.AccentHl);
        SetBrush("TextOnAccent", AppTheme.TextOnAccent);
        SetBrush("HeaderLine", AppTheme.Mix(AppTheme.Line, Colors.Black, 0.5));
        SetBrush("CardBg", AppTheme.WithAlpha(AppTheme.Panel, 0.76));   // CSS .card-main-copy-split
        SetBrush("CornerBg", AppTheme.WithAlpha(AppTheme.Panel, 0.94));

        // version/update pill (CSS .brand-version-pill / .brand-update-btn)
        SetBrush("PillBg", AppTheme.Mix(AppTheme.Accent, AppTheme.Surface, 0.16));
        SetBrush("PillBorder", AppTheme.Mix(AppTheme.Accent, AppTheme.Line, 0.48));
        SetBrush("PillFg", AppTheme.Mix(AppTheme.Text, AppTheme.AccentHl, 0.92));
        SetBrush("PillBgHover", AppTheme.Mix(AppTheme.Accent, AppTheme.Surface, 0.26));
        SetBrush("PillBgActive", AppTheme.Mix(AppTheme.Accent, AppTheme.Surface, 0.34));
        SetBrush("PillBorderActive", AppTheme.Mix(AppTheme.AccentHl, AppTheme.Line, 0.70));
        SetBrush("PillFgActive", AppTheme.AccentHl);

        // gradient faces
        SetGradient("KeyFace",
            AppTheme.Mix(AppTheme.Surface, Colors.White, 0.93),
            AppTheme.Mix(AppTheme.Surface, Colors.Black, 0.84));
        SetGradient("KeyFaceHover",
            AppTheme.Mix(AppTheme.Surface, Colors.White, 0.90),
            AppTheme.Mix(AppTheme.Surface, Colors.Black, 0.80));
        SetGradient("AccentFace",
            AppTheme.Mix(AppTheme.AccentHl, Colors.White, 0.72),
            AppTheme.Mix(AppTheme.Accent, Colors.Black, 0.86));
        SetGradient("BadgeFace",
            AppTheme.Mix(AppTheme.AccentHl, Colors.White, 0.84),
            AppTheme.Mix(AppTheme.Accent, Colors.Black, 0.92));

        // background aurora follows the accent
        if (Aurora.Background is RadialGradientBrush aurora && aurora.GradientStops.Count == 2)
        {
            aurora.GradientStops[0].Color = AppTheme.WithAlpha(AppTheme.Accent, 0.14);
            aurora.GradientStops[1].Color = AppTheme.WithAlpha(AppTheme.Accent, 0);
        }

        // whole-UI zoom (uiScale preset) — like the web's zoom on the app root.
        // ALWAYS assign: returning to MD (1.0) must clear the previous transform,
        // otherwise switching back to MD does nothing.
        RootScale.LayoutTransform = Math.Abs(AppTheme.UiScale - 1.0) < 0.001
            ? null
            : new ScaleTransform(AppTheme.UiScale, AppTheme.UiScale);
    }

    private void SetBrush(string key, Color color)
    {
        if (Resources.TryGetValue(key, out var res) && res is SolidColorBrush brush)
            brush.Color = color;
    }

    private void SetGradient(string key, Color top, Color bottom)
    {
        if (Resources.TryGetValue(key, out var res) && res is LinearGradientBrush g &&
            g.GradientStops.Count == 2)
        {
            g.GradientStops[0].Color = top;
            g.GradientStops[1].Color = bottom;
        }
    }

    private void ApplyPreferences()
    {
        _viewMode = _prefs.ViewMode;
        _masterGainDb = ClampDb((float)_prefs.MasterGainDb);
        _appliedMasterGainDb = _masterGainDb; // persisted route gains already include it
        _showAll = _prefs.ShowAllDevices;
        _powerOn = _prefs.PowerOn;
        _labelSquare = Math.Clamp(_prefs.LabelSquare, AppTheme.LabelSquareMin, AppTheme.LabelSquareMax);

        CornerBox.Width = _labelSquare;
        CornerBox.Height = _labelSquare;
        Matrix.LabelSquare = _labelSquare;

        GainDrum.Value = _masterGainDb;
    }

    /// <summary>Called by the tray "Start with Windows" item; returns the applied state.</summary>
    public bool ToggleStartupAtBoot()
    {
        try
        {
            _controller.SetStartupAtBoot(!_controller.StartupAtBoot);
        }
        catch
        {
            // Shortcut creation can fail in constrained environments; reflect reality.
        }
        return _controller.StartupAtBoot;
    }

    /// <summary>Flush everything and release the engine. Safe to call more than once.</summary>
    public void PrepareShutdown()
    {
        if (_shutdownDone) return;
        _shutdownDone = true;
        _allowClose = true;

        _metricsTimer.Stop();
        _boundsTimer.Stop();
        SaveBounds();
        _prefs.Flush();
        try { _controller.FlushSave(); }
        catch { /* best-effort */ }
        try { _controller.Dispose(); }
        catch { /* best-effort */ }
    }

    private void RestoreWindowPlacement()
    {
        try
        {
            var (x, y, w, h, _, maximized) = _controller.GetWindowBounds();
            if (w > 300 && h > 200)
            {
                var pos = new PixelPoint(x, y);
                var hasPosition = x > int.MinValue && y > int.MinValue &&
                                  x > -10000 && y > -10000 && (x != 0 || y != 0);

                // Clamp to the target screen's working area: geometry saved on a bigger
                // or differently-scaled monitor must never exceed the screen the window
                // actually opens on.
                if (TryGetWorkAreaDips(hasPosition ? pos : new PixelPoint(0, 0), out var workArea))
                {
                    w = Math.Min(w, (int)workArea.Width);
                    h = Math.Min(h, (int)workArea.Height);
                }

                Width = w;
                Height = h;
                if (hasPosition)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Position = pos;
                    _normalPosition = pos;
                }
                // Seed the tracked size even without a usable saved position: opening
                // maximized means TrackNormalBounds never runs, and an unseeded tracker
                // makes SaveBounds fall back to the live (maximized) rect — the very bug
                // this restore path exists to prevent.
                _normalClientSize = new Size(w, h);
                _hasNormalBounds = true;
                // Reopen maximized when it was left that way — the tracked normal rect
                // above is what un-maximizing falls back to.
                if (maximized) WindowState = WindowState.Maximized;
            }
        }
        catch
        {
            // Bad persisted geometry must never block startup.
        }
    }

    /// <summary>
    /// Snapshots the current rect as the restore geometry, but only while the window is
    /// genuinely in its normal state. Called from SaveBounds (debounced, and on
    /// hide/shutdown), so it costs one screen query per settle instead of one per event.
    /// </summary>
    private void TrackNormalBounds()
    {
        if (WindowState != WindowState.Normal || !IsVisible) return;
        try
        {
            // Belt under the state check: a rect already covering the screen's working
            // area is never a "normal" rect worth remembering, whatever the state says.
            if (TryGetWorkAreaDips(Position, out var workArea) &&
                ClientSize.Width >= workArea.Width - 2 &&
                ClientSize.Height >= workArea.Height - 2)
            {
                return;
            }
        }
        catch
        {
            // Screen lookup is advisory only.
        }
        _normalPosition = Position;
        _normalClientSize = ClientSize;
        _hasNormalBounds = true;
    }

    /// <summary>Working area of the screen holding <paramref name="point"/>, in DIPs
    /// (the unit Width/Height and ClientSize use).</summary>
    private bool TryGetWorkAreaDips(PixelPoint point, out Size workArea)
    {
        workArea = default;
        var screen = Screens.ScreenFromPoint(point) ?? Screens.Primary;
        if (screen is null || screen.Scaling <= 0) return false;
        workArea = new Size(screen.WorkingArea.Width / screen.Scaling,
                            screen.WorkingArea.Height / screen.Scaling);
        return true;
    }

    private void KickBoundsSave()
    {
        _boundsTimer.Stop();
        _boundsTimer.Start();
    }

    private void SaveBounds()
    {
        if (_controller is null || WindowState == WindowState.Minimized) return;
        TrackNormalBounds();
        try
        {
            // Persist the tracked NORMAL rect (client size — it is restored via
            // Width/Height, so saving FrameSize inflated the window by the border
            // thickness on every save/restore cycle).
            var pos = _hasNormalBounds ? _normalPosition : Position;
            var size = _hasNormalBounds ? _normalClientSize : ClientSize;
            _controller.SetWindowBounds(
                pos.X, pos.Y,
                (int)Math.Round(size.Width), (int)Math.Round(size.Height),
                startMinimized: !IsVisible,
                maximized: WindowState == WindowState.Maximized);
        }
        catch
        {
            // Never let placement persistence take the UI down.
        }
    }

    // =====================================================================
    // header: caption buttons + update pill
    // =====================================================================

    private void WireHeader()
    {
        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => WindowState =
            WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CloseButton.Click += (_, _) => Close(); // Closing handler turns this into hide-to-tray

        UpdateBtn.Click += async (_, _) => await HandleUpdateClickAsync();
        SetUpdateState(UpdateState.Idle);

        PickBgBtn.Click += (_, _) => ShowPicker("bg", AppTheme.BackgroundOptions,
            () => _prefs.BackgroundKey, k => _prefs.BackgroundKey = k);
        PickAccentBtn.Click += (_, _) => ShowPicker("accent", AppTheme.AccentOptions,
            () => _prefs.AccentKey, k => _prefs.AccentKey = k);
        PickFontBtn.Click += (_, _) => ShowPicker("font", AppTheme.FontOptions,
            () => _prefs.FontKey, k => _prefs.FontKey = k);
        PickSizeBtn.Click += (_, _) => ShowPicker("size", AppTheme.FontSizeOptions,
            () => _prefs.FontSizeKey, k => _prefs.FontSizeKey = k);
        PickScaleBtn.Click += (_, _) => ShowPicker("scale", AppTheme.UiScaleOptions,
            () => _prefs.UiScaleKey, k => _prefs.UiScaleKey = k);
        UpdateQuickButtons();

        // the floating drawer tracks the slot reserved for it in the header, and the
        // matrix gets scroll room for however much of it the floating dock covers
        LayoutUpdated += (_, _) =>
        {
            PositionQuickDrawer();
            if (DockBar.TranslatePoint(new Point(0, 0), Matrix) is { } dockTop)
                Matrix.BottomInset = Math.Max(0, Matrix.Bounds.Height - dockTop.Y);
        };
        PickerBackdrop.PointerPressed += (_, _) => ClosePicker();
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape && _pickerCategory is not null)
            {
                ClosePicker();
                e.Handled = true;
            }
        };
    }

    private async Task HandleUpdateClickAsync()
    {
        try
        {
            if (_updateState is UpdateState.Checking or UpdateState.Downloading) return;

            if (_updateState == UpdateState.Available)
            {
                _updatePercent = 0;
                _updateSpeedBps = 0;
                _lastProgressPercent = 0;
                _lastProgressAt = 0;
                SetUpdateState(UpdateState.Downloading);
                await _controller.DownloadUpdateAsync();
                SetUpdateState(_controller.CanApplyUpdate ? UpdateState.Ready : UpdateState.Idle);
                return;
            }

            if (_updateState == UpdateState.Ready)
            {
                if (_controller.ApplyUpdateAndArmRestart())
                {
                    QuitRequested?.Invoke(); // real close — restart is armed
                }
                return;
            }

            SetUpdateState(UpdateState.Checking);
            var result = await _controller.CheckForUpdatesAsync();
            if (result.Portable)
            {
                SetUpdateState(UpdateState.Portable);
                return;
            }
            if (!string.IsNullOrEmpty(result.AvailableVersion))
            {
                _updateVersion = result.AvailableVersion!;
                _updateBytes = result.DownloadBytes;
                SetUpdateState(UpdateState.Available);
                return;
            }
            _updateVersion = result.CurrentVersion;
            _updateBytes = 0;
            SetUpdateState(UpdateState.Current);
            DispatcherTimer.RunOnce(() =>
            {
                if (_updateState == UpdateState.Current) SetUpdateState(UpdateState.Idle);
            }, TimeSpan.FromSeconds(2.5));
        }
        catch (Exception ex)
        {
            ShowBanner($"Update failed: {ex.Message}");
            SetUpdateState(UpdateState.Error);
            DispatcherTimer.RunOnce(() =>
            {
                if (_updateState == UpdateState.Error) SetUpdateState(UpdateState.Idle);
            }, TimeSpan.FromSeconds(3));
        }
    }

    private void OnUpdateDownloadProgress(int percent)
    {
        _updatePercent = Math.Clamp(percent, 0, 100);

        // Velopack reports percent only; derive bytes/sec from how fast the percentage
        // moves against the known package size, smoothed so the figure stays readable.
        var now = Environment.TickCount64;
        if (_updatePercent < _lastProgressPercent)
        {
            // restarted/second download: re-baseline instead of freezing the estimate
            _lastProgressPercent = _updatePercent;
            _lastProgressAt = now;
            _updateSpeedBps = 0;
        }
        else if (_updateBytes > 0 && _lastProgressAt > 0 && now > _lastProgressAt)
        {
            var deltaBytes = (_updatePercent - _lastProgressPercent) / 100.0 * _updateBytes;
            var deltaSec = (now - _lastProgressAt) / 1000.0;
            if (deltaBytes > 0 && deltaSec > 0.05)
            {
                var sample = deltaBytes / deltaSec;
                _updateSpeedBps = _updateSpeedBps <= 0 ? sample : _updateSpeedBps * 0.7 + sample * 0.3;
                _lastProgressPercent = _updatePercent;
                _lastProgressAt = now;
            }
        }
        else
        {
            _lastProgressPercent = _updatePercent;
            _lastProgressAt = now;
        }

        if (_updateState == UpdateState.Downloading) SetUpdateState(UpdateState.Downloading);
    }

    private static string FormatBytes(double bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / (1024 * 1024 * 1024):0.0} GB",
        >= 1024 * 1024 => $"{bytes / (1024 * 1024):0.0} MB",
        >= 1024 => $"{bytes / 1024:0} KB",
        _ => $"{bytes:0} B",
    };

    private void SetUpdateState(UpdateState state)
    {
        _updateState = state;

        // ONE pill: the version stays put on the left; the right half spells out what
        // is happening and what a click will do (plus size/progress/speed mid-download).
        var size = _updateBytes > 0 ? FormatBytes(_updateBytes) : null;
        var done = _updateBytes > 0 ? FormatBytes(_updateBytes * _updatePercent / 100.0) : null;
        var speed = _updateSpeedBps > 0 ? $" · {FormatBytes(_updateSpeedBps)}/s" : "";

        // Text only, no symbol glyphs: ⟳/✓/⏻ are not in Consolas, and the fallback
        // font's taller line box made the pill text jump vertically between states.
        UpdateSuffix.Text = state switch
        {
            UpdateState.Checking => "checking…",
            UpdateState.Current => "up to date",
            UpdateState.Available => size is null
                ? $"↓ update to v{_updateVersion}"
                : $"↓ update to v{_updateVersion} · {size}",
            UpdateState.Downloading => done is null
                ? $"downloading {_updatePercent}%"
                : $"{_updatePercent}% · {done}/{size}{speed}",
            UpdateState.Ready => "restart to install",
            UpdateState.Error => "failed — retry",
            UpdateState.Portable => "portable build",
            _ => "check for updates",
        };
        ToolTip.SetTip(UpdateBtn, state switch
        {
            UpdateState.Checking => "Contacting GitHub releases…",
            UpdateState.Current => $"v{_updateVersion} is the latest release",
            UpdateState.Available => size is null
                ? $"Version {_updateVersion} is available — click to download it in the background"
                : $"Version {_updateVersion} is available ({size}) — click to download it in the background",
            UpdateState.Downloading => $"Downloading v{_updateVersion}: {_updatePercent}%" +
                                       (done is null ? "" : $" ({done} of {size})") +
                                       (_updateSpeedBps > 0 ? $" at {FormatBytes(_updateSpeedBps)}/s" : "") +
                                       ". You can keep using the app.",
            UpdateState.Ready => $"v{_updateVersion} is ready. Click to close the app, install and relaunch.",
            UpdateState.Portable => "This build updates itself only when installed via Setup.exe — grab it from GitHub Releases",
            UpdateState.Error => "Update check failed — click to retry",
            _ => "Click to check for a newer release",
        });
        UpdateBtn.Classes.Set("actionable", state is UpdateState.Available or UpdateState.Ready);
    }

    // =====================================================================
    // corner control block
    // =====================================================================

    private void WireCorner()
    {
        PowerBtn.Click += (_, _) =>
        {
            _powerOn = !_powerOn;
            _prefs.PowerOn = _powerOn;
            try { _controller.SetEngineEnabled(_powerOn); }
            catch (Exception ex) { ShowBanner(ex.Message); }
            UpdateCornerVisuals();
        };

        ReloadBtn.Click += (_, _) =>
        {
            // Light the key while the restart runs so the press is visibly acknowledged —
            // a silent no-feedback button is indistinguishable from a broken one.
            ReloadBtn.Classes.Set("active", true);
            try
            {
                _controller.ReloadEngine();
                RefreshSnapshotAndRebuild();
            }
            catch (Exception ex) { ShowBanner(ex.Message); }
            DispatcherTimer.RunOnce(() => ReloadBtn.Classes.Set("active", false),
                TimeSpan.FromMilliseconds(450));
        };

        InputModeBtn.Click += (_, _) =>
        {
            if (_snapshot.Locked) return;
            string[] modes = ["input", "loopback", "both"];
            var idx = Array.IndexOf(modes, _snapshot.InputDeviceMode);
            var next = modes[(idx + 1 + modes.Length) % modes.Length];
            _prefs.InputDeviceMode = next;
            try { _controller.SetInputDeviceMode(next); }
            catch (Exception ex) { ShowBanner(ex.Message); }
            RefreshSnapshotAndRebuild();
        };

        ShowAllBtn.Click += (_, _) =>
        {
            _showAll = !_showAll;
            _prefs.ShowAllDevices = _showAll;
            RebuildModel();
            UpdateCornerVisuals();
        };

        LockBtn.Click += (_, _) =>
        {
            try { _controller.SetLocked(!_snapshot.Locked); }
            catch (Exception ex) { ShowBanner(ex.Message); }
            _prefs.Locked = _controller.Locked;
            RefreshSnapshotAndRebuild();
        };

        ViewBtn.Click += (_, _) =>
        {
            _viewMode = _viewMode == "channel" ? "device" : "channel";
            _prefs.ViewMode = _viewMode;
            _pendingCellState.Clear();
            RebuildModel();
            UpdateCornerVisuals();
        };

        MuteBtn.Click += (_, _) =>
        {
            // Transient: deliberately NOT persisted and works while locked.
            _mutedAll = !_mutedAll;
            try { _controller.SetTransientMuteAll(_mutedAll); }
            catch (Exception ex) { ShowBanner(ex.Message); }
            UpdateCornerVisuals();
        };

        InDrum.ValueFormatter = v => v.ToString("0") + "ms";
        OutDrum.ValueFormatter = v => v.ToString("0") + "ms";
        GainDrum.ValueFormatter = v => v.ToString("+0.0;-0.0;0.0");
        GainDrum.ShowGlow = true; // only the master gain wheel carries the accent glow
        ToolTip.SetTip(InDrum, "Capture buffer. Leave at 10ms unless an input device glitches; " +
                               "the latency budget rebalances around it.");
        ToolTip.SetTip(OutDrum, "Target end-to-end latency. The engine splits it across " +
                                "capture, ring and render buffers.");

        // ONE buffer knob: OUT is the knob that actually governs stability/latency
        // (render buffer + ASRC fill target + barrier); IN is derived. DEBOUNCED:
        // applying a buffer change restarts the whole engine, so per-tick application
        // froze the wheel — the drum spins freely and the engine restarts once, 450ms
        // after the last tick (the web app did the same with a 500ms timer).
        OutDrum.ValueCommitted += (_, v) =>
        {
            _pendingBufferMs = (int)Math.Round(v);
            _bufferApplyTimer.Stop();
            _bufferApplyTimer.Start();
        };
        InDrum.ValueCommitted += (_, v) =>
        {
            _pendingInBufferMs = (int)Math.Round(v);
            _bufferApplyTimer.Stop();
            _bufferApplyTimer.Start();
        };
        GainDrum.ValueCommitted += (_, v) => OnMasterGainCommitted((float)v);
    }

    private void UpdateCornerVisuals()
    {
        PowerBtn.Classes.Set("active", _powerOn);
        LockBtn.Classes.Set("active", _snapshot.Locked);
        LockBtn.Content = _snapshot.Locked ? "🔒" : "🔓";
        ShowAllBtn.Classes.Set("active", _showAll);
        ViewBtn.Classes.Set("active", _viewMode == "channel");
        ToolTip.SetTip(ViewBtn, _viewMode == "channel" ? "Switch to Device View" : "Switch to Channel View");
        MuteBtn.Classes.Set("danger", _mutedAll);
        // speaker-with-wave ↔ muted speaker: near-identical glyph widths, no jump
        MuteBtn.Content = _mutedAll ? "🔇" : "🔊";
        ToolTip.SetTip(MuteBtn, _mutedAll ? "Unmute all outputs" : "Mute all outputs (transient)");
        InputModeBtn.Content = _snapshot.InputDeviceMode switch
        {
            "input" => "🎤",
            "loopback" => "🔊",
            _ => "⇄",
        };
        InputModeBtn.Classes.Set("active", _snapshot.InputDeviceMode == "both");
        ToolTip.SetTip(InputModeBtn, $"Input device list mode: {_snapshot.InputDeviceMode} (click to cycle)");
    }

    private void OnMasterGainCommitted(float newMaster)
    {
        // The route push is debounced — pushing every active route per 0.5dB wheel tick
        // made the wheel feel laggy. No model rebuild here: master gain is invisible on
        // the tiles by design (their readouts are per-tile offsets only).
        _masterGainDb = ClampDb(newMaster);
        _prefs.MasterGainDb = _masterGainDb;
        _gainApplyTimer.Stop();
        _gainApplyTimer.Start();
    }

    private void ApplyPendingMasterGain()
    {
        // _appliedMasterGainDb = the master gain the snapshot's route gains already
        // include; deltas accumulate correctly across any number of debounced ticks.
        if (Math.Abs(_masterGainDb - _appliedMasterGainDb) < 0.001f) return;

        var requests = new List<RouteRequest>();
        foreach (var route in _snapshot.Routes)
        {
            if (!TryMapRoute(route, out var inDev, out var inCh, out var outDev, out var outCh))
                continue;
            requests.Add(new RouteRequest(
                inDev.Id, inCh, outDev.Id, outCh,
                Active: true,
                GainDb: ClampDb(route.GainDb - _appliedMasterGainDb + _masterGainDb),
                PhaseInverted: route.PhaseInverted));
        }
        _appliedMasterGainDb = _masterGainDb;
        if (requests.Count > 0)
            ApplyRoutes(requests);
    }

    // =====================================================================
    // theme preset picker (gear)
    // =====================================================================

    private string? _pickerCategory;
    private double _pickerContentHeight;

    /// <summary>
    /// Category picker panel, faithful to the web `.quick-control-picker`: a dark
    /// chassis panel with VERTICAL option rows — colored swatch square (or letter)
    /// + preset name — the active row accent-highlighted. The panel is anchored
    /// under the WHOLE quick-controls drawer and spans exactly its width, so every
    /// category expands from the same integrated drawer edge (web parity).
    /// </summary>
    private void ShowPicker(string category, IReadOnlyList<AppTheme.PresetOption> options,
        Func<string> getCurrent, Action<string> setKey)
    {
        // toggle: clicking the open category's button collapses the drawer; clicking
        // a different one swaps the list in place (no popup, no dismiss races).
        if (_pickerCategory == category) { ClosePicker(); return; }
        var switching = _pickerCategory is not null;
        _pickerCategory = category;

        // pin the collapsed width so growing the list never widens the box
        if (double.IsNaN(QuickStrip.Width) && QuickStrip.Bounds.Width > 0)
            QuickStrip.Width = QuickStrip.Bounds.Width;

        static Transitions RowTransitions() => new()
        {
            new BrushTransition { Property = Button.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(100), Easing = new CubicEaseOut() },
            new BrushTransition { Property = Button.BorderBrushProperty, Duration = TimeSpan.FromMilliseconds(100), Easing = new CubicEaseOut() },
        };

        var rows = new List<Button>();

        // Selecting an option restyles rows IN PLACE (the row brushes glide via their
        // transitions) — rebuilding the controls on every click made selection snap.
        void ApplyRowVisual(Button row, bool active)
        {
            row.BorderBrush = active
                ? new SolidColorBrush(AppTheme.Mix(AppTheme.Accent, AppTheme.Line, 0.60))
                : Brushes.Transparent;
            row.Background = active
                ? new SolidColorBrush(AppTheme.WithAlpha(AppTheme.Accent, 0.14))
                : Brushes.Transparent;
            if (row.Content is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock tb)
            {
                tb.FontWeight = active ? FontWeight.Bold : FontWeight.SemiBold;
                tb.Foreground = active ? AppTheme.TextStrongBrush : AppTheme.MutedBrush;
            }
        }

        void RefreshRows()
        {
            foreach (var row in rows)
                ApplyRowVisual(row, string.Equals((string)row.Tag!, getCurrent(), StringComparison.OrdinalIgnoreCase));
        }

        var list = new StackPanel { Spacing = 2 };
        foreach (var opt in options)
        {
            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(AppTheme.RadiusMicro),
                BorderThickness = new Thickness(1),
                BorderBrush = AppTheme.LineStrongBrush,
                Background = opt.Swatch is { } c ? new SolidColorBrush(c) : Brushes.Transparent,
                Child = string.IsNullOrEmpty(opt.SwatchLabel) ? null : new TextBlock
                {
                    Text = opt.SwatchLabel,
                    FontSize = opt.SwatchLabel.Length > 2 ? 7 : AppTheme.Fs2xs,
                    FontWeight = FontWeight.Bold,
                    Foreground = opt.SwatchText is { } tc ? new SolidColorBrush(tc) : AppTheme.TextStrongBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var row = new Button
            {
                Tag = opt.Key,
                Padding = new Thickness(8, 5),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(AppTheme.RadiusOverlay),
                BorderThickness = new Thickness(1),
                Transitions = RowTransitions(),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        swatch,
                        new TextBlock
                        {
                            Text = opt.Key,
                            FontSize = AppTheme.FsXs,
                            VerticalAlignment = VerticalAlignment.Center,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        },
                    }
                },
            };

            var key = opt.Key;
            row.Click += (_, _) =>
            {
                if (string.Equals(key, getCurrent(), StringComparison.OrdinalIgnoreCase))
                {
                    ClosePicker(); // web: re-click active = close
                    return;
                }
                setKey(key);
                ApplyThemeLive();
                RefreshRows();
            };
            rows.Add(row);
            list.Children.Add(row);
        }
        RefreshRows();

        // the options go INSIDE the drawer box, under a hairline separator —
        // the drawer literally grows to show them (web .quick-control-picker)
        QuickList.Children.Clear();
        QuickList.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(2, 5, 2, 0),
            Background = new SolidColorBrush(AppTheme.Line),
        });
        QuickList.Children.Add(list);
        QuickList.IsVisible = true;
        QuickStrip.Background = new SolidColorBrush(AppTheme.Panel);

        // measure what the list wants so MaxHeight can animate 0 → exactly that
        // (animating to an arbitrary big number would distort the grow timing)
        var innerWidth = Math.Max(60,
            QuickStrip.Bounds.Width - QuickStrip.Padding.Left - QuickStrip.Padding.Right - 2);
        list.Measure(new Size(innerWidth, double.PositiveInfinity));
        _pickerContentHeight = list.DesiredSize.Height + 6; // + separator row

        PickerBackdrop.IsVisible = true;

        // category switch: dip the list so the swap reads as a crossfade while the
        // box height glides old → new
        if (switching) QuickList.Opacity = 0.25;

        // next frame: the transitions carry the box open (height) and the rows in
        Dispatcher.UIThread.Post(() =>
        {
            if (_pickerCategory is null) return;
            QuickList.Opacity = 1;
            QuickList.MaxHeight = _pickerContentHeight;
        }, DispatcherPriority.Render);
    }

    /// <summary>Keep the floating drawer over the footprint reserved for it in the
    /// header, and keep that footprint matching the drawer's collapsed size.</summary>
    private void PositionQuickDrawer()
    {
        var chrome = QuickStrip.Padding.Left + QuickStrip.Padding.Right +
                     QuickStrip.BorderThickness.Left + QuickStrip.BorderThickness.Right;
        if (QuickButtons.Bounds.Width > 0)
        {
            var w = QuickButtons.Bounds.Width + chrome;
            var h = QuickButtons.Bounds.Height + chrome;
            if (Math.Abs(QuickSlot.Width - w) > 0.5) QuickSlot.Width = w;
            if (Math.Abs(QuickSlot.Height - h) > 0.5) QuickSlot.Height = h;
        }

        if (QuickSlot.TranslatePoint(new Point(0, 0), OverlayHost) is not { } p) return;
        var curLeft = Canvas.GetLeft(QuickStrip);
        var curTop = Canvas.GetTop(QuickStrip);
        if (double.IsNaN(curLeft) || Math.Abs(curLeft - p.X) > 0.5) Canvas.SetLeft(QuickStrip, p.X);
        if (double.IsNaN(curTop) || Math.Abs(curTop - p.Y) > 0.5) Canvas.SetTop(QuickStrip, p.Y);
    }

    private void ClosePicker()
    {
        if (_pickerCategory is null && !PickerBackdrop.IsVisible) return;
        _pickerCategory = null;
        PickerBackdrop.IsVisible = false;
        QuickStrip.Background = Brushes.Transparent;

        // animate shut (height + fade), then tear down once the motion is done
        QuickList.Opacity = 0;
        QuickList.MaxHeight = 0;
        DispatcherTimer.RunOnce(() =>
        {
            if (_pickerCategory is not null) return; // reopened mid-close
            QuickList.Children.Clear();
            QuickList.IsVisible = false;
            QuickStrip.Width = double.NaN; // back to auto (the button row's width)
        }, TimeSpan.FromMilliseconds(220));
    }

    /// <summary>Refresh the header quick-button faces after a theme change.</summary>
    private void UpdateQuickButtons()
    {
        PickBgSwatch.Background = new SolidColorBrush(AppTheme.Surface);
        PickAccentSwatch.Background = new SolidColorBrush(AppTheme.AccentHl);
        PickFontBtn.Content = AppTheme.CurrentFontLabel;
        PickSizeBtn.Content = AppTheme.CurrentSizeLabel;
        PickScaleBtn.Content = AppTheme.CurrentScaleLabel;
    }

    /// <summary>Re-applies the palette from prefs and repaints every themed surface.</summary>
    private void ApplyThemeLive()
    {
        AppTheme.Apply(_prefs.BackgroundKey, _prefs.AccentKey, _prefs.FontKey,
            _prefs.FontSizeKey, _prefs.UiScaleKey);
        ApplyThemeToWindow();
        UpdateQuickButtons();
        Matrix.InvalidateVisual();
        OutDrum.InvalidateVisual();
        GainDrum.InvalidateVisual();
        SourceMeters.InvalidateVisual();
        DestMeters.InvalidateVisual();
        UpdateDockCards();
    }

    private void ApplyPendingBufferMs()
    {
        if (_pendingBufferMs is null && _pendingInBufferMs is null) return;
        try
        {
            // LATENCY = the end-to-end budget the engine splits; IN = capture-buffer
            // override for glitchy capture devices (the budget rebalances around it,
            // so total latency still tracks the LATENCY knob).
            if (_pendingBufferMs is { } outMs) _controller.SetOutputBufferMs(outMs);
            if (_pendingInBufferMs is { } inMs) _controller.SetInputBufferMs(inMs);
        }
        catch (Exception ex) { ShowBanner(ex.Message); }
        _pendingBufferMs = null;
        _pendingInBufferMs = null;
    }

    // =====================================================================
    // matrix wiring
    // =====================================================================

    private void WireMatrix()
    {
        Matrix.CellToggled += (_, e) => OnCellToggled(e);
        Matrix.CellGainDelta += (_, e) => OnCellGainDelta(e);
        Matrix.CellPhaseToggled += (_, e) => OnCellPhaseToggled(e);
        Matrix.CellGainReset += (_, e) => OnCellGainReset(e);
        Matrix.MasterRequested += (_, e) => OnMasterRequested(e);
        Matrix.ReorderRequested += (_, e) => OnReorderRequested(e);
        Matrix.SelectionChanged += (_, e) =>
        {
            _hoverRowKey = e.RowKey;
            _hoverColKey = e.ColKey;
            UpdateDockCards();
        };
        Matrix.LabelSquareChanged += (_, v) =>
        {
            // drag-resize of the label square: the corner block follows live,
            // one shared value persisted (input column width == output row height)
            _labelSquare = v;
            CornerBox.Width = v;
            CornerBox.Height = v;
            _prefs.LabelSquare = v;
        };
    }

    /// <summary>Double-click on a dock device card sets that device as master —
    /// same gesture as on the matrix header cards.</summary>
    private void WireDockCards()
    {
        SourceCard.DoubleTapped += (_, _) =>
        {
            var (sourceId, _) = ResolveDetailPair();
            if (sourceId is not null) TrySetMaster(isInput: true, sourceId);
        };
        DestCard.DoubleTapped += (_, _) =>
        {
            var (_, destId) = ResolveDetailPair();
            if (destId is not null) TrySetMaster(isInput: false, destId);
        };
        ToolTip.SetTip(SourceCard, "Double-click to make this input the master");
        ToolTip.SetTip(DestCard, "Double-click to make this output the master (engine clock)");
    }

    private void OnCellToggled(MatrixCellEvent e)
    {
        if (_snapshot.Locked) return;
        var (on, gain, phase) = GetCellState(e.RowKey, e.ColKey);
        var requests = BuildRequestsForCell(e.RowKey, e.ColKey, !on, gain, phase);
        if (!on)
            _pendingCellState.Remove(CellKey(e.RowKey, e.ColKey));
        else
            _pendingCellState[CellKey(e.RowKey, e.ColKey)] = (gain, phase); // keep staging when turned off
        ApplyRoutes(requests);
    }

    private void OnCellGainDelta(MatrixCellGainEvent e)
    {
        if (_snapshot.Locked) return;
        var (on, gain, phase) = GetCellState(e.RowKey, e.ColKey);
        var next = ClampDb(gain + (float)e.DeltaDb);
        _pendingCellState[CellKey(e.RowKey, e.ColKey)] = (next, phase);
        if (on)
            ApplyRoutes(BuildRequestsForCell(e.RowKey, e.ColKey, true, next, phase));
        else
            RebuildModel();
    }

    private void OnCellGainReset(MatrixCellEvent e)
    {
        if (_snapshot.Locked) return;
        var (on, _, phase) = GetCellState(e.RowKey, e.ColKey);
        _pendingCellState[CellKey(e.RowKey, e.ColKey)] = (0f, phase);
        if (on)
            ApplyRoutes(BuildRequestsForCell(e.RowKey, e.ColKey, true, 0f, phase));
        else
            RebuildModel();
    }

    private void OnCellPhaseToggled(MatrixCellEvent e)
    {
        if (_snapshot.Locked) return;
        var (on, gain, phase) = GetCellState(e.RowKey, e.ColKey);
        _pendingCellState[CellKey(e.RowKey, e.ColKey)] = (gain, !phase);
        if (on)
            ApplyRoutes(BuildRequestsForCell(e.RowKey, e.ColKey, true, gain, !phase));
        else
            RebuildModel();
    }

    private void OnMasterRequested(MatrixHeaderEvent e) => TrySetMaster(e.IsInput, e.DeviceId);

    /// <summary>
    /// Sets the master device. The preference is always recorded (and promoted by the
    /// engine the moment the device attaches and carries a route), so the badge — which
    /// follows the recorded choice — is the whole feedback; nothing is announced.
    /// </summary>
    private void TrySetMaster(bool isInput, string deviceId)
    {
        if (_snapshot.Locked) return;
        try
        {
            if (isInput)
            {
                _controller.SetInputMaster(deviceId);
                _prefs.InputMasterId = deviceId;
            }
            else
            {
                _controller.SetOutputMaster(deviceId);
                _prefs.OutputMasterId = deviceId;
            }
        }
        catch (Exception ex)
        {
            ShowBanner(ex.Message); // a real failure still deserves the error banner
        }
        RefreshSnapshotAndRebuild();
    }

    private void OnReorderRequested(MatrixReorderEvent e)
    {
        if (_snapshot.Locked || e.DeviceId == e.TargetDeviceId) return;

        var order = e.IsInput ? _prefs.InputOrder : _prefs.OutputOrder;
        // Ensure both ids are present (order may lag behind newly-seen devices).
        var visible = (e.IsInput ? _model?.Inputs : _model?.Outputs)?.Select(d => d.Id) ?? [];
        foreach (var id in visible)
            if (!order.Contains(id)) order.Add(id);

        var fromIdx = order.IndexOf(e.DeviceId);
        var toIdx = order.IndexOf(e.TargetDeviceId);
        if (fromIdx < 0 || toIdx < 0) return;
        order.RemoveAt(fromIdx);
        order.Insert(toIdx > fromIdx ? order.IndexOf(e.TargetDeviceId) + 1 : order.IndexOf(e.TargetDeviceId), e.DeviceId);

        if (e.IsInput) _prefs.InputOrder = order;
        else _prefs.OutputOrder = order;
        RebuildModel();
    }

    // =====================================================================
    // route building (port of App.jsx syncConnectionToNative)
    // =====================================================================

    private static string CellKey(string rowKey, string colKey) => rowKey + "|" + colKey;

    private (bool On, float GainDb, bool Phase) GetCellState(string rowKey, string colKey)
    {
        var key = CellKey(rowKey, colKey);
        if (_model?.Cells != null && _model.Cells.TryGetValue(key, out var cell) && cell.On)
            return (true, cell.GainDb, cell.PhaseInverted);
        if (_pendingCellState.TryGetValue(key, out var pending))
            return (false, pending.GainDb, pending.Phase);
        return (false, 0f, false);
    }

    private List<RouteRequest> BuildRequestsForCell(string rowKey, string colKey, bool on, float cellGain, bool phase)
    {
        var requests = new List<RouteRequest>();
        var baseGain = ClampDb(cellGain + _masterGainDb);

        if (_viewMode == "channel")
        {
            if (!TryParseChannelKey(rowKey, out var inId, out var inCh) ||
                !TryParseChannelKey(colKey, out var outId, out var outCh))
                return requests;
            requests.Add(new RouteRequest(inId, inCh, outId, outCh, on, on ? baseGain : 0f, on && phase));
            return requests;
        }

        var inputId = DeviceIdFromKey(rowKey);
        var outputId = DeviceIdFromKey(colKey);
        var inCount = Math.Max(1, FindDeviceChannels(inputId, isInput: true));
        var outCount = Math.Max(1, FindDeviceChannels(outputId, isInput: false));

        if (!on)
        {
            // Device tile OFF kills every channel route for the pair immediately.
            for (var i = 0; i < inCount; i++)
                for (var o = 0; o < outCount; o++)
                    requests.Add(new RouteRequest(inputId, i, outputId, o, false, 0f, false));
            return requests;
        }

        foreach (var (inCh, outCh, gainOffsetDb) in BuildDeviceToChannelRouteMatrix(inCount, outCount))
        {
            requests.Add(new RouteRequest(
                inputId, inCh, outputId, outCh,
                Active: true,
                GainDb: ClampDb(baseGain + gainOffsetDb),
                PhaseInverted: phase));
        }
        return requests;
    }

    /// <summary>
    /// Diagonal / spread mapping between device channel sets — verbatim port of
    /// App.jsx buildDeviceToChannelRouteMatrix (device-relative indexes 0..n-1).
    /// </summary>
    private static List<(int InCh, int OutCh, float GainOffsetDb)> BuildDeviceToChannelRouteMatrix(int inCount, int outCount)
    {
        var routes = new List<(int, int, float)>();
        if (inCount <= 0 || outCount <= 0) return routes;

        if (inCount == outCount)
        {
            for (var i = 0; i < inCount; i++)
                routes.Add((i, i, 0f));
            return routes;
        }

        if (inCount < outCount)
        {
            for (var outSlot = 0; outSlot < outCount; outSlot++)
                routes.Add((outSlot * inCount / outCount, outSlot, 0f));
            return routes;
        }

        // Downmix: bucket inputs onto outputs, attenuating each bucket by 1/groupSize.
        var bucketSizes = new int[outCount];
        for (var inSlot = 0; inSlot < inCount; inSlot++)
            bucketSizes[Math.Min(outCount - 1, inSlot * outCount / inCount)]++;

        for (var inSlot = 0; inSlot < inCount; inSlot++)
        {
            var outSlot = Math.Min(outCount - 1, inSlot * outCount / inCount);
            var groupSize = Math.Max(1, bucketSizes[outSlot]);
            routes.Add((inSlot, outSlot, (float)(20.0 * Math.Log10(1.0 / groupSize))));
        }
        return routes;
    }

    private void ApplyRoutes(List<RouteRequest> requests)
    {
        if (requests.Count == 0) return;
        try
        {
            var errors = _controller.SetRoutes(requests);
            if (errors is { Count: > 0 })
                ShowBanner(string.Join(" · ", errors));
        }
        catch (Exception ex)
        {
            ShowBanner($"Route update failed: {ex.Message}");
        }
        RefreshSnapshotAndRebuild();
    }

    // =====================================================================
    // snapshot → model
    // =====================================================================

    private void OnStateChanged(UiSnapshot snapshot)
    {
        _snapshot = snapshot;
        SyncFromSnapshot();
        RebuildModel();
        UpdateCornerVisuals();
        UpdateDockCards();
    }

    private void RefreshSnapshotAndRebuild()
    {
        try { _snapshot = _controller.GetSnapshot(); }
        catch { return; }
        SyncFromSnapshot();
        RebuildModel();
        UpdateCornerVisuals();
        UpdateDockCards();
    }

    private void SyncFromSnapshot()
    {
        // Drum values follow the authoritative config — but never stomp a value the
        // user just dialed while its debounced apply is still pending.
        if ((_bufferApplyTimer?.IsEnabled ?? false) == false)
        {
            if (Math.Abs(OutDrum.Value - _snapshot.OutputBufferMs) >= 0.5)
                OutDrum.Value = _snapshot.OutputBufferMs;
            if (Math.Abs(InDrum.Value - _snapshot.InputBufferMs) >= 0.5)
                InDrum.Value = _snapshot.InputBufferMs;
        }
    }

    private void RebuildModel()
    {
        var channelView = _viewMode == "channel";
        var inputs = OrderedDevices(isInput: true).Select(d => MakeInfo(d, isInput: true)).ToList();
        var outputs = OrderedDevices(isInput: false).Select(d => MakeInfo(d, isInput: false)).ToList();

        var cells = new Dictionary<string, MatrixCell>();
        foreach (var route in _snapshot.Routes)
        {
            if (!TryMapRoute(route, out var inDev, out var inCh, out var outDev, out var outCh))
                continue;

            // Tiles show ONLY their own offset. Route gains include the master gain that
            // was last PUSHED (_appliedMasterGainDb), so subtract exactly that — using the
            // wheel's live value here made every tile sprout a phantom readout while the
            // master moved (and until the debounced apply landed).
            var cellGain = ClampDb(route.GainDb - _appliedMasterGainDb);
            if (channelView)
            {
                cells[$"ch:{inDev.Id}:{inCh}|ch:{outDev.Id}:{outCh}"] = new MatrixCell
                {
                    On = true,
                    GainDb = cellGain,
                    PhaseInverted = route.PhaseInverted,
                };
            }
            else
            {
                // Device view aggregates the pair; base gain ≈ the loudest channel route
                // (spread offsets are ≤ 0 dB, so max recovers the un-offset cell gain).
                var key = $"dev:{inDev.Id}|dev:{outDev.Id}";
                if (cells.TryGetValue(key, out var existing))
                {
                    existing.GainDb = Math.Max(existing.GainDb, cellGain);
                    existing.PhaseInverted |= route.PhaseInverted;
                }
                else
                {
                    cells[key] = new MatrixCell { On = true, GainDb = cellGain, PhaseInverted = route.PhaseInverted };
                }
            }
        }

        // Staged gain/phase on cells that are currently off (shows in the readout).
        foreach (var (key, pending) in _pendingCellState)
        {
            if (!cells.ContainsKey(key))
                cells[key] = new MatrixCell { On = false, GainDb = pending.GainDb, PhaseInverted = pending.Phase };
        }

        _model = new MatrixModel
        {
            Inputs = inputs,
            Outputs = outputs,
            Cells = cells,
            ChannelView = channelView,
            Locked = _snapshot.Locked,
        };
        Matrix.Model = _model;
    }

    private List<DeviceSnapshot> OrderedDevices(bool isInput)
    {
        var configured = isInput ? _snapshot.Inputs : _snapshot.Outputs;
        var available = isInput ? _snapshot.AvailableInputs : _snapshot.AvailableOutputs;

        // Which engine devices actually carry a route right now. Devices stay in the
        // engine after their last route is removed (dormant-route bookkeeping), but a
        // row with no routes is noise when show-all is off.
        var routedIds = new HashSet<string>();
        foreach (var route in _snapshot.Routes)
        {
            if (TryMapRoute(route, out var inDev, out _, out var outDev, out _))
                routedIds.Add(isInput ? inDev.Id : outDev.Id);
        }

        // The input-mode filter applies to every UNROUTED row, configured or available:
        // "input" shows capture endpoints only (the VB-Audio virtual mic), "loopback"
        // shows render loopbacks only (the virtual speaker). Routed devices and the
        // master always show — hiding audio that is actively flowing would be worse.
        bool ModeAllows(DeviceSnapshot d) => !isInput || _snapshot.InputDeviceMode switch
        {
            "input" => !d.IsLoopback,
            "loopback" => d.IsLoopback,
            _ => true,
        };

        var pool = configured
            .Where(d => routedIds.Contains(d.Id) || d.IsMaster || (_showAll && ModeAllows(d)))
            .ToList();
        if (_showAll)
        {
            foreach (var device in available)
                if (ModeAllows(device) && !pool.Any(d => d.Id == device.Id))
                    pool.Add(device);
        }

        var saved = isInput ? _prefs.InputOrder : _prefs.OutputOrder;
        var merged = MergeOrder(saved, pool.Select(d => d.Id).ToList());

        // Persist the merged order (retains offline devices' slots, appends new ones) —
        // mirrors App.jsx buildPersistedState.
        if (!merged.SequenceEqual(saved))
        {
            if (isInput) _prefs.InputOrder = merged;
            else _prefs.OutputOrder = merged;
        }

        var byId = pool.ToDictionary(d => d.Id);
        var result = new List<DeviceSnapshot>();
        foreach (var id in merged)
            if (byId.TryGetValue(id, out var device))
                result.Add(device);
        return result;
    }

    /// <summary>Verbatim port of App.jsx mergeOrder — saved slots win, new ids append.</summary>
    private static List<string> MergeOrder(IReadOnlyList<string> savedOrder, IReadOnlyList<string> currentIds)
    {
        var current = currentIds.Where(id => !string.IsNullOrEmpty(id)).ToList();
        var currentSet = new HashSet<string>(current);
        var seen = new HashSet<string>();
        var merged = new List<string>();
        var cursor = 0;

        foreach (var id in savedOrder)
        {
            if (string.IsNullOrEmpty(id) || seen.Contains(id)) continue;
            if (currentSet.Contains(id))
            {
                // A slot that belongs to a present device: fill it with the next present
                // device in the CURRENT order, so live reordering still persists.
                while (cursor < current.Count && seen.Contains(current[cursor])) cursor++;
                if (cursor < current.Count)
                {
                    var cid = current[cursor];
                    cursor++;
                    seen.Add(cid);
                    merged.Add(cid);
                }
            }
            else
            {
                seen.Add(id);
                merged.Add(id);
            }
        }

        foreach (var id in current)
        {
            if (seen.Add(id))
                merged.Add(id);
        }
        return merged;
    }

    private MatrixDeviceInfo MakeInfo(DeviceSnapshot device, bool isInput)
    {
        var custom = isInput ? _prefs.GetInputLabel(device.Id) : _prefs.GetOutputLabel(device.Id);
        var (primary, hardware) = SplitDeviceLabel(device.Name);
        custom = CleanCustomLabel(custom, device.Name, hardware);

        var channels = Math.Max(1, device.Channels);
        if (!_peaksByDevice.TryGetValue(device.Id, out var peaks) || peaks.Length != channels)
        {
            peaks = new float[channels];
            _peaksByDevice[device.Id] = peaks;
        }

        return new MatrixDeviceInfo
        {
            Id = device.Id,
            Label = custom ?? primary,
            SubLabel = hardware,
            Channels = channels,
            IsMaster = device.IsMaster,
            IsLoopback = device.IsLoopback,
            Peaks = peaks,
        };
    }

    /// <summary>
    /// Windows names endpoints "&lt;endpoint&gt; (&lt;hardware&gt;)". Split them so the
    /// hardware part appears ONCE, on the sub-label line — the name never repeats what
    /// is written underneath it. Matching runs from the end with depth counting so
    /// nested parens survive ("Speaker (Realtek(R) Audio)" → "Speaker" + "Realtek(R) Audio").
    /// </summary>
    private static (string Primary, string Hardware) SplitDeviceLabel(string raw)
    {
        raw = (raw ?? "").Trim();

        // "(loopback)" is our own marker, not part of the device name: hold it aside
        // so the hardware match sees the real trailing group, then put it back.
        const string loopback = "(loopback)";
        var isLoopback = raw.EndsWith(loopback, StringComparison.OrdinalIgnoreCase);
        var core = isLoopback ? raw[..^loopback.Length].TrimEnd() : raw;

        var primary = core;
        var hardware = "";
        if (core.EndsWith(')'))
        {
            var depth = 0;
            for (var i = core.Length - 1; i >= 0; i--)
            {
                if (core[i] == ')') depth++;
                else if (core[i] == '(' && --depth == 0)
                {
                    hardware = core[(i + 1)..^1].Trim();
                    primary = core[..i].TrimEnd();
                    break;
                }
            }
        }

        if (primary.Length == 0) { primary = core; hardware = ""; }
        if (string.Equals(hardware, primary, StringComparison.OrdinalIgnoreCase)) hardware = "";
        if (isLoopback) primary += " " + loopback;
        return (primary, hardware);
    }

    /// <summary>A saved label that is just the raw device name is not a real rename —
    /// treat it as absent, and strip a trailing "(hardware)" from genuine ones.</summary>
    private static string? CleanCustomLabel(string? custom, string rawName, string hardware)
    {
        if (string.IsNullOrWhiteSpace(custom)) return null;
        custom = custom.Trim();
        if (string.Equals(custom, rawName.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        if (hardware.Length > 0)
        {
            var suffix = "(" + hardware + ")";
            if (custom.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                custom = custom[..^suffix.Length].TrimEnd();
        }
        return custom.Length > 0 ? custom : null;
    }

    private bool TryMapRoute(RouteSnapshot route, out DeviceSnapshot inDev, out int inCh, out DeviceSnapshot outDev, out int outCh)
    {
        inDev = null!;
        outDev = null!;
        inCh = outCh = 0;
        var input = FindByGlobalChannel(_snapshot.Inputs, route.InCh);
        var output = FindByGlobalChannel(_snapshot.Outputs, route.OutCh);
        if (input is null || output is null) return false;
        inDev = input;
        outDev = output;
        inCh = route.InCh - input.Offset;
        outCh = route.OutCh - output.Offset;
        return true;
    }

    private static DeviceSnapshot? FindByGlobalChannel(List<DeviceSnapshot> devices, int globalChannel) =>
        devices.FirstOrDefault(d => globalChannel >= d.Offset && globalChannel < d.Offset + Math.Max(1, d.Channels));

    private int FindDeviceChannels(string deviceId, bool isInput)
    {
        var configured = isInput ? _snapshot.Inputs : _snapshot.Outputs;
        var available = isInput ? _snapshot.AvailableInputs : _snapshot.AvailableOutputs;
        var device = configured.FirstOrDefault(d => d.Id == deviceId)
                     ?? available.FirstOrDefault(d => d.Id == deviceId);
        return device?.Channels ?? 0;
    }

    private static string DeviceIdFromKey(string key)
    {
        if (key.StartsWith("dev:", StringComparison.Ordinal)) return key[4..];
        if (TryParseChannelKey(key, out var id, out _)) return id;
        return key;
    }

    private static bool TryParseChannelKey(string key, out string deviceId, out int channel)
    {
        deviceId = "";
        channel = 0;
        if (!key.StartsWith("ch:", StringComparison.Ordinal)) return false;
        var body = key[3..];
        var split = body.LastIndexOf(':');
        if (split <= 0) return false;
        deviceId = body[..split];
        return int.TryParse(body[(split + 1)..], out channel);
    }

    private static float ClampDb(float db) => Math.Clamp(db, DbMin, DbMax);

    // =====================================================================
    // metrics (100ms) → peaks, dock, status line
    // =====================================================================

    private void OnMetricsTick()
    {
        MetricsSnapshot metrics;
        try { metrics = _controller.GetMetrics(); }
        catch { return; }

        // Peaks into the model arrays: auto-scale per device, then the pow-0.72 display
        // curve (App.jsx autoScaleLevels + shapeMeterLevel).
        CopyPeaks(metrics.Inputs, "in");
        CopyPeaks(metrics.Outputs, "out");
        Matrix.RefreshPeaks();

        // Latency EMA display smoothing (App.jsx updateLatencyDisplay: α=0.1, 1.2ms step
        // threshold, 900ms min update, 6s null grace) — kills digit flicker at 10Hz.
        MetricInLatency.Text = _inLatencySmooth.Format(metrics.InputLatencyMs);
        MetricInJitter.Text = _jitterSmooth.Format(metrics.InputJitterMs);
        MetricInOverflows.Text = metrics.Inputs.Sum(d => d.Overflows).ToString();
        MetricInDrops.Text = metrics.Inputs.Sum(d => d.DroppedFrames).ToString();
        MetricOutLatency.Text = _outLatencySmooth.Format(metrics.OutputLatencyMs);

        // Tier/period transparency: which WASAPI rung each side achieved.
        var inTier = metrics.Inputs.FirstOrDefault(d => d.PeriodMs > 0);
        if (inTier is not null)
            ToolTip.SetTip(MetricInLatency, $"Capture cadence {inTier.PeriodMs:0.##}ms · {inTier.TierName}");
        var outTier = metrics.Outputs.FirstOrDefault(d => d.PeriodMs > 0);
        if (outTier is not null)
            ToolTip.SetTip(MetricOutLatency, $"Engine period {outTier.PeriodMs:0.##}ms · {outTier.TierName}");
        MetricOutSync.Text = metrics.Outputs.Sum(d => d.SyncCorrections).ToString();
        MetricOutUnderruns.Text = metrics.Outputs.Sum(d => d.Underruns).ToString();
        MetricOutDrops.Text = metrics.Outputs.Sum(d => d.DroppedFrames).ToString();

        // Dock card meters follow the detail pair.
        var (sourceId, destId) = ResolveDetailPair();
        SourceMeters.SetLevels(ShapedLevels(metrics.Inputs, sourceId, "in"));
        DestMeters.SetLevels(ShapedLevels(metrics.Outputs, destId, "out"));
    }

    private void CopyPeaks(List<DeviceMetrics> deviceMetrics, string prefix)
    {
        foreach (var dm in deviceMetrics)
        {
            if (!_peaksByDevice.TryGetValue(dm.DeviceId, out var target)) continue;
            var raw = new float[target.Length];
            for (var i = 0; i < raw.Length; i++)
                raw[i] = i < dm.PeakLevels.Length ? dm.PeakLevels[i] : 0f;
            _autoScale.Scale($"{prefix}:{dm.DeviceId}", raw);
            for (var i = 0; i < target.Length; i++)
                target[i] = ShapeMeterLevel(raw[i]);
        }
    }

    private static float ShapeMeterLevel(float value) =>
        (float)Math.Clamp(Math.Pow(Math.Clamp(value, 0f, 1f), 0.72), 0, 1);

    private IReadOnlyList<double> ShapedLevels(List<DeviceMetrics> metrics, string? deviceId, string prefix)
    {
        if (deviceId is null) return [];
        var dm = metrics.FirstOrDefault(m => m.DeviceId == deviceId);
        if (dm is null) return [];
        var raw = (float[])dm.PeakLevels.Clone();
        _autoScale.Scale($"{prefix}-dock:{deviceId}", raw);
        return raw.Select(p => (double)ShapeMeterLevel(p)).ToList();
    }

    private static string FormatMs(double? value) => value is { } v ? $"{v:0.0}ms" : "--";

    /// <summary>App.jsx updateLatencyDisplay port: EMA α=0.1, shows a new value only when
    /// it moved ≥1.2ms or 900ms elapsed; a missing value survives a 6s grace period.</summary>
    private sealed class LatencySmoother
    {
        private double? _smooth;
        private double? _shown;
        private long _shownAt;
        private long _missingSince;

        public string Format(double? value)
        {
            var now = Environment.TickCount64;
            if (value is not { } v)
            {
                if (_missingSince == 0) _missingSince = now;
                if (now - _missingSince < 6000 && _shown is { } held) return $"{held:0.0}ms";
                _smooth = null;
                _shown = null;
                return "--";
            }

            _missingSince = 0;
            // Fast tracker: big moves pass straight through (a buffer change or drain
            // must show immediately, not crawl over seconds), small ones get a light
            // EMA so the last digit doesn't flicker at 10 Hz.
            _smooth = _smooth is { } s && Math.Abs(v - s) < 8 ? s * 0.6 + v * 0.4 : v;
            var display = Math.Round(_smooth.Value, 1);
            if (_shown is not { } last || Math.Abs(display - last) >= 0.7 || now - _shownAt >= 400)
            {
                _shown = display;
                _shownAt = now;
            }
            return $"{_shown:0.0}ms";
        }
    }

    /// <summary>App.jsx updateJitterDisplay port: update on ≥0.4ms change or every 320ms.</summary>
    private sealed class JitterSmoother
    {
        private double? _shown;
        private long _shownAt;

        public string Format(double? value)
        {
            if (value is not { } v)
            {
                _shown = null;
                return "--";
            }
            var now = Environment.TickCount64;
            var rounded = Math.Round(v, 1);
            if (_shown is not { } last || Math.Abs(rounded - last) >= 0.4 || now - _shownAt >= 320)
            {
                _shown = rounded;
                _shownAt = now;
            }
            return $"{_shown:0.0}ms";
        }
    }

    /// <summary>App.jsx autoScaleLevels port: adaptive floor/peak tracker per device so
    /// quiet sources still animate and loud ones don't pin the bars.</summary>
    private sealed class AutoScaleTracker
    {
        private readonly Dictionary<string, (double Floor, double Peak)> _map = new();

        public void Scale(string key, float[] levels)
        {
            if (levels.Length == 0) return;
            var (floor, peak) = _map.TryGetValue(key, out var t) ? t : (0.003, 0.1);

            double obsPeak = 0, obsFloor = 1;
            foreach (var l in levels)
            {
                var c = Math.Clamp(l, 0f, 1f);
                if (c > obsPeak) obsPeak = c;
                if (c < obsFloor) obsFloor = c;
            }

            peak = obsPeak > peak ? peak + (obsPeak - peak) * 0.32 : Math.Max(obsPeak, peak * 0.975);
            floor = obsFloor < floor ? floor + (obsFloor - floor) * 0.18 : Math.Min(obsFloor, floor * 1.015 + 0.0002);

            var range = Math.Clamp(peak - floor, 0.035, 0.7);
            _map[key] = (floor, floor + range);

            for (var i = 0; i < levels.Length; i++)
                levels[i] = (float)Math.Clamp((Math.Clamp(levels[i], 0f, 1f) - floor) / range, 0, 1);
        }
    }

    // =====================================================================
    // dock cards (hovered route, falling back to the master pair)
    // =====================================================================

    private (string? SourceId, string? DestId) ResolveDetailPair()
    {
        var sourceId = _hoverRowKey is { } row ? DeviceIdFromKey(row) : null;
        var destId = _hoverColKey is { } col ? DeviceIdFromKey(col) : null;

        sourceId ??= _snapshot.Inputs.FirstOrDefault(d => d.IsMaster)?.Id
                     ?? (_prefs.InputMasterId is { Length: > 0 } im && _snapshot.Inputs.Any(d => d.Id == im) ? im : null)
                     ?? _snapshot.Inputs.FirstOrDefault()?.Id;
        destId ??= _snapshot.Outputs.FirstOrDefault(d => d.IsMaster)?.Id
                   ?? (_prefs.OutputMasterId is { Length: > 0 } om && _snapshot.Outputs.Any(d => d.Id == om) ? om : null)
                   ?? _snapshot.Outputs.FirstOrDefault()?.Id;
        return (sourceId, destId);
    }

    private void UpdateDockCards()
    {
        var (sourceId, destId) = ResolveDetailPair();

        var source = FindInfo(_model?.Inputs, sourceId);
        var dest = FindInfo(_model?.Outputs, destId);

        SourceName.Text = source?.Label ?? "—";
        SourceSub.Text = source?.SubLabel ?? "";
        DestName.Text = dest?.Label ?? "—";
        DestSub.Text = dest?.SubLabel ?? "";

        BuildChips(SourceChipsPanel, source?.Channels ?? 0);
        BuildChips(DestChipsPanel, dest?.Channels ?? 0);

        // MASTER indicator: the 20px vertical badge (rotated MASTER text, accent face)
        // shows ONLY when the displayed device is the master — nothing otherwise.
        StyleMasterCard(SourceMasterBadge, SourceTextStack, SourceMeters, source?.IsMaster == true);
        StyleMasterCard(DestMasterBadge, DestTextStack, DestMeters, dest?.IsMaster == true);

        // Route indicator between the cards.
        var active = false;
        if (source is not null && dest is not null)
        {
            foreach (var route in _snapshot.Routes)
            {
                if (TryMapRoute(route, out var inDev, out _, out var outDev, out _) &&
                    inDev.Id == source.Id && outDev.Id == dest.Id)
                {
                    active = true;
                    break;
                }
            }
        }
        var fanOut = active && source is not null && dest is not null && source.Channels != dest.Channels;
        RouteGlyph.Text = active ? (fanOut ? "⮆" : "🡢") : "⏸";
        RouteGlyph.Foreground = active ? AppTheme.AccentHlBrush : AppTheme.TextStrongBrush;
        RouteBox.BorderBrush = active
            ? new SolidColorBrush(AppTheme.Mix(AppTheme.Accent, AppTheme.Line, 0.60))
            : new SolidColorBrush(AppTheme.Mix(AppTheme.Line, Colors.White, 0.88));
    }

    private static void StyleMasterCard(Border badge, StackPanel textStack, MeterBars meters, bool isMaster)
    {
        // Opacity, not IsVisible: the badge fades and the content margins glide
        // (their ThicknessTransitions) instead of snapping when the pair changes.
        badge.IsVisible = true;
        badge.Opacity = isMaster ? 1 : 0;
        // content clears the 20px badge (CSS pads the card 30px left when badged)
        textStack.Margin = new Thickness(isMaster ? 30 : 10, 8, 10, 0);
        meters.Margin = new Thickness(isMaster ? 24 : 4, 4, 4, 4);
    }

    private static MatrixDeviceInfo? FindInfo(List<MatrixDeviceInfo>? infos, string? id) =>
        id is null ? null : infos?.FirstOrDefault(d => d.Id == id);

    private static void BuildChips(Grid panel, int channels)
    {
        // cache key includes the theme version so a live theme change re-tints them
        if (panel.Tag is (int existing, int ver) && existing == channels && ver == AppTheme.Version) return;
        panel.Tag = (channels, AppTheme.Version);
        panel.Children.Clear();
        panel.RowDefinitions.Clear();

        // Chips fill the card's FULL height — one stretched lane per channel (the
        // "chip long side = one channel lane" rule, same as the matrix chip columns).
        var chipText = new SolidColorBrush(AppTheme.Mix(AppTheme.AccentHl, AppTheme.Text, 0.76));
        for (var i = 0; i < channels; i++)
        {
            panel.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
            var label = channels == 1 ? "M"
                : channels == 2 ? (i == 0 ? "L" : "R")
                : (i + 1).ToString();
            var chip = new Border
            {
                CornerRadius = new CornerRadius(AppTheme.RadiusMicro),
                BorderThickness = new Thickness(1),
                BorderBrush = AppTheme.LineStrongBrush,
                // same key-face recipe as the matrix chip columns — one themed look
                Background = AppTheme.KeyFace(),
                Margin = new Thickness(0, 0, 0, i < channels - 1 ? 6 : 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Child = new TextBlock
                {
                    Text = label,
                    FontSize = AppTheme.Fs2xs,
                    FontWeight = FontWeight.ExtraBold,
                    Foreground = chipText,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Grid.SetRow(chip, i);
            panel.Children.Add(chip);
        }
    }

    // =====================================================================
    // transient error banner
    // =====================================================================

    private void ShowBanner(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        BannerText.Text = message;
        if (!Banner.IsVisible)
        {
            // fade in with the shared motion timing (its Opacity transition)
            Banner.Opacity = 0;
            Banner.IsVisible = true;
            Dispatcher.UIThread.Post(() => Banner.Opacity = 1, DispatcherPriority.Render);
        }
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }
}
