using System.Text.Json;
using System.Diagnostics;

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
    public int CaptureBufferMs { get; set; } = 40;
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
            File.WriteAllText(tempPath, json);

            if (File.Exists(path))
            {
                File.Copy(tempPath, path, overwrite: true);
                File.Delete(tempPath);
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

    public static AppConfig FromEngine(Audio.AudioEngine engine, int winX, int winY, int winW, int winH, bool locked, bool startMinimized, bool startupAtBoot, string uiPreferencesJson, string inputDeviceMode)
    {
        var config = new AppConfig
        {
            Window = new WindowConfig { X = winX, Y = winY, Width = winW, Height = winH, StartMinimized = startMinimized },
            Locked = locked,
            StartupAtBoot = startupAtBoot,
            CaptureBufferMs = engine.CaptureBufferMs,
            InputMasterDeviceId = engine.GetInputMasterDevice()?.Info.Id ?? "",
            OutputMasterDeviceId = engine.GetOutputMasterDevice()?.Info.Id ?? "",
            InputDeviceMode = inputDeviceMode is "input" or "loopback" or "both" ? inputDeviceMode : "both",
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
        var legacyUiPrefs = ReadLegacyUiPreferences();
        var captureBufferMs = CaptureBufferMs > 0 ? CaptureBufferMs : (legacyUiPrefs?.CaptureBufferMs ?? 40);
        engine.SetCaptureBufferMs(captureBufferMs);

        // Honor the user's configured active device lists exactly as saved, in saved order.
        // If a saved device is unavailable on this machine right now, skip it at runtime,
        // but do not infer replacements or expand the config with other system devices.
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

        // Wipe and rebuild with all available configured devices in original saved order.
        for (int i = engine.InputDevices.Count - 1; i >= 0; i--)
            engine.RemoveInputDevice(i);
        for (int i = engine.OutputDevices.Count - 1; i >= 0; i--)
            engine.RemoveOutputDevice(i);

        var keptInputs = inputSnapshot.ToList();
        var keptOutputs = outputSnapshot.ToList();

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

        var inputMasterDeviceId = !string.IsNullOrWhiteSpace(InputMasterDeviceId)
            ? InputMasterDeviceId
            : legacyUiPrefs?.InputMasterId ?? "";
        if (!string.IsNullOrWhiteSpace(inputMasterDeviceId))
            engine.SetInputMasterDevice(inputMasterDeviceId);

        var outputMasterDeviceId = !string.IsNullOrWhiteSpace(OutputMasterDeviceId)
            ? OutputMasterDeviceId
            : legacyUiPrefs?.OutputMasterId ?? "";
        if (!string.IsNullOrWhiteSpace(outputMasterDeviceId))
            engine.SetOutputMasterDevice(outputMasterDeviceId);

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

    private LegacyUiPreferencesSnapshot? ReadLegacyUiPreferences()
    {
        if (string.IsNullOrWhiteSpace(UiPreferencesJson)) return null;

        try
        {
            return JsonSerializer.Deserialize<LegacyUiPreferencesSnapshot>(UiPreferencesJson, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private sealed class LegacyUiPreferencesSnapshot
    {
        public int? CaptureBufferMs { get; set; }
        public string InputMasterId { get; set; } = "";
        public string OutputMasterId { get; set; } = "";
    }
}
