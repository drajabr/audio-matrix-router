using System.Text.Json;

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
}

public class AppConfig
{
    public WindowConfig Window { get; set; } = new();
    public List<DeviceConfig> InputDevices { get; set; } = [];
    public List<DeviceConfig> OutputDevices { get; set; } = [];
    public List<CrosspointConfig> Crosspoints { get; set; } = [];
    public List<OutputLatencyConfig> OutputLatencies { get; set; } = [];
    public bool Locked { get; set; }
    public bool StartupAtBoot { get; set; }
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
            var json = JsonSerializer.Serialize(this, _jsonOptions);
            File.WriteAllText(GetConfigPath(), json);
        }
        catch { }
    }

    public static AppConfig FromEngine(Audio.AudioEngine engine, int winX, int winY, int winW, int winH, bool locked, bool startMinimized, bool startupAtBoot, string uiPreferencesJson)
    {
        var config = new AppConfig
        {
            Window = new WindowConfig { X = winX, Y = winY, Width = winW, Height = winH, StartMinimized = startMinimized },
            Locked = locked,
            StartupAtBoot = startupAtBoot,
            UiPreferencesJson = uiPreferencesJson ?? ""
        };

        foreach (var d in engine.InputDevices)
            config.InputDevices.Add(new DeviceConfig { Id = d.Info.Id, Name = d.Info.Name });
        foreach (var d in engine.OutputDevices)
        {
            config.OutputDevices.Add(new DeviceConfig { Id = d.Info.Id, Name = d.Info.Name });
            config.OutputLatencies.Add(new OutputLatencyConfig { DeviceId = d.Info.Id, DelayMs = d.OutputDelayMs });
        }

        var mat = engine.RoutingMatrix;
        for (int i = 0; i < mat.InputChannels; i++)
            for (int o = 0; o < mat.OutputChannels; o++)
            {
                var cp = mat.GetCrosspoint(i, o);
                if (cp.Active)
                    config.Crosspoints.Add(new CrosspointConfig { InCh = i, OutCh = o, GainDb = mat.GetGainDb(i, o) });
            }

        return config;
    }

    public void ApplyToEngine(Audio.AudioEngine engine)
    {
        // Migration: a previous version of SyncDevicesWithSystem auto-added every system
        // capture + loopback endpoint as an active input and persisted that bloated list.
        // On startup we'd then open a WasapiCapture/WasapiLoopbackCapture per device,
        // which pegs CPU and hangs the UI. So: only add devices that are referenced by
        // at least one saved crosspoint, then remap channel indices to the pruned layout.
        // First pass: temporarily add all devices to learn each one's channel count.
        foreach (var d in InputDevices)
            engine.AddInputDevice(d.Id);
        foreach (var d in OutputDevices)
            engine.AddOutputDevice(d.Id);

        // Snapshot original (id, channels, offset) in saved order, then clear.
        var inputSnapshot = engine.InputDevices
            .Select(d => (Id: d.Info.Id, Channels: d.Info.Channels, OldOffset: d.GlobalChannelOffset))
            .ToList();
        var outputSnapshot = engine.OutputDevices
            .Select(d => (Id: d.Info.Id, Channels: d.Info.Channels, OldOffset: d.GlobalChannelOffset))
            .ToList();

        // Determine which devices any crosspoint references.
        var usedInputIds = new HashSet<string>();
        var usedOutputIds = new HashSet<string>();
        foreach (var cp in Crosspoints)
        {
            var inDev = inputSnapshot.FirstOrDefault(d => cp.InCh >= d.OldOffset && cp.InCh < d.OldOffset + d.Channels);
            if (inDev.Id != null) usedInputIds.Add(inDev.Id);
            var outDev = outputSnapshot.FirstOrDefault(d => cp.OutCh >= d.OldOffset && cp.OutCh < d.OldOffset + d.Channels);
            if (outDev.Id != null) usedOutputIds.Add(outDev.Id);
        }

        // Wipe and rebuild with only referenced devices, in original saved order.
        // RemoveInputDevice/RemoveOutputDevice resize the matrix; remove from the end
        // to avoid shifting indices repeatedly.
        for (int i = engine.InputDevices.Count - 1; i >= 0; i--)
            engine.RemoveInputDevice(i);
        for (int i = engine.OutputDevices.Count - 1; i >= 0; i--)
            engine.RemoveOutputDevice(i);

        var keptInputs = inputSnapshot.Where(d => usedInputIds.Contains(d.Id)).ToList();
        var keptOutputs = outputSnapshot.Where(d => usedOutputIds.Contains(d.Id)).ToList();

        foreach (var d in keptInputs)
            engine.AddInputDevice(d.Id);
        foreach (var d in keptOutputs)
            engine.AddOutputDevice(d.Id);

        // Build new offset tables to remap saved crosspoint channels.
        var newInputOffsets = new Dictionary<string, int>();
        int inAcc = 0;
        foreach (var d in keptInputs) { newInputOffsets[d.Id] = inAcc; inAcc += d.Channels; }
        var newOutputOffsets = new Dictionary<string, int>();
        int outAcc = 0;
        foreach (var d in keptOutputs) { newOutputOffsets[d.Id] = outAcc; outAcc += d.Channels; }

        foreach (var outputLatency in OutputLatencies)
            engine.SetOutputDelayMs(outputLatency.DeviceId, outputLatency.DelayMs);

        foreach (var cp in Crosspoints)
        {
            var inDev = inputSnapshot.FirstOrDefault(d => cp.InCh >= d.OldOffset && cp.InCh < d.OldOffset + d.Channels);
            var outDev = outputSnapshot.FirstOrDefault(d => cp.OutCh >= d.OldOffset && cp.OutCh < d.OldOffset + d.Channels);
            if (inDev.Id == null || outDev.Id == null) continue;
            if (!newInputOffsets.TryGetValue(inDev.Id, out var newInOffset)) continue;
            if (!newOutputOffsets.TryGetValue(outDev.Id, out var newOutOffset)) continue;

            int newIn = newInOffset + (cp.InCh - inDev.OldOffset);
            int newOut = newOutOffset + (cp.OutCh - outDev.OldOffset);
            engine.SetCrosspoint(newIn, newOut, true, cp.GainDb);
        }
    }
}
