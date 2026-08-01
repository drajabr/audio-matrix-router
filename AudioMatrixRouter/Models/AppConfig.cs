using System.Text.Json;
using System.Diagnostics;
using System.Text;

namespace AudioMatrixRouter.Models;

public class WindowConfig
{
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public int Width { get; set; } = 0;
    public int Height { get; set; } = 0;
    public bool StartMinimized { get; set; }
}

public class DeviceConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Channels { get; set; }
}

public class CrosspointConfig
{
    public int InCh { get; set; }
    public int OutCh { get; set; }
    public float GainDb { get; set; }
    public bool PhaseInverted { get; set; }
}

public class OutputLatencyConfig
{
    public string DeviceId { get; set; } = "";
    public int DelayMs { get; set; }
    public double BaseLatencyMs { get; set; }
}

public class DormantRouteConfig
{
    /// <summary>
    /// Routes for unavailable/disconnected devices. Keyed by "inputDeviceId|outputDeviceId".
    /// Each entry is an array of {inLocalChannel, outLocalChannel, gainDb}.
    /// </summary>
    public string InputDeviceId { get; set; } = "";
    public string OutputDeviceId { get; set; } = "";
    public int InputLocalChannel { get; set; }
    public int OutputLocalChannel { get; set; }
    public float GainDb { get; set; }
    public bool PhaseInverted { get; set; }
}

/// <summary>
/// Durable record of every device the user has ever routed, online or not. Unlike the
/// InputDevices/OutputDevices lists (which mirror the engine's current active set), an
/// entry here survives the device being offline at save time, so its name, channel
/// layout and per-device settings can be re-applied whenever it comes back. Pruned only
/// by explicit user removal of the device.
/// </summary>
public class KnownDeviceConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Channels { get; set; }
    public bool IsInput { get; set; }
    public bool IsLoopback { get; set; }
    public int OutputDelayMs { get; set; }
}

public class AppConfig
{
    public WindowConfig Window { get; set; } = new();
    public List<DeviceConfig> InputDevices { get; set; } = [];
    public List<DeviceConfig> OutputDevices { get; set; } = [];
    public List<CrosspointConfig> Crosspoints { get; set; } = [];
    public List<OutputLatencyConfig> OutputLatencies { get; set; } = [];
    /// <summary>
    /// Routes for devices that were previously configured but are not currently available.
    /// These routes are never removed automatically and are restored when devices reconnect.
    /// </summary>
    public List<DormantRouteConfig> DormantRoutes { get; set; } = [];
    public List<KnownDeviceConfig> KnownDevices { get; set; } = [];
    public bool Locked { get; set; }
    public bool StartupAtBoot { get; set; }
    public int InputBufferMs { get; set; } = 40;
    public int OutputBufferMs { get; set; } = 40;
    public string InputMasterDeviceId { get; set; } = "";
    public string OutputMasterDeviceId { get; set; } = "";
    public string InputDeviceMode { get; set; } = "both";
    public string UiPreferencesJson { get; set; } = "";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Config now lives in %APPDATA%\AudioMatrixRouter\config.json.
    ///
    /// The previous location (next to the executable) silently lost settings whenever the
    /// install directory was not writable (Program Files, read-only shares, sandboxed
    /// installs): Save() swallowed the exception and the user's routes/devices vanished
    /// on the next launch. %APPDATA% is always writable for the current user.
    /// </summary>
    public static string GetConfigPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            // Extremely unusual; fall back to the legacy exe-side location.
            return GetLegacyConfigPath();
        }
        return Path.Combine(appData, "AudioMatrixRouter", "config.json");
    }

    private static string GetLegacyConfigPath()
    {
        var exePath = Environment.ProcessPath ?? "";
        var dir = Path.GetDirectoryName(exePath) ?? ".";
        return Path.Combine(dir, "config.json");
    }

    public static AppConfig? Load()
    {
        // Preferred location first; fall back to the legacy exe-side file so existing
        // installs migrate their settings transparently on first run (the next Save()
        // writes to %APPDATA% and the legacy file is left untouched as a backup).
        foreach (var path in new[] { GetConfigPath(), GetLegacyConfigPath() })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<AppConfig>(json, _jsonOptions);
                if (config != null) return config;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppConfig] Load failed from '{path}': {ex}");
            }
        }
        return null;
    }

    public void Save()
    {
        try
        {
            var path = GetConfigPath();
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(this, _jsonOptions);
            var tempPath = path + ".tmp";
            var backupPath = path + ".bak";

            // Write-through temp file minimizes data loss on abrupt shutdown/reboot.
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppConfig] Save failed: {ex}");
        }
    }

    public static AppConfig FromEngine(Audio.AudioEngine engine, int winX, int winY, int winW, int winH, bool locked, bool startMinimized, bool startupAtBoot, string uiPreferencesJson, string inputDeviceMode, AppConfig? previousConfig = null)
    {
        var config = new AppConfig
        {
            Window = new WindowConfig { X = winX, Y = winY, Width = winW, Height = winH, StartMinimized = startMinimized },
            Locked = locked,
            StartupAtBoot = startupAtBoot,
            InputBufferMs = engine.InputBufferMs,
            OutputBufferMs = engine.OutputBufferMs,
            InputMasterDeviceId = engine.GetInputMasterDevice()?.Info.Id ?? "",
            // Persist the durable preference, not just the session flags — a master that is
            // currently offline must survive the save or it would never be promoted again.
            OutputMasterDeviceId = engine.PreferredOutputMasterId
                ?? engine.GetOutputMasterDevice()?.Info.Id ?? "",
            InputDeviceMode = inputDeviceMode is "input" or "loopback" or "both" ? inputDeviceMode : "both",
            UiPreferencesJson = uiPreferencesJson ?? ""
        };

        // Build sets of active device IDs for comparison with dormant routes
        var activeInputIds = new HashSet<string>();
        var activeOutputIds = new HashSet<string>();

        foreach (var d in engine.InputDevices)
        {
            config.InputDevices.Add(new DeviceConfig { Id = d.Info.Id, Name = d.Info.Name, Channels = d.Info.Channels });
            activeInputIds.Add(d.Info.Id);
        }
        foreach (var d in engine.OutputDevices)
        {
            var baseLatencyMs = d.BaseLatencyMs;
            config.OutputDevices.Add(new DeviceConfig { Id = d.Info.Id, Name = d.Info.Name, Channels = d.Info.Channels });
            config.OutputLatencies.Add(new OutputLatencyConfig { DeviceId = d.Info.Id, DelayMs = d.OutputDelayMs, BaseLatencyMs = baseLatencyMs });
            activeOutputIds.Add(d.Info.Id);
        }

        // Save active routes
        var mat = engine.RoutingMatrix;
        for (int i = 0; i < mat.InputChannels; i++)
            for (int o = 0; o < mat.OutputChannels; o++)
            {
                var cp = mat.GetCrosspoint(i, o);
                if (cp.Active)
                    config.Crosspoints.Add(new CrosspointConfig { InCh = i, OutCh = o, GainDb = mat.GetGainDb(i, o), PhaseInverted = cp.PhaseInverted });
            }

        // Preserve dormant routes from previous config. We persist EVERY known dormant
        // route, regardless of whether one or both of its endpoints happen to be active
        // right now. Reason: a route may be partially restorable (one peer reconnected,
        // the other still gone). The engine's RestoreDormantRoutesForXxxDevice helpers
        // remove an entry from _dormantRoutes only when both peers are present and the
        // crosspoint is actually re-established. Anything still in _dormantRoutes after
        // a save belongs on disk — otherwise reconnecting a single peer (input OR output)
        // would silently drop the route from the file and the next disconnect would
        // permanently forget it. Policy: a route is forgotten only by explicit user
        // deletion.
        if (previousConfig != null)
        {
            foreach (var dormant in previousConfig.DormantRoutes)
            {
                config.DormantRoutes.Add(dormant);
            }
        }

        // Also capture dormant routes from the engine itself (routes that were just stored
        // during device removal/refresh). Convert them to DormantRouteConfig format and
        // dedupe against any already carried over from previousConfig.
        foreach (var engineDormant in engine.DormantRoutes)
        {
            var existing = config.DormantRoutes.FirstOrDefault(d =>
                d.InputDeviceId == engineDormant.InputDeviceId &&
                d.InputLocalChannel == engineDormant.InputLocalChannel &&
                d.OutputDeviceId == engineDormant.OutputDeviceId &&
                d.OutputLocalChannel == engineDormant.OutputLocalChannel);

            if (existing == null)
            {
                config.DormantRoutes.Add(new DormantRouteConfig
                {
                    InputDeviceId = engineDormant.InputDeviceId,
                    InputLocalChannel = engineDormant.InputLocalChannel,
                    OutputDeviceId = engineDormant.OutputDeviceId,
                    OutputLocalChannel = engineDormant.OutputLocalChannel,
                    GainDb = engineDormant.GainDb,
                    PhaseInverted = engineDormant.PhaseInverted
                });
            }
            else
            {
                // Refresh in case the engine's copy is more recent than the previous file's.
                existing.GainDb = engineDormant.GainDb;
                existing.PhaseInverted = engineDormant.PhaseInverted;
            }
        }

        // The engine's dormant list is authoritative for deletions: a route the engine no
        // longer tracks as either live or dormant was explicitly deleted by the user (route
        // toggled off, routes cleared, or device removed) and must not resurrect from the
        // previous file. Keep only entries that are still live, still dormant in the engine,
        // or reference a device-channel pair the engine cannot currently resolve both ends
        // of (those are exactly the offline-device routes dormancy exists for).
        var engineDormantKeys = new HashSet<string>(engine.DormantRoutes.Select(r =>
            $"{r.InputDeviceId}|{r.InputLocalChannel}|{r.OutputDeviceId}|{r.OutputLocalChannel}"), StringComparer.Ordinal);
        config.DormantRoutes.RemoveAll(d =>
            activeInputIds.Contains(d.InputDeviceId) && activeOutputIds.Contains(d.OutputDeviceId)
            && !engineDormantKeys.Contains($"{d.InputDeviceId}|{d.InputLocalChannel}|{d.OutputDeviceId}|{d.OutputLocalChannel}"));

        // Preserve device metadata (Id/Name/Channels) for any device that is referenced by a
        // persisted dormant route but is not currently in the active list. Without this, the
        // device entry vanishes from disk the moment it's offline, which (a) loses the friendly
        // name + channel count needed by ApplyToEngine's saved-offset remap and (b) prevents
        // the auto-add-on-launch path in MainForm from re-adding the device when it returns.
        // Combined with the dormant-route persistence above, this gives the "never forget a
        // device that was ever used in a tile" guarantee.
        var dormantInputIds = config.DormantRoutes.Select(d => d.InputDeviceId).Distinct(StringComparer.Ordinal);
        var dormantOutputIds = config.DormantRoutes.Select(d => d.OutputDeviceId).Distinct(StringComparer.Ordinal);

        if (previousConfig != null)
        {
            foreach (var id in dormantInputIds)
            {
                if (activeInputIds.Contains(id)) continue;
                if (config.InputDevices.Any(d => d.Id == id)) continue;
                var prev = previousConfig.InputDevices.FirstOrDefault(d => d.Id == id);
                if (prev != null)
                {
                    config.InputDevices.Add(new DeviceConfig { Id = prev.Id, Name = prev.Name, Channels = prev.Channels });
                }
            }
            foreach (var id in dormantOutputIds)
            {
                if (activeOutputIds.Contains(id)) continue;
                if (config.OutputDevices.Any(d => d.Id == id)) continue;
                var prev = previousConfig.OutputDevices.FirstOrDefault(d => d.Id == id);
                if (prev != null)
                {
                    config.OutputDevices.Add(new DeviceConfig { Id = prev.Id, Name = prev.Name, Channels = prev.Channels });
                    var prevLatency = previousConfig.OutputLatencies.FirstOrDefault(l => l.DeviceId == id);
                    if (prevLatency != null)
                    {
                        config.OutputLatencies.Add(prevLatency);
                    }
                }
            }
        }

        // KnownDevices: durable union of everything known before plus everything active now.
        // Entries are only ever pruned by explicit user device removal (MainForm mutates its
        // in-memory config before saving), never by a device merely being offline.
        if (previousConfig != null)
        {
            foreach (var k in previousConfig.KnownDevices)
            {
                config.KnownDevices.Add(new KnownDeviceConfig
                {
                    Id = k.Id,
                    Name = k.Name,
                    Channels = k.Channels,
                    IsInput = k.IsInput,
                    IsLoopback = k.IsLoopback,
                    OutputDelayMs = k.OutputDelayMs
                });
            }
        }

        void UpsertKnown(string id, string name, int channels, bool isInput, bool isLoopback, int outputDelayMs)
        {
            var entry = config.KnownDevices.FirstOrDefault(k => k.Id == id && k.IsInput == isInput);
            if (entry == null)
            {
                config.KnownDevices.Add(new KnownDeviceConfig
                {
                    Id = id, Name = name, Channels = channels,
                    IsInput = isInput, IsLoopback = isLoopback, OutputDelayMs = outputDelayMs
                });
            }
            else
            {
                entry.Name = name;
                entry.Channels = channels;
                entry.IsLoopback = isLoopback;
                entry.OutputDelayMs = outputDelayMs;
            }
        }

        foreach (var d in engine.InputDevices)
            UpsertKnown(d.Info.Id, d.Info.Name, d.Info.Channels, isInput: true, d.IsLoopback, 0);
        foreach (var d in engine.OutputDevices)
            UpsertKnown(d.Info.Id, d.Info.Name, d.Info.Channels, isInput: false, isLoopback: false, d.OutputDelayMs);

        // Keep an OutputLatencies entry for every known-but-offline output so the delay
        // survives on disk and can be re-applied when the device reattaches.
        foreach (var k in config.KnownDevices)
        {
            if (k.IsInput || k.OutputDelayMs <= 0) continue;
            if (config.OutputLatencies.Any(l => l.DeviceId == k.Id)) continue;
            config.OutputLatencies.Add(new OutputLatencyConfig { DeviceId = k.Id, DelayMs = k.OutputDelayMs });
        }

        return config;
    }

    public void ApplyToEngine(Audio.AudioEngine engine)
    {
        engine.SetInputBufferMs(InputBufferMs > 0 ? InputBufferMs : 40);
        engine.SetOutputBufferMs(OutputBufferMs > 0 ? OutputBufferMs : 40);

        // Build saved offset maps using persisted config order and channel counts so saved
        // global crosspoint indices can be converted to device-relative routes even when
        // some devices are currently unavailable.
        var savedInputLayout = new List<(string Id, int Channels, int SavedOffset)>();
        int savedInAcc = 0;
        foreach (var d in InputDevices)
        {
            savedInputLayout.Add((d.Id, d.Channels, savedInAcc));
            savedInAcc += Math.Max(0, d.Channels);
        }

        var savedOutputLayout = new List<(string Id, int Channels, int SavedOffset)>();
        int savedOutAcc = 0;
        foreach (var d in OutputDevices)
        {
            savedOutputLayout.Add((d.Id, d.Channels, savedOutAcc));
            savedOutAcc += Math.Max(0, d.Channels);
        }

        // 1) Seed EVERY persisted route into the engine's dormant list, device-relative.
        // The AddXxxDevice restore hooks then activate whatever has both peers present;
        // everything else stays dormant in the engine, which is what lets the hotplug
        // refresh re-attach a device and restore its routes at any later point. (Before
        // this, dormant routes only ever existed after a runtime removal, so a restart
        // permanently killed the reattach path.)
        var seeds = new List<Audio.AudioEngine.RoutedCrosspoint>();
        foreach (var dormant in DormantRoutes)
        {
            seeds.Add(new Audio.AudioEngine.RoutedCrosspoint(
                dormant.InputDeviceId, dormant.InputLocalChannel,
                dormant.OutputDeviceId, dormant.OutputLocalChannel,
                true, dormant.GainDb, dormant.PhaseInverted));
        }
        foreach (var cp in Crosspoints)
        {
            var inDev = savedInputLayout.FirstOrDefault(d => cp.InCh >= d.SavedOffset && cp.InCh < d.SavedOffset + d.Channels);
            var outDev = savedOutputLayout.FirstOrDefault(d => cp.OutCh >= d.SavedOffset && cp.OutCh < d.SavedOffset + d.Channels);
            if (string.IsNullOrWhiteSpace(inDev.Id) || string.IsNullOrWhiteSpace(outDev.Id)) continue;

            seeds.Add(new Audio.AudioEngine.RoutedCrosspoint(
                inDev.Id, cp.InCh - inDev.SavedOffset,
                outDev.Id, cp.OutCh - outDev.SavedOffset,
                true, cp.GainDb, cp.PhaseInverted));
        }
        engine.SeedDormantRoutes(seeds);

        // 2) Durable master preference before adds, so the master is promoted the moment
        // its device is attached (or later, when it reappears).
        if (!string.IsNullOrWhiteSpace(OutputMasterDeviceId))
            engine.SetOutputMasterDevice(OutputMasterDeviceId);

        // 3) Add devices in saved order; unavailable ones are a no-op and stay pending in
        // the dormant list / KnownDevices for the hotplug refresh.
        foreach (var d in InputDevices)
            engine.AddInputDevice(d.Id);
        foreach (var d in OutputDevices)
            engine.AddOutputDevice(d.Id);

        // Devices known from earlier sessions but missing from the active lists (offline
        // at last save) — try them too.
        foreach (var k in KnownDevices)
        {
            if (string.IsNullOrWhiteSpace(k.Id)) continue;
            if (k.IsInput)
            {
                if (!InputDevices.Any(d => d.Id == k.Id)) engine.AddInputDevice(k.Id);
            }
            else
            {
                if (!OutputDevices.Any(d => d.Id == k.Id)) engine.AddOutputDevice(k.Id);
            }
        }

        foreach (var outputLatency in OutputLatencies)
        {
            engine.SetOutputDelayMs(outputLatency.DeviceId, outputLatency.DelayMs);
        }

        if (!string.IsNullOrWhiteSpace(InputMasterDeviceId))
            engine.SetInputMasterDevice(InputMasterDeviceId);
    }

}
