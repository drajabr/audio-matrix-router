using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
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
    private bool _showAll;
    private bool _powerOn = true;
    private bool _mutedAll;
    private double _labelSquare = AppTheme.LabelSquareDefault;
    private string? _hoverRowKey;
    private string? _hoverColKey;

    private bool _allowClose;
    private bool _shutdownDone;

    private enum UpdateState { Idle, Checking, Current, Available, Downloading, Ready, Portable, Error }
    private UpdateState _updateState = UpdateState.Idle;
    private string _updateVersion = "";
    private int _updatePercent;

    /// <summary>Raised when the app must really exit (update apply armed a restart).</summary>
    public event Action? QuitRequested;

    public bool StartupAtBoot => _controller.StartupAtBoot;

    public MainWindow()
    {
        InitializeComponent();

        VersionText.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

        _controller = new AppController(action => Dispatcher.UIThread.Post(action));
        _controller.UpdateDownloadProgress += OnUpdateDownloadProgress;
        // Initialize BEFORE subscribing StateChanged: it raises the event synchronously
        // and the handler needs _prefs/_model, which don't exist yet at this point.
        _controller.Initialize();

        _prefs = new UiPreferences(_controller);
        ApplyPreferences();
        RestoreWindowPlacement();

        _controller.StateChanged += OnStateChanged;

        _snapshot = _controller.GetSnapshot();
        SyncFromSnapshot();

        WireHeader();
        WireCorner();
        WireMatrix();

        RebuildModel();
        UpdateCornerVisuals();
        UpdateDockCards();

        _metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _metricsTimer.Tick += (_, _) => OnMetricsTick();
        _metricsTimer.Start();

        _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _bannerTimer.Tick += (_, _) =>
        {
            _bannerTimer.Stop();
            Banner.IsVisible = false;
        };

        // Window bounds → controller config, debounced. NOTE (integrator seam): the
        // AppController contract has SetWindowBounds but no getter, so restoring the
        // previous placement needs either a controller-side restore or a new getter.
        _boundsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _boundsTimer.Tick += (_, _) =>
        {
            _boundsTimer.Stop();
            SaveBounds();
        };
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

    private void ApplyPreferences()
    {
        _viewMode = _prefs.ViewMode;
        _masterGainDb = ClampDb((float)_prefs.MasterGainDb);
        _showAll = _prefs.ShowAllDevices;
        _powerOn = _prefs.PowerOn;
        _labelSquare = Math.Clamp(_prefs.LabelSquare, AppTheme.LabelSquareMin, AppTheme.LabelSquareMax);

        // TODO: label-square drag-resize — fixed from prefs for now.
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
            var (x, y, w, h, _) = _controller.GetWindowBounds();
            if (w > 300 && h > 200)
            {
                Width = w;
                Height = h;
                if (x > int.MinValue && y > int.MinValue && x > -10000 && y > -10000 && (x != 0 || y != 0))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Position = new PixelPoint(x, y);
                }
            }
        }
        catch
        {
            // Bad persisted geometry must never block startup.
        }
    }

    private void KickBoundsSave()
    {
        _boundsTimer.Stop();
        _boundsTimer.Start();
    }

    private void SaveBounds()
    {
        if (_controller is null || WindowState == WindowState.Minimized) return;
        try
        {
            var size = FrameSize ?? ClientSize;
            _controller.SetWindowBounds(
                Position.X, Position.Y,
                (int)Math.Round(size.Width), (int)Math.Round(size.Height),
                startMinimized: !IsVisible);
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
    }

    private async Task HandleUpdateClickAsync()
    {
        try
        {
            if (_updateState is UpdateState.Checking or UpdateState.Downloading) return;

            if (_updateState == UpdateState.Available)
            {
                _updatePercent = 0;
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
                SetUpdateState(UpdateState.Available);
                return;
            }
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
        if (_updateState == UpdateState.Downloading)
            UpdateBtn.Content = $"{_updatePercent}%";
    }

    private void SetUpdateState(UpdateState state)
    {
        _updateState = state;
        UpdateBtn.Content = state switch
        {
            UpdateState.Checking => "…",
            UpdateState.Current => "✓",
            UpdateState.Available => $"Update v{_updateVersion}",
            UpdateState.Downloading => $"{_updatePercent}%",
            UpdateState.Ready => "Restart to update",
            UpdateState.Error => "!",
            UpdateState.Portable => "⟳",
            _ => "⟳",
        };
        ToolTip.SetTip(UpdateBtn, state switch
        {
            UpdateState.Checking => "Checking for updates…",
            UpdateState.Current => "Up to date",
            UpdateState.Available => $"Download v{_updateVersion}",
            UpdateState.Downloading => $"Downloading update {_updatePercent}%",
            UpdateState.Ready => "Restart to install the update",
            UpdateState.Portable => "Portable build — download the installer from GitHub Releases to enable in-app updates",
            UpdateState.Error => "Update check failed — click to retry",
            _ => "Check for updates",
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
            try { _controller.RefreshDevices(); }
            catch (Exception ex) { ShowBanner(ex.Message); }
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

        InDrum.ValueFormatter = v => v.ToString("0");
        OutDrum.ValueFormatter = v => v.ToString("0");
        GainDrum.ValueFormatter = v => v.ToString("+0.0;-0.0;0.0");

        InDrum.ValueCommitted += (_, v) =>
        {
            try { _controller.SetInputBufferMs((int)Math.Round(v)); }
            catch (Exception ex) { ShowBanner(ex.Message); }
        };
        OutDrum.ValueCommitted += (_, v) =>
        {
            try { _controller.SetOutputBufferMs((int)Math.Round(v)); }
            catch (Exception ex) { ShowBanner(ex.Message); }
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
        MuteBtn.Content = _mutedAll ? "🔇 MUTED" : "🔈 MUTE";
        InputModeBtn.Content = _snapshot.InputDeviceMode.ToUpperInvariant();
        InputModeBtn.Classes.Set("active", _snapshot.InputDeviceMode == "both");
        ToolTip.SetTip(InputModeBtn, $"Input device list mode: {_snapshot.InputDeviceMode} (click to cycle)");
    }

    private void OnMasterGainCommitted(float newMaster)
    {
        var oldMaster = _masterGainDb;
        _masterGainDb = ClampDb(newMaster);
        _prefs.MasterGainDb = _masterGainDb;

        // Re-send every active route with the master delta folded in (the engine only
        // knows absolute per-route gain — master gain is a UI concept, like App.jsx).
        var requests = new List<RouteRequest>();
        foreach (var route in _snapshot.Routes)
        {
            if (!TryMapRoute(route, out var inDev, out var inCh, out var outDev, out var outCh))
                continue;
            requests.Add(new RouteRequest(
                inDev.Id, inCh, outDev.Id, outCh,
                Active: true,
                GainDb: ClampDb(route.GainDb - oldMaster + _masterGainDb),
                PhaseInverted: route.PhaseInverted));
        }
        if (requests.Count > 0)
            ApplyRoutes(requests);
        else
            RebuildModel(); // gain readouts shift even with no routes staged
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

    private void OnMasterRequested(MatrixHeaderEvent e)
    {
        if (_snapshot.Locked) return;
        try
        {
            if (e.IsInput)
            {
                _controller.SetInputMaster(e.DeviceId);
                _prefs.InputMasterId = e.DeviceId;
            }
            else
            {
                _controller.SetOutputMaster(e.DeviceId);
                _prefs.OutputMasterId = e.DeviceId;
            }
        }
        catch (Exception ex)
        {
            ShowBanner(ex.Message);
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
        // Drum values follow the authoritative config (skip while unchanged so a
        // mid-drag value is not stomped for no reason).
        if (Math.Abs(InDrum.Value - _snapshot.InputBufferMs) >= 0.5)
            InDrum.Value = _snapshot.InputBufferMs;
        if (Math.Abs(OutDrum.Value - _snapshot.OutputBufferMs) >= 0.5)
            OutDrum.Value = _snapshot.OutputBufferMs;
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

            var cellGain = ClampDb(route.GainDb - _masterGainDb);
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

        var pool = new List<DeviceSnapshot>(configured);
        if (_showAll)
        {
            foreach (var device in available)
                if (!pool.Any(d => d.Id == device.Id))
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

    /// <summary>App.jsx getDeviceLabelParts, trimmed: text before "(" + first "(...)".</summary>
    private static (string Primary, string Hardware) SplitDeviceLabel(string raw)
    {
        raw = (raw ?? "").Trim();
        var open = raw.IndexOf('(');
        var primary = open > 0 ? raw[..open].Trim() : raw;
        var hardware = "";
        if (open >= 0)
        {
            var close = raw.IndexOf(')', open + 1);
            if (close > open)
                hardware = raw[(open + 1)..close].Trim();
        }
        if (hardware == primary) hardware = "";
        return (primary.Length > 0 ? primary : raw, hardware);
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
        MetricOutSync.Text = metrics.Outputs.Sum(d => d.SyncCorrections).ToString();
        MetricOutUnderruns.Text = metrics.Outputs.Sum(d => d.Underruns).ToString();
        MetricOutDrops.Text = metrics.Outputs.Sum(d => d.DroppedFrames).ToString();

        StatusText.Text = metrics.Running
            ? $"Running · {_totalLatencySmooth.Format(metrics.TotalLatencyMs)}"
            : "Standby";

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
            _smooth = _smooth is { } s ? s * 0.9 + v * 0.1 : v;
            var display = Math.Round(_smooth.Value, 1);
            if (_shown is not { } last || Math.Abs(display - last) >= 1.2 || now - _shownAt >= 900)
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

        // MASTER indicator simplified as an accent left border (TODO: full 20px edge
        // bar with badge-corner geometry per DESIGN-REFERENCE §3.3).
        StyleMasterCard(SourceCard, source?.IsMaster == true);
        StyleMasterCard(DestCard, dest?.IsMaster == true);

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
        RouteGlyph.Foreground = active ? AppTheme.AccentHlBrush : AppTheme.MutedBrush;
    }

    private static void StyleMasterCard(Border card, bool isMaster)
    {
        card.BorderThickness = isMaster ? new Thickness(3, 1, 1, 1) : new Thickness(1);
        card.BorderBrush = isMaster ? AppTheme.AccentBrush : AppTheme.LineBrush;
    }

    private static MatrixDeviceInfo? FindInfo(List<MatrixDeviceInfo>? infos, string? id) =>
        id is null ? null : infos?.FirstOrDefault(d => d.Id == id);

    private static void BuildChips(StackPanel panel, int channels)
    {
        if (panel.Tag is int existing && existing == channels) return;
        panel.Tag = channels;
        panel.Children.Clear();

        for (var i = 0; i < channels; i++)
        {
            var label = channels == 1 ? "M"
                : channels == 2 ? (i == 0 ? "L" : "R")
                : (i + 1).ToString();
            panel.Children.Add(new Border
            {
                Width = AppTheme.ChipShort - 8,
                Height = AppTheme.ChipShort - 8,
                CornerRadius = new CornerRadius(AppTheme.RadiusMicro),
                BorderThickness = new Thickness(1),
                BorderBrush = AppTheme.LineStrongBrush,
                Background = AppTheme.KeyFace(0.08, 0.14),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = label,
                    FontSize = 9,
                    FontWeight = FontWeight.ExtraBold,
                    Foreground = AppTheme.AccentHlBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
        panel.VerticalAlignment = VerticalAlignment.Center;
    }

    // =====================================================================
    // transient error banner
    // =====================================================================

    private void ShowBanner(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        BannerText.Text = message;
        Banner.IsVisible = true;
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }
}
