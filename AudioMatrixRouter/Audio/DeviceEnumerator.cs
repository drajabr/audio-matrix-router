using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioMatrixRouter.Audio;

public class DeviceEnumerator : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;
    private Action? _onDeviceChange;

    public DeviceEnumerator()
    {
        _enumerator = new MMDeviceEnumerator();
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    public void SetChangeCallback(Action callback) => _onDeviceChange = callback;

    public List<DeviceInfo> GetDevices(DataFlow flow)
    {
        var result = new List<DeviceInfo>();
        try
        {
            var devices = _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            foreach (var device in devices)
            {
                try
                {
                    var mixFormat = device.AudioClient.MixFormat;
                    result.Add(new DeviceInfo(
                        device.ID,
                        device.FriendlyName,
                        mixFormat.Channels,
                        mixFormat.SampleRate,
                        flow
                    ));
                }
                catch { /* skip devices that fail to query */ }
            }
        }
        catch { }
        return result;
    }

    public MMDevice? GetDevice(string id)
    {
        try { return _enumerator.GetDevice(id); }
        catch { return null; }
    }

    // IMMNotificationClient
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _onDeviceChange?.Invoke();
    public void OnDeviceAdded(string pwstrDeviceId) => _onDeviceChange?.Invoke();
    public void OnDeviceRemoved(string deviceId) => _onDeviceChange?.Invoke();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        _enumerator.UnregisterEndpointNotificationCallback(this);
        _enumerator.Dispose();
    }
}
