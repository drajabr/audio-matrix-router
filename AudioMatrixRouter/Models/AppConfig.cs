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

    public static string GetConfigPath()
    {
        var exePath = Environment.ProcessPath ?? "";
        var dir = Path.GetDirectoryName(exePath) ?? ".";
        return Path.Combine(dir, "config.json");
    }

    public static AppConfig? Load()
    {
        var path = GetConfigPath();
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json, _jsonOptions);
        }
        catch { return null; }
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
            OutputMasterDeviceId = engine.GetOutputMasterDevice()?.Info.Id ?? "",
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
                    config.Crosspoints.Add(new CrosspointConfig { InCh = i, OutCh = o, GainDb = mat.GetGainDb(i, o) });
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
                    GainDb = engineDormant.GainDb
                });
            }
            else
            {
                // Refresh gain in case the engine's copy is more recent than the previous file's.
                existing.GainDb = engineDormant.GainDb;
            }
        }

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

        return config;
    }

    public void ApplyToEngine(Audio.AudioEngine engine)
    {
        engine.SetInputBufferMs(InputBufferMs > 0 ? InputBufferMs : 40);
        engine.SetOutputBufferMs(OutputBufferMs > 0 ? OutputBufferMs : 40);

        // Honor the user's configured active device lists exactly as saved, in saved order.
        // If a saved device is unavailable on this machine right now, skip it at runtime,
        // but do not infer replacements or expand the config with other system devices.
        foreach (var d in InputDevices)
            engine.AddInputDevice(d.Id);
        foreach (var d in OutputDevices)
            engine.AddOutputDevice(d.Id);

        // Snapshot active devices after add attempts.
        var activeInputById = engine.InputDevices
            .GroupBy(d => d.Info.Id)
            .ToDictionary(g => g.Key, g => g.First());
        var activeOutputById = engine.OutputDevices
            .GroupBy(d => d.Info.Id)
            .ToDictionary(g => g.Key, g => g.First());

        // Build saved offset maps using persisted config order and channel counts.
        // This allows stable remapping from saved global channel indices even when
        // some devices are currently unavailable or current offsets differ.
        var savedInputLayout = new List<(string Id, int Channels, int SavedOffset)>();
        int savedInAcc = 0;
        foreach (var d in InputDevices)
        {
            int channels = d.Channels > 0
                ? d.Channels
                : (activeInputById.TryGetValue(d.Id, out var active) ? active.Info.Channels : 0);
            savedInputLayout.Add((d.Id, channels, savedInAcc));
            savedInAcc += Math.Max(0, channels);
        }

        var savedOutputLayout = new List<(string Id, int Channels, int SavedOffset)>();
        int savedOutAcc = 0;
        foreach (var d in OutputDevices)
        {
            int channels = d.Channels > 0
                ? d.Channels
                : (activeOutputById.TryGetValue(d.Id, out var active) ? active.Info.Channels : 0);
            savedOutputLayout.Add((d.Id, channels, savedOutAcc));
            savedOutAcc += Math.Max(0, channels);
        }

        // Wipe and rebuild with all available configured devices in original saved order.
        for (int i = engine.InputDevices.Count - 1; i >= 0; i--)
            engine.RemoveInputDevice(i);
        for (int i = engine.OutputDevices.Count - 1; i >= 0; i--)
            engine.RemoveOutputDevice(i);

        var keptInputs = InputDevices
            .Where(d => activeInputById.ContainsKey(d.Id))
            .Select(d => d.Id)
            .ToList();
        var keptOutputs = OutputDevices
            .Where(d => activeOutputById.ContainsKey(d.Id))
            .Select(d => d.Id)
            .ToList();

        foreach (var id in keptInputs)
            engine.AddInputDevice(id);
        foreach (var id in keptOutputs)
            engine.AddOutputDevice(id);

        // Build new offset tables to remap saved crosspoint channels.
        var newInputOffsets = new Dictionary<string, int>();
        int inAcc = 0;
        foreach (var d in engine.InputDevices)
        {
            newInputOffsets[d.Info.Id] = d.GlobalChannelOffset;
            inAcc += d.Info.Channels;
        }
        var newOutputOffsets = new Dictionary<string, int>();
        int outAcc = 0;
        foreach (var d in engine.OutputDevices)
        {
            newOutputOffsets[d.Info.Id] = d.GlobalChannelOffset;
            outAcc += d.Info.Channels;
        }

        foreach (var outputLatency in OutputLatencies)
        {
            engine.SetOutputDelayMs(outputLatency.DeviceId, outputLatency.DelayMs);
            // Base latency no longer adjusted (learned bias removed)
        }

        if (!string.IsNullOrWhiteSpace(InputMasterDeviceId))
            engine.SetInputMasterDevice(InputMasterDeviceId);

        if (!string.IsNullOrWhiteSpace(OutputMasterDeviceId))
            engine.SetOutputMasterDevice(OutputMasterDeviceId);

        foreach (var cp in Crosspoints)
        {
            var inDev = savedInputLayout.FirstOrDefault(d => cp.InCh >= d.SavedOffset && cp.InCh < d.SavedOffset + d.Channels);
            var outDev = savedOutputLayout.FirstOrDefault(d => cp.OutCh >= d.SavedOffset && cp.OutCh < d.SavedOffset + d.Channels);
            if (string.IsNullOrWhiteSpace(inDev.Id) || string.IsNullOrWhiteSpace(outDev.Id)) continue;
            if (!newInputOffsets.TryGetValue(inDev.Id, out var newInOffset)) continue;
            if (!newOutputOffsets.TryGetValue(outDev.Id, out var newOutOffset)) continue;

            int newIn = newInOffset + (cp.InCh - inDev.SavedOffset);
            int newOut = newOutOffset + (cp.OutCh - outDev.SavedOffset);
            engine.SetCrosspoint(newIn, newOut, true, cp.GainDb);
        }

        // Restore dormant routes when their devices reconnect.
        // Dormant routes are routes for devices that were previously configured but have been disconnected.
        // When they reconnect, automatically restore their previous routing.
        foreach (var dormant in DormantRoutes)
        {
            // Check if both devices are now available
            if (!newInputOffsets.TryGetValue(dormant.InputDeviceId, out var dormantInOffset)) continue;
            if (!newOutputOffsets.TryGetValue(dormant.OutputDeviceId, out var dormantOutOffset)) continue;

            // Calculate the global channel indices for this route
            int dormantInGlobal = dormantInOffset + dormant.InputLocalChannel;
            int dormantOutGlobal = dormantOutOffset + dormant.OutputLocalChannel;

            // Validate channel indices are within bounds
            int totalInChannels = engine.TotalInputChannels;
            int totalOutChannels = engine.TotalOutputChannels;
            if (dormantInGlobal < 0 || dormantInGlobal >= totalInChannels) continue;
            if (dormantOutGlobal < 0 || dormantOutGlobal >= totalOutChannels) continue;

            // Restore the route
            engine.SetCrosspoint(dormantInGlobal, dormantOutGlobal, true, dormant.GainDb);
        }
    }

}
