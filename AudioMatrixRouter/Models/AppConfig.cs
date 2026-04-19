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

public class AppConfig
{
    public WindowConfig Window { get; set; } = new();
    public List<DeviceConfig> InputDevices { get; set; } = [];
    public List<DeviceConfig> OutputDevices { get; set; } = [];
    public List<CrosspointConfig> Crosspoints { get; set; } = [];
    public bool Locked { get; set; }
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

    public static AppConfig FromEngine(Audio.AudioEngine engine, int winX, int winY, int winW, int winH, bool locked, bool startMinimized, string uiPreferencesJson)
    {
        var config = new AppConfig
        {
            Window = new WindowConfig { X = winX, Y = winY, Width = winW, Height = winH, StartMinimized = startMinimized },
            Locked = locked,
            UiPreferencesJson = uiPreferencesJson ?? ""
        };

        foreach (var d in engine.InputDevices)
            config.InputDevices.Add(new DeviceConfig { Id = d.Info.Id, Name = d.Info.Name });
        foreach (var d in engine.OutputDevices)
            config.OutputDevices.Add(new DeviceConfig { Id = d.Info.Id, Name = d.Info.Name });

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
        foreach (var d in InputDevices)
            engine.AddInputDevice(d.Id);
        foreach (var d in OutputDevices)
            engine.AddOutputDevice(d.Id);
        foreach (var cp in Crosspoints)
            engine.SetCrosspoint(cp.InCh, cp.OutCh, true, cp.GainDb);
    }
}
