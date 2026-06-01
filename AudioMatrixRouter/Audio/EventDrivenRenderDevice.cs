using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioMatrixRouter.Audio;

public class EventDrivenRenderDevice
{
    private WasapiOut? _render;

    public ActiveDevice Device { get; }

    public EventDrivenRenderDevice(ActiveDevice device)
    {
        Device = device;
    }

    public bool Start(MMDevice mmDevice, int latencyMs)
    {
        try
        {
            _render = new WasapiOut(mmDevice, AudioClientShareMode.Shared, true, latencyMs);
            _render.Init(Device.MixProvider!);
            _render.PlaybackStopped += OnPlaybackStopped;
            Device.Render = _render;
            Device.RenderLatencyMs = latencyMs;
            return true;
        }
        catch
        {
            try { _render?.Dispose(); } catch { }
            _render = null;
            Device.Render = null;
            return false;
        }
    }

    public void Play()
    {
        try
        {
            _render?.Play();
        }
        catch
        {
        }
    }

    public void Stop()
    {
        try { _render?.Stop(); } catch { }
        try { _render?.Dispose(); } catch { }
        _render = null;
        Device.Render = null;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // PlaybackStopped may be called by WasapiOut when render is stopped or an error occurs.
        // That event is ignored here because AudioEngine manages stop/restart itself.
    }
}
