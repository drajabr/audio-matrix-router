using System.Text.Json.Nodes;
using Avalonia.Threading;

namespace AudioMatrixRouter.App.Services;

/// <summary>
/// Typed wrapper over <see cref="AppController.UiPreferencesJson"/> using the EXACT
/// camelCase keys the web UI persisted (see App.jsx buildPersistedState), so users'
/// setups survive the host swap. Unknown keys (matrixByView, theme keys, buffer echoes,
/// anything future) are preserved by round-tripping the whole document as a JsonObject.
/// Writes are debounced ~300ms; call <see cref="Flush"/> on shutdown.
/// </summary>
public sealed class UiPreferences
{
    private readonly AppController _controller;
    private readonly JsonObject _root;
    private readonly DispatcherTimer _saveTimer;
    private bool _dirty;

    public UiPreferences(AppController controller)
    {
        _controller = controller;

        JsonObject? root = null;
        try
        {
            var json = controller.UiPreferencesJson;
            if (!string.IsNullOrWhiteSpace(json))
                root = JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            // Corrupt prefs must never take the app down — start fresh.
        }
        _root = root ?? new JsonObject();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Flush();
        };
    }

    // ===== simple typed keys =====

    public string ViewMode
    {
        get => GetString("viewMode", "device") == "channel" ? "channel" : "device";
        set => SetNode("viewMode", value);
    }

    public double MasterGainDb
    {
        get => GetDouble("masterGainDb", 0);
        set => SetNode("masterGainDb", value);
    }

    public bool ShowAllDevices
    {
        get => GetBool("showAllDevices", false);
        set => SetNode("showAllDevices", value);
    }

    public string InputDeviceMode
    {
        get => GetString("inputDeviceMode", "both");
        set => SetNode("inputDeviceMode", value);
    }

    public bool PowerOn
    {
        get => GetBool("powerOn", true);
        set => SetNode("powerOn", value);
    }

    public bool Locked
    {
        get => GetBool("locked", false);
        set => SetNode("locked", value);
    }

    public bool ControlsCollapsed
    {
        get => GetBool("controlsCollapsed", false);
        set => SetNode("controlsCollapsed", value);
    }

    public string InputMasterId
    {
        get => GetString("inputMasterId", "");
        set => SetNode("inputMasterId", value);
    }

    public string OutputMasterId
    {
        get => GetString("outputMasterId", "");
        set => SetNode("outputMasterId", value);
    }

    // Theme keys — settable: the gear popup writes them live.
    public string BackgroundKey
    {
        get => GetString("backgroundKey", "black");
        set => SetNode("backgroundKey", value);
    }

    public string AccentKey
    {
        get => GetString("accentKey", "teal");
        set => SetNode("accentKey", value);
    }

    public string FontKey
    {
        get => GetString("fontKey", "consolas");
        set => SetNode("fontKey", value);
    }

    public string FontSizeKey
    {
        get => GetString("fontSizeKey", "md");
        set => SetNode("fontSizeKey", value);
    }

    public string UiScaleKey
    {
        get => GetString("uiScaleKey", "md");
        set => SetNode("uiScaleKey", value);
    }

    // ===== labelSizing { sourceWidth, destinationHeight } =====
    // Both handles write the same number in the web app (the corner box is always
    // square), so a single scalar accessor is faithful.

    public double LabelSquare
    {
        get
        {
            if (_root["labelSizing"] is JsonObject sizing)
            {
                var v = ReadDouble(sizing["sourceWidth"]) ?? ReadDouble(sizing["destinationHeight"]);
                if (v is not null)
                    return Math.Clamp(v.Value, Theme.LabelSquareMin, Theme.LabelSquareMax);
            }
            return Theme.LabelSquareDefault;
        }
        set
        {
            var v = Math.Clamp(Math.Round(value), Theme.LabelSquareMin, Theme.LabelSquareMax);
            _root["labelSizing"] = new JsonObject
            {
                ["sourceWidth"] = v,
                ["destinationHeight"] = v,
            };
            MarkDirty();
        }
    }

    // ===== labels (deviceId -> custom label) =====

    public string? GetInputLabel(string deviceId) => GetLabel("inputLabels", deviceId);
    public string? GetOutputLabel(string deviceId) => GetLabel("outputLabels", deviceId);

    public void SetInputLabel(string deviceId, string label) => SetLabel("inputLabels", deviceId, label);
    public void SetOutputLabel(string deviceId, string label) => SetLabel("outputLabels", deviceId, label);

    private string? GetLabel(string mapKey, string deviceId)
    {
        if (_root[mapKey] is JsonObject map && map[deviceId] is JsonValue v &&
            v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
        {
            return s;
        }
        return null;
    }

    private void SetLabel(string mapKey, string deviceId, string label)
    {
        if (_root[mapKey] is not JsonObject map)
        {
            map = new JsonObject();
            _root[mapKey] = map;
        }
        if (string.IsNullOrWhiteSpace(label))
            map.Remove(deviceId);
        else
            map[deviceId] = label;
        MarkDirty();
    }

    // ===== device ordering =====

    public List<string> InputOrder
    {
        get => GetStringList("inputOrder");
        set => SetStringList("inputOrder", value);
    }

    public List<string> OutputOrder
    {
        get => GetStringList("outputOrder");
        set => SetStringList("outputOrder", value);
    }

    private List<string> GetStringList(string key)
    {
        var result = new List<string>();
        if (_root[key] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                    result.Add(s);
            }
        }
        return result;
    }

    private void SetStringList(string key, List<string> value)
    {
        var arr = new JsonArray();
        foreach (var s in value)
            arr.Add((JsonNode)s);
        _root[key] = arr;
        MarkDirty();
    }

    // ===== plumbing =====

    /// <summary>Write pending changes through to the controller immediately.</summary>
    public void Flush()
    {
        if (!_dirty) return;
        _dirty = false;
        try
        {
            _controller.UiPreferencesJson = _root.ToJsonString();
        }
        catch
        {
            // Persistence failure is non-fatal; the next change retries.
            _dirty = true;
        }
    }

    private void MarkDirty()
    {
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SetNode(string key, JsonNode? value)
    {
        _root[key] = value;
        MarkDirty();
    }

    private string GetString(string key, string fallback)
    {
        if (_root[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
            return s;
        return fallback;
    }

    private bool GetBool(string key, bool fallback)
    {
        if (_root[key] is JsonValue v && v.TryGetValue<bool>(out var b))
            return b;
        return fallback;
    }

    private double GetDouble(string key, double fallback) => ReadDouble(_root[key]) ?? fallback;

    private static double? ReadDouble(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d))
            return d;
        return null;
    }
}
