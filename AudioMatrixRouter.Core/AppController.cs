using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioMatrixRouter.Audio;
using AudioMatrixRouter.Models;
using NAudio.CoreAudioApi;

namespace AudioMatrixRouter;

/// <summary>
/// UI-agnostic application controller: owns the <see cref="AudioEngine"/>, the config
/// lifecycle, hotplug/save debouncing, boot-time startup retries and the Velopack
/// updater. This is the non-UI orchestration logic lifted out of the WinForms MainForm
/// so any host (WinForms/WebView today, Avalonia tomorrow) can drive the same engine.
///
/// Threading model: all public members are expected to be called on the host's UI
/// thread. Engine callbacks and timer ticks arrive on worker/MTA COM threads and are
/// marshaled back through the delegate supplied to the constructor before touching
/// controller state, so internal state is only ever mutated on the UI thread.
/// </summary>
public sealed class AppController : IDisposable
{
    private const string StartupShortcutName = "AudioMatrixRouter.lnk";

    // Boot resilience: at Windows startup WASAPI endpoints appear late (audio service,
    // USB/Bluetooth/HDMI drivers still initialising), so the first enumeration often
    // misses devices and the engine never starts. Retry with backoff until it does.
    private static readonly int[] StartupRetryDelaysMs = [2000, 4000, 8000, 15000, 30000, 60000];
    private const int DeviceRefreshDebounceMs = 250;
    private const int SaveDebounceMs = 350;

    private readonly Action<Action> _marshal;
    private readonly System.Threading.Timer _saveTimer;
    private readonly System.Threading.Timer _deviceRefreshTimer;
    private readonly System.Threading.Timer _startupRetryTimer;
    private int _startupRetryStage;

    private bool _locked;
    private bool _startupAtBoot;
    private string _uiPreferencesJson = "";
    private string _inputDeviceMode = "both";
    private bool _suppressConfigSave;
    // Suppresses intermediate state pushes while a multi-step engine operation is in
    // progress, so the host never sees half-applied route state that would overwrite
    // an optimistic UI update. The operation emits one final push when done.
    private bool _suppressStatePush;
    // Monotonic build counter stamped into every snapshot. The host ignores any state
    // whose rev is not newer than the last one it applied, which makes stale/racing
    // pushes harmless by construction.
    private long _stateRev;
    // In-memory canonical copy of the last saved config. Saves never re-read the file
    // from disk (a transient read failure used to silently drop every dormant route).
    private AppConfig? _lastSavedConfig;

    // Cached enumeration of system devices. WASAPI device enumeration + AudioClient
    // MixFormat queries are slow (COM activation per endpoint); refresh only on
    // hot-plug events / manual refresh, not every snapshot build.
    private List<DeviceInfo> _cachedAvailableInputs = [];
    private List<DeviceInfo> _cachedAvailableOutputs = [];
    private bool _availableDevicesDirty = true;

    // Window bounds cache written into the config on save. Seeded from the previous
    // config at Initialize so background saves never clobber persisted geometry when
    // the host has not reported bounds yet.
    private int _winX = -1;
    private int _winY = -1;
    private int _winW;
    private int _winH;
    private bool _startMinimized;
    private bool _winMaximized;

    // In-app updater (Velopack against the GitHub releases of drajabr/audio-matrix-router).
    private Velopack.UpdateManager? _updateManager;
    private Velopack.UpdateInfo? _pendingUpdate;
    private volatile bool _updateDownloaded;
    private int _updateBusy;

    private bool _initialized;
    private volatile bool _disposed;

    public AppController(Action<Action> marshalToUi)
    {
        _marshal = marshalToUi ?? throw new ArgumentNullException(nameof(marshalToUi));
        Engine = new Audio.AudioEngine();

        // Every timer body runs marshaled onto the UI thread; the timers themselves are
        // one-shot (armed via Change) so a burst of triggers coalesces into one tick.
        _saveTimer = new System.Threading.Timer(_ => _marshal(OnSaveTimerTick), null, Timeout.Infinite, Timeout.Infinite);
        _deviceRefreshTimer = new System.Threading.Timer(_ => _marshal(OnDeviceRefreshTick), null, Timeout.Infinite, Timeout.Infinite);
        _startupRetryTimer = new System.Threading.Timer(_ => _marshal(OnStartupRetryTick), null, Timeout.Infinite, Timeout.Infinite);
        // Mid-stream endpoint deaths: debounce (a hub drop kills several endpoints at
        // once) then force a full ReloadEngine — its unconditional Stop+Start is what
        // recovers a device that is dead but still present in the device list.
        _faultRestartTimer = new System.Threading.Timer(_ => _marshal(OnFaultRestartTick), null, Timeout.Infinite, Timeout.Infinite);
    }

    private const int FaultRestartDebounceMs = 300;
    private readonly System.Threading.Timer _faultRestartTimer;

    private void OnEngineFaulted()
    {
        if (_disposed) return;
        try { _faultRestartTimer.Change(FaultRestartDebounceMs, Timeout.Infinite); } catch { }
    }

    private void OnFaultRestartTick()
    {
        if (_disposed) return;
        ReloadEngine();
    }

    public Audio.AudioEngine Engine { get; }

    /// <summary>Raised (already marshaled to the UI thread) after any state change.</summary>
    public event Action<UiSnapshot>? StateChanged;

    /// <summary>Update download progress, 0..100, marshaled to the UI thread.</summary>
    public event Action<int>? UpdateDownloadProgress;

    public bool Locked => _locked;

    public bool StartupAtBoot => _startupAtBoot;

    public string UiPreferencesJson
    {
        get => _uiPreferencesJson;
        set
        {
            _uiPreferencesJson = value ?? "";
            ScheduleSave();
        }
    }

    // ---------------------------------------------------------------- lifecycle

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        Engine.Init();
        Engine.DevicesChanged += OnEngineDevicesChanged;
        Engine.StateChanged += OnEngineStateChanged;
        Engine.EngineFaulted += OnEngineFaulted;

        _suppressConfigSave = true;
        try
        {
            var loadedConfig = AppConfig.Load();
            _lastSavedConfig = loadedConfig;
            if (loadedConfig != null)
            {
                loadedConfig.ApplyToEngine(Engine);
                _locked = loadedConfig.Locked;
                _uiPreferencesJson = loadedConfig.UiPreferencesJson ?? "";
                _startupAtBoot = loadedConfig.StartupAtBoot;
                _inputDeviceMode = loadedConfig.InputDeviceMode is "input" or "loopback" or "both"
                    ? loadedConfig.InputDeviceMode
                    : "both";

                _winX = loadedConfig.Window.X;
                _winY = loadedConfig.Window.Y;
                _winW = loadedConfig.Window.Width;
                _winH = loadedConfig.Window.Height;
                _startMinimized = loadedConfig.Window.StartMinimized;
                _winMaximized = loadedConfig.Window.Maximized;

                _startupAtBoot = ApplyStartupAtBoot(_startupAtBoot) && _startupAtBoot;

                SyncDevicesWithSystem();
                TryAutoStart();

                // Some (or all) devices may simply not be ready yet — common right after
                // Windows boot. Keep retrying with backoff instead of requiring a manual
                // reload; each retry re-enumerates, re-attaches and auto-starts.
                if (!Engine.IsRunning
                    && (Engine.DormantRoutes.Count > 0 || Engine.RoutingMatrix.HasAnyCrosspoints()))
                {
                    _startupRetryStage = 0;
                    _startupRetryTimer.Change(StartupRetryDelaysMs[0], Timeout.Infinite);
                }
            }
            else
            {
                SyncDevicesWithSystem();
            }
        }
        finally
        {
            _suppressConfigSave = false;
        }

        RaiseStateChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Engine.DevicesChanged -= OnEngineDevicesChanged;
        Engine.StateChanged -= OnEngineStateChanged;
        Engine.EngineFaulted -= OnEngineFaulted;

        _saveTimer.Dispose();
        _deviceRefreshTimer.Dispose();
        _startupRetryTimer.Dispose();
        _faultRestartTimer.Dispose();

        Engine.Stop();
        Engine.Dispose();
    }

    // ---------------------------------------------------------------- engine callbacks

    private void OnEngineDevicesChanged()
    {
        if (_disposed) return;
        _availableDevicesDirty = true;
        try
        {
            // Always marshal: this callback arrives on an MTA COM thread. Re-arming the
            // one-shot timer restarts the debounce window so a burst of hotplug events
            // coalesces into a single refresh.
            _marshal(() =>
            {
                if (_disposed) return;
                _deviceRefreshTimer.Change(DeviceRefreshDebounceMs, Timeout.Infinite);
            });
        }
        catch (InvalidOperationException)
        {
            // Host torn down mid-callback.
        }
    }

    private void OnEngineStateChanged()
    {
        if (_disposed) return;
        // Capture the suppression flags now: during a suppressed multi-step operation the
        // engine raises this synchronously on the UI thread, but the marshaled body may
        // only run after the operation cleared the flags — which would leak the very
        // intermediate push suppression exists to prevent.
        bool save = !_suppressConfigSave;
        bool push = !_suppressStatePush;
        if (!save && !push) return;
        try
        {
            _marshal(() =>
            {
                if (_disposed) return;
                if (save) ScheduleSave();
                if (push) RaiseStateChanged();
            });
        }
        catch (InvalidOperationException)
        {
            // Host torn down mid-callback.
        }
    }

    // ---------------------------------------------------------------- timers

    private void OnSaveTimerTick()
    {
        if (_disposed) return;
        SaveConfig();
    }

    private void OnDeviceRefreshTick()
    {
        if (_disposed) return;
        _availableDevicesDirty = true;
        _suppressConfigSave = true;
        try
        {
            SyncDevicesWithSystem();
        }
        finally
        {
            _suppressConfigSave = false;
        }
        TryAutoStart();
        RaiseStateChanged();
    }

    private void OnStartupRetryTick()
    {
        if (_disposed) return;
        _suppressConfigSave = true;
        try
        {
            SyncDevicesWithSystem();
        }
        finally
        {
            _suppressConfigSave = false;
        }
        TryAutoStart();
        RaiseStateChanged();

        bool stillWaiting = !Engine.IsRunning
            && (Engine.DormantRoutes.Count > 0 || Engine.RoutingMatrix.HasAnyCrosspoints());
        if (stillWaiting && _startupRetryStage < StartupRetryDelaysMs.Length - 1)
        {
            _startupRetryStage++;
            _startupRetryTimer.Change(StartupRetryDelaysMs[_startupRetryStage], Timeout.Infinite);
        }
    }

    // ---------------------------------------------------------------- device sync

    private void SyncDevicesWithSystem()
    {
        _availableDevicesDirty = true;
        Engine.RefreshDevices();
        ApplyKnownDeviceSettings();
    }

    /// <summary>
    /// Re-applies persisted per-device settings (currently the output delay) to devices
    /// that just (re)attached. A freshly added ActiveDevice starts with defaults; without
    /// this, replugging a device silently loses its configured delay until app restart.
    /// </summary>
    private void ApplyKnownDeviceSettings()
    {
        var known = _lastSavedConfig?.KnownDevices;
        if (known == null || known.Count == 0) return;

        foreach (var k in known)
        {
            if (k.IsInput || k.OutputDelayMs <= 0) continue;
            var dev = Engine.OutputDevices.FirstOrDefault(d => d.Info.Id == k.Id);
            if (dev != null && dev.OutputDelayMs == 0)
            {
                Engine.SetOutputDelayMs(k.Id, k.OutputDelayMs);
            }
        }
    }

    private void TryAutoStart()
    {
        if (Engine.IsRunning) return;
        if (Engine.InputDevices.Count == 0 || Engine.OutputDevices.Count == 0) return;
        if (!Engine.RoutingMatrix.HasAnyCrosspoints()) return;
        Engine.Start();
    }

    public void RefreshDevices()
    {
        _availableDevicesDirty = true;
        _suppressConfigSave = true;
        try
        {
            SyncDevicesWithSystem();
        }
        finally
        {
            _suppressConfigSave = false;
        }
        TryAutoStart();
        RaiseStateChanged();
    }

    /// <summary>
    /// The reload key: re-enumerate devices, re-apply their saved settings, and bounce
    /// the audio path. <see cref="RefreshDevices"/> alone only reacts to devices coming
    /// and going — with the engine already running it starts nothing and stops nothing,
    /// so pressing reload had no observable effect. This always restarts a running
    /// engine, which is the point of the button: recover a wedged audio path.
    /// </summary>
    public void ReloadEngine()
    {
        _availableDevicesDirty = true;
        _suppressConfigSave = true;
        try
        {
            SyncDevicesWithSystem();
        }
        finally
        {
            _suppressConfigSave = false;
        }

        if (Engine.IsRunning) Engine.Stop();
        TryAutoStart();
        RaiseStateChanged();
    }

    // ---------------------------------------------------------------- config

    private void ScheduleSave()
    {
        if (_disposed) return;
        _saveTimer.Change(SaveDebounceMs, Timeout.Infinite);
    }

    private void SaveConfig()
    {
        var config = AppConfig.FromEngine(
            Engine,
            _winX, _winY, _winW, _winH,
            _locked, _startMinimized, _startupAtBoot,
            _uiPreferencesJson, _inputDeviceMode,
            _lastSavedConfig, _winMaximized);
        config.Save();
        _lastSavedConfig = config;
    }

    /// <summary>Synchronous save now (used at shutdown). Cancels any pending debounce.</summary>
    public void FlushSave()
    {
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        SaveConfig();
    }

    /// <summary>Cache the host window geometry (always the NORMAL-state rect — the host
    /// tracks it through maximize/hide); it is written into the config on save.</summary>
    public void SetWindowBounds(int x, int y, int w, int h, bool startMinimized, bool maximized = false)
    {
        bool changed = _winX != x || _winY != y || _winW != w || _winH != h
                       || _startMinimized != startMinimized || _winMaximized != maximized;
        _winX = x;
        _winY = y;
        _winW = w;
        _winH = h;
        _startMinimized = startMinimized;
        _winMaximized = maximized;
        // Persist geometry changes on their own: a hard reboot skips the shutdown flush,
        // and a session where ONLY the window moved used to leave the file stale.
        if (changed) ScheduleSave();
    }

    /// <summary>Last persisted window geometry (seeded from config at Initialize) so the
    /// host can restore its placement.</summary>
    public (int X, int Y, int W, int H, bool StartMinimized, bool Maximized) GetWindowBounds() =>
        (_winX, _winY, _winW, _winH, _startMinimized, _winMaximized);

    // ---------------------------------------------------------------- snapshots

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(BuildSnapshot());
    }

    public UiSnapshot GetSnapshot() => BuildSnapshot();

    private void EnsureAvailableDevicesCached()
    {
        if (!_availableDevicesDirty) return;
        try
        {
            bool includeCapture = _inputDeviceMode != "loopback";
            bool includeLoopback = _inputDeviceMode != "input";
            _cachedAvailableInputs = Engine.GetAvailableInputDevices(includeCapture, includeLoopback);
            _cachedAvailableOutputs = Engine.GetAvailableDevices(DataFlow.Render);
        }
        catch
        {
            // Fall back to whatever is currently cached.
        }
        _availableDevicesDirty = false;
    }

    private UiSnapshot BuildSnapshot()
    {
        var matrix = Engine.RoutingMatrix;
        var routes = new List<RouteSnapshot>();
        for (int inCh = 0; inCh < matrix.InputChannels; inCh++)
        {
            for (int outCh = 0; outCh < matrix.OutputChannels; outCh++)
            {
                var cp = matrix.GetCrosspoint(inCh, outCh);
                if (!cp.Active) continue;
                routes.Add(new RouteSnapshot(inCh, outCh, matrix.GetGainDb(inCh, outCh), cp.PhaseInverted));
            }
        }

        var inputs = new List<DeviceSnapshot>(Engine.InputDevices.Count);
        var activeInputIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in Engine.InputDevices)
        {
            activeInputIds.Add(d.Info.Id);
            inputs.Add(new DeviceSnapshot(
                d.Info.Id, d.Info.Name, d.Info.Channels, d.GlobalChannelOffset,
                d.IsMasterDevice, 0, d.Info.SampleRate, d.IsLoopback, IsActive: true));
        }

        var outputs = new List<DeviceSnapshot>(Engine.OutputDevices.Count);
        var activeOutputIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in Engine.OutputDevices)
        {
            activeOutputIds.Add(d.Info.Id);
            outputs.Add(new DeviceSnapshot(
                d.Info.Id, d.Info.Name, d.Info.Channels, d.GlobalChannelOffset,
                d.IsMasterDevice, d.OutputDelayMs, d.Info.SampleRate, IsLoopback: false, IsActive: true));
        }

        EnsureAvailableDevicesCached();

        var availableInputs = new List<DeviceSnapshot>(_cachedAvailableInputs.Count);
        foreach (var d in _cachedAvailableInputs)
        {
            availableInputs.Add(new DeviceSnapshot(
                d.Id, d.Name, d.Channels, 0, false, 0, d.SampleRate,
                IsLoopback: d.Id.StartsWith("loop:", StringComparison.Ordinal),
                IsActive: activeInputIds.Contains(d.Id)));
        }

        var availableOutputs = new List<DeviceSnapshot>(_cachedAvailableOutputs.Count);
        foreach (var d in _cachedAvailableOutputs)
        {
            availableOutputs.Add(new DeviceSnapshot(
                d.Id, d.Name, d.Channels, 0, false, 0, d.SampleRate,
                IsLoopback: false,
                IsActive: activeOutputIds.Contains(d.Id)));
        }

        return new UiSnapshot
        {
            // Build-order stamp: the host only applies a snapshot whose rev is newer
            // than the last one it applied, so stale pushes can never win.
            Rev = Interlocked.Increment(ref _stateRev),
            Running = Engine.IsRunning,
            Locked = _locked,
            StartupAtBoot = _startupAtBoot,
            InputBufferMs = Engine.InputBufferMs,
            OutputBufferMs = Engine.OutputBufferMs,
            InputDeviceMode = _inputDeviceMode,
            Inputs = inputs,
            Outputs = outputs,
            AvailableInputs = availableInputs,
            AvailableOutputs = availableOutputs,
            Routes = routes
        };
    }

    /// <summary>
    /// Lightweight time-varying telemetry sampled at the host's cadence: peaks, latencies,
    /// jitter and per-device counters. Deliberately carries NO routes or device lists, so
    /// a metrics tick can never overwrite route state in the host. Peak sampling is
    /// destructive (sample-and-reset), which is why it lives ONLY here and never in
    /// BuildSnapshot — RPC replies used to steal peak samples and make the meters dip.
    /// </summary>
    public MetricsSnapshot GetMetrics()
    {
        var masterOutput = Engine.GetOutputMasterDevice();
        var preferredConsumerId = string.IsNullOrWhiteSpace(masterOutput?.ConsumerId)
            ? (masterOutput?.Info.Id ?? string.Empty)
            : masterOutput!.ConsumerId;

        // Per-input sync-correction totals in one pass (no LINQ per input per tick).
        var syncCorrectionsByInput = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var outDev in Engine.OutputDevices)
        {
            if (outDev.MixProvider == null) continue;
            foreach (var inDev in Engine.InputDevices)
            {
                long count = outDev.MixProvider.GetInputSyncCorrectionCount(inDev.Info.Id);
                if (count == 0) continue;
                syncCorrectionsByInput[inDev.Info.Id] =
                    syncCorrectionsByInput.GetValueOrDefault(inDev.Info.Id) + count;
            }
        }

        double? maxWorkingLatencyMs = null;
        var routeLatencies = new List<RouteLatency>();
        foreach (var (inCh, outCh, latencyMs) in Engine.GetActiveRouteLatencies())
        {
            var rounded = Math.Round(latencyMs, 1);
            maxWorkingLatencyMs = maxWorkingLatencyMs.HasValue
                ? Math.Max(maxWorkingLatencyMs.Value, rounded)
                : rounded;
            routeLatencies.Add(new RouteLatency(inCh, outCh, rounded));
        }

        var inputMetrics = new List<DeviceMetrics>(Engine.InputDevices.Count);
        foreach (var d in Engine.InputDevices)
        {
            inputMetrics.Add(new DeviceMetrics
            {
                DeviceId = d.Info.Id,
                PeakLevels = SampleAndResetPeaks(d.PeakLevels),
                Overflows = Interlocked.Read(ref d.InputOverflowCount),
                DroppedFrames = !string.IsNullOrWhiteSpace(preferredConsumerId)
                    ? (d.RingBuffer?.GetDroppedFramesForConsumer(preferredConsumerId) ?? 0)
                    : (d.RingBuffer?.TotalFramesDropped ?? 0),
                SyncCorrections = syncCorrectionsByInput.GetValueOrDefault(d.Info.Id),
                PeriodMs = d.Capture is { } cap ? Math.Round(cap.ActualPeriodMs, 2) : 0,
                TierName = d.Capture?.Tier.ToString() ?? ""
            });
        }

        var outputMetrics = new List<DeviceMetrics>(Engine.OutputDevices.Count);
        foreach (var d in Engine.OutputDevices)
        {
            outputMetrics.Add(new DeviceMetrics
            {
                DeviceId = d.Info.Id,
                PeakLevels = d.MixProvider?.SamplePeakLevels() ?? [],
                Underruns = d.MixProvider?.UnderrunCount ?? 0,
                DroppedFrames = d.MixProvider?.DroppedFrames ?? 0,
                LatencyMs = d.RenderLatencyMs > 0
                    ? Math.Round((double)d.RenderLatencyMs + d.OutputDelayMs, 1)
                    : null,
                VariationRangeMs = d.MixProvider?.OutputVariationRangeMs,
                SyncErrorMs = d.MixProvider?.OutputSyncErrorMs,
                AppliedPpm = d.MixProvider?.OutputAppliedPpm,
                FastCatchUpActive = d.MixProvider?.FastCatchUpActive ?? false,
                FastCatchUpDutyPercent = d.MixProvider?.FastCatchUpDutyPercent ?? 0,
                PostRecoveryUnderruns = d.MixProvider?.PostRecoveryUnderruns ?? 0,
                PeriodMs = d.Render is { } rnd ? Math.Round(rnd.ActualPeriodMs, 2) : 0,
                TierName = d.Render?.Tier.ToString() ?? ""
            });
        }

        // Input-side figure = the worst ROUTED capture+queue path, so it decomposes the
        // same routes the total measures (the master input may be an idle loopback whose
        // queue means nothing). Output-side = everything that is NOT the input path —
        // the raw render-buffer number was a constant equal to the knob setting.
        double? inputSide = Engine.TryGetRoutedInputPathLatencyMs(out var routedInputLatency)
            ? Math.Round(routedInputLatency, 1)
            : Engine.TryGetInputPathLatencyMs(out var inputPathLatency)
                ? Math.Round(inputPathLatency, 1)
                : null;
        double? outputSide = maxWorkingLatencyMs is { } total && inputSide is { } inp
            ? Math.Max(0, Math.Round(total - inp, 1))
            : Engine.TryGetOutputPathLatencyMs(out var outputPathLatency)
                ? Math.Round(outputPathLatency, 1)
                : null;

        return new MetricsSnapshot
        {
            Running = Engine.IsRunning,
            TotalLatencyMs = maxWorkingLatencyMs,
            InputLatencyMs = inputSide,
            OutputLatencyMs = outputSide,
            InputJitterMs = Engine.TryGetInputJitterMs(out var inputJitter)
                ? Math.Round(inputJitter, 1)
                : null,
            Inputs = inputMetrics,
            Outputs = outputMetrics,
            RouteLatencies = routeLatencies
        };
    }

    private static float[] SampleAndResetPeaks(float[]? peaks)
    {
        if (peaks == null || peaks.Length == 0) return [];
        var snapshot = new float[peaks.Length];
        for (int i = 0; i < peaks.Length; i++)
        {
            snapshot[i] = peaks[i];
            peaks[i] = 0f;
        }
        return snapshot;
    }

    // ---------------------------------------------------------------- routes

    /// <summary>
    /// Applies a batch of device-relative crosspoint changes. Referenced devices are
    /// auto-added to the engine (this is how user-selected devices become active — there
    /// is no separate add step). Returns human-readable route errors for ACTIVE routes
    /// whose device/channel cannot be resolved; such routes are dropped, never written
    /// to a stale index.
    /// </summary>
    public List<string> SetRoutes(IReadOnlyList<RouteRequest> routes)
    {
        if (_locked) return ["Matrix is locked"];

        var routeErrors = new List<string>();
        bool devicesChanged = false;

        // Suppress intermediate pushes: device adds fire StateChanged with the OLD route
        // state, which would overwrite the optimistic toggle in the host's UI. One
        // authoritative push goes out at the end.
        _suppressStatePush = true;
        try
        {
            // Device batch: at most ONE engine restart for the whole operation instead
            // of one per added device plus one extra.
            using (Engine.BeginDeviceBatch())
            {
                foreach (var route in routes)
                {
                    if (!string.IsNullOrEmpty(route.InDeviceId) && route.Active)
                    {
                        if (Engine.AddInputDevice(route.InDeviceId)) devicesChanged = true;
                    }
                    if (!string.IsNullOrEmpty(route.OutDeviceId) && route.Active)
                    {
                        if (Engine.AddOutputDevice(route.OutDeviceId)) devicesChanged = true;
                    }
                }

                var updates = new List<(int InCh, int OutCh, bool Active, float GainDb, bool PhaseInverted)>();
                foreach (var route in routes)
                {
                    int inGlobal = -1;
                    int outGlobal = -1;

                    if (!string.IsNullOrEmpty(route.InDeviceId))
                    {
                        var inDev = Engine.InputDevices.FirstOrDefault(d => d.Info.Id == route.InDeviceId);
                        if (inDev != null && route.InChannel >= 0 && route.InChannel < inDev.Info.Channels)
                        {
                            inGlobal = inDev.GlobalChannelOffset + route.InChannel;
                        }
                        else if (route.Active)
                        {
                            // Do NOT fall back to a stale global index — that writes the
                            // crosspoint somewhere wrong. Report it.
                            routeErrors.Add($"Input '{route.InDeviceId}' channel {route.InChannel + 1} is not available");
                            continue;
                        }
                        else
                        {
                            continue;
                        }
                    }

                    if (!string.IsNullOrEmpty(route.OutDeviceId))
                    {
                        var outDev = Engine.OutputDevices.FirstOrDefault(d => d.Info.Id == route.OutDeviceId);
                        if (outDev != null && route.OutChannel >= 0 && route.OutChannel < outDev.Info.Channels)
                        {
                            outGlobal = outDev.GlobalChannelOffset + route.OutChannel;
                        }
                        else if (route.Active)
                        {
                            routeErrors.Add($"Output '{route.OutDeviceId}' channel {route.OutChannel + 1} is not available");
                            continue;
                        }
                        else
                        {
                            continue;
                        }
                    }

                    if (inGlobal < 0 || outGlobal < 0) continue;
                    updates.Add((inGlobal, outGlobal, route.Active, route.GainDb, route.PhaseInverted));
                }

                var (changed, skipped) = Engine.SetCrosspoints(updates);
                if (skipped > 0)
                {
                    routeErrors.Add($"{skipped} route(s) were out of range and skipped");
                }
                if (changed > 0 || devicesChanged)
                {
                    ScheduleSave();
                }
            } // batch dispose: restarts once if a device add stopped the engine

            if (Engine.RoutingMatrix.HasAnyCrosspoints())
            {
                if (!Engine.IsRunning)
                {
                    Engine.Start();
                }
            }
            else if (Engine.IsRunning)
            {
                Engine.Stop();
            }
        }
        finally
        {
            _suppressStatePush = false;
        }

        RaiseStateChanged();
        return routeErrors;
    }

    public void ClearRoutes()
    {
        if (_locked) return;
        Engine.ClearCrosspoints();
        ScheduleSave();
        RaiseStateChanged();
    }

    // ---------------------------------------------------------------- simple mutators

    public void SetLocked(bool locked)
    {
        _locked = locked;
        ScheduleSave();
        RaiseStateChanged();
    }

    /// <summary>No lock check — mute must work even when the UI is locked. Not persisted.</summary>
    public void SetTransientMuteAll(bool muted)
    {
        Engine.RoutingMatrix.TransientMuteAll = muted;
        RaiseStateChanged();
    }

    public void SetEngineEnabled(bool enabled)
    {
        if (enabled)
        {
            if (Engine.RoutingMatrix.HasAnyCrosspoints())
            {
                Engine.Start();
            }
        }
        else
        {
            Engine.Stop();
        }
        RaiseStateChanged();
    }

    /// <summary>Returns false when the device is not currently attached to the engine —
    /// the preference is still remembered and promoted when the device appears.</summary>
    public bool SetInputMaster(string deviceId)
    {
        bool applied = Engine.SetInputMasterDevice(deviceId ?? string.Empty);
        ScheduleSave();
        RaiseStateChanged();
        return applied;
    }

    /// <summary>Returns false when the device is not currently attached to the engine —
    /// the preference is still remembered and promoted when the device appears.</summary>
    public bool SetOutputMaster(string deviceId)
    {
        bool applied = Engine.SetOutputMasterDevice(deviceId ?? string.Empty);
        ScheduleSave();
        RaiseStateChanged();
        return applied;
    }

    public void SetOutputDelayMs(string deviceId, int delayMs)
    {
        Engine.SetOutputDelayMs(deviceId ?? string.Empty, delayMs);
        ScheduleSave();
        RaiseStateChanged();
    }

    public void SetInputBufferMs(int ms)
    {
        Engine.SetInputBufferMs(ms);
        ScheduleSave();
        RaiseStateChanged();
    }

    public void SetOutputBufferMs(int ms)
    {
        Engine.SetOutputBufferMs(ms);
        ScheduleSave();
        RaiseStateChanged();
    }

    public void SetInputDeviceMode(string mode)
    {
        if (mode is not ("input" or "loopback" or "both")) return;
        _inputDeviceMode = mode;
        _availableDevicesDirty = true;
        ScheduleSave();
        RaiseStateChanged();
    }

    public void RemoveInputDevice(string deviceId)
    {
        var id = deviceId ?? string.Empty;
        // Explicit user removal: forget the device entirely — dormant routes
        // (RemoveInputDevice re-captures them, so prune after) and its KnownDevices
        // entry, or it would resurrect on restart.
        Engine.RemoveInputDevice(id);
        Engine.RemoveDormantRoutesFor(id);
        _lastSavedConfig?.KnownDevices.RemoveAll(k => k.Id == id && k.IsInput);
        ScheduleSave();
        RaiseStateChanged();
    }

    public void RemoveOutputDevice(string deviceId)
    {
        var id = deviceId ?? string.Empty;
        Engine.RemoveOutputDevice(id);
        Engine.RemoveDormantRoutesFor(id);
        _lastSavedConfig?.KnownDevices.RemoveAll(k => k.Id == id && !k.IsInput);
        ScheduleSave();
        RaiseStateChanged();
    }

    // ---------------------------------------------------------------- startup at boot

    public bool SetStartupAtBoot(bool enabled)
    {
        if (!ApplyStartupAtBoot(enabled)) return false;
        _startupAtBoot = enabled;
        ScheduleSave();
        RaiseStateChanged();
        return true;
    }

    private static string GetStartupShortcutPath()
    {
        var startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        return Path.Combine(startupDir, StartupShortcutName);
    }

    private static bool ApplyStartupAtBoot(bool enabled)
    {
        try
        {
            var shortcutPath = GetStartupShortcutPath();
            if (enabled)
            {
                CreateStartupShortcut(shortcutPath);
            }
            else if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CreateStartupShortcut(string shortcutPath)
    {
        var executablePath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Cannot resolve the executable path");

        // Use WScript.Shell COM object to create the shortcut.
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
            throw new InvalidOperationException("WScript.Shell COM object not available");

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);

        shortcut.TargetPath = executablePath;
        shortcut.Arguments = "--startup";
        shortcut.WorkingDirectory = Path.GetDirectoryName(executablePath);
        shortcut.Description = "Audio Matrix Router";

        shortcut.Save();

        Marshal.FinalReleaseComObject(shortcut);
        Marshal.FinalReleaseComObject(shell);
    }

    // ---------------------------------------------------------------- updater

    private static string AppVersionString =>
        typeof(AppController).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private Velopack.UpdateManager GetUpdateManager()
    {
        if (_updateManager != null) return _updateManager;

        // AMR_UPDATE_URL overrides the update feed with a local folder or plain URL —
        // lets the whole check/download/apply cycle be tested against a local `vpk pack`
        // output without publishing a GitHub release.
        var overrideUrl = Environment.GetEnvironmentVariable("AMR_UPDATE_URL");
        _updateManager = !string.IsNullOrWhiteSpace(overrideUrl)
            ? new Velopack.UpdateManager(overrideUrl)
            : new Velopack.UpdateManager(
                new Velopack.Sources.GithubSource("https://github.com/drajabr/audio-matrix-router", null, false));
        return _updateManager;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        var mgr = GetUpdateManager();
        if (!mgr.IsInstalled)
        {
            // Portable/dev run — Velopack can't update in place.
            return new UpdateCheckResult(AppVersionString, null, Portable: true);
        }

        var info = await Task.Run(() => mgr.CheckForUpdatesAsync()).ConfigureAwait(false);
        _pendingUpdate = info;
        _updateDownloaded = false;
        return new UpdateCheckResult(
            mgr.CurrentVersion?.ToString() ?? AppVersionString,
            info?.TargetFullRelease?.Version?.ToString(),
            Portable: false,
            DownloadBytes: EstimateDownloadBytes(info));
    }

    /// <summary>What the download will actually pull: the delta chain when one exists
    /// (the usual case between consecutive releases), otherwise the full package.</summary>
    private static long EstimateDownloadBytes(Velopack.UpdateInfo? info)
    {
        if (info is null) return 0;
        try
        {
            var deltas = info.DeltasToTarget;
            if (deltas is { Length: > 0 })
            {
                long sum = 0;
                foreach (var d in deltas) sum += d.Size;
                if (sum > 0) return sum;
            }
            return info.TargetFullRelease?.Size ?? 0;
        }
        catch
        {
            return 0; // size is cosmetic — never let it break the check
        }
    }

    public async Task DownloadUpdateAsync()
    {
        var info = _pendingUpdate
            ?? throw new InvalidOperationException("No update pending — check for updates first");
        if (Interlocked.Exchange(ref _updateBusy, 1) == 1)
        {
            throw new InvalidOperationException("An update download is already in progress");
        }

        try
        {
            int lastPercent = -1;
            await Task.Run(() => GetUpdateManager().DownloadUpdatesAsync(info, percent =>
            {
                if (percent == lastPercent) return;
                lastPercent = percent;
                try
                {
                    _marshal(() =>
                    {
                        if (_disposed) return;
                        UpdateDownloadProgress?.Invoke(percent);
                    });
                }
                catch (InvalidOperationException)
                {
                    // Host torn down mid-download.
                }
            })).ConfigureAwait(false);
            _updateDownloaded = true;
        }
        finally
        {
            Interlocked.Exchange(ref _updateBusy, 0);
        }
    }

    public bool CanApplyUpdate => _pendingUpdate != null && _updateDownloaded;

    /// <summary>
    /// Arms Velopack to wait for OUR process to exit, apply the downloaded update
    /// silently and relaunch. The caller is responsible for closing the app afterwards.
    /// </summary>
    public bool ApplyUpdateAndArmRestart()
    {
        var pending = _pendingUpdate;
        if (pending == null || !_updateDownloaded) return false;
        GetUpdateManager().WaitExitThenApplyUpdates(pending, silent: true, restart: true);
        return true;
    }
}
