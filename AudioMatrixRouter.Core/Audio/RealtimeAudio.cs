using System.Runtime.InteropServices;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioMatrixRouter.Audio;

/// <summary>
/// Registers the calling thread with MMCSS "Pro Audio" and raises its priority.
/// NAudio does NOT do this for its capture or playback threads (verified against
/// NAudio 2.x source) — they run at normal priority, and with only ~10 ms of ring
/// margin at low latency settings an ordinary scheduling delay on either thread
/// books an audible underrun. Idempotent per thread; failures are ignored (MMCSS
/// is best-effort — the engine must run fine without it).
/// </summary>
internal static class MmcssHelper
{
    [ThreadStatic] private static bool t_joined;

    public static void BoostCurrentThread()
    {
        if (t_joined) return;
        t_joined = true;
        try { Thread.CurrentThread.Priority = ThreadPriority.Highest; } catch { }
        try
        {
            uint taskIndex = 0;
            _ = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
        }
        catch
        {
            // avrt.dll missing / call rejected — priority boost above still applies.
        }
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, EntryPoint = "AvSetMmThreadCharacteristicsW")]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);
}

/// <summary>
/// Loopback capture with a ~10 ms delivery cadence. NAudio's WasapiLoopbackCapture
/// hardcodes a 100 ms polling buffer, and its poll loop sleeps buffer/2 — so loopback
/// inputs delivered audio in ~50 ms bursts, 20 Hz. Against a 20-25 ms ring fill target
/// that is structurally unstable: the ring starves between bursts and the fill servo
/// (which updates once per delivery) runs at a fifth of its designed rate. A 20 ms
/// buffer makes the same poll loop wake every ~10 ms, so loopback producers behave
/// like event-driven microphone captures.
/// </summary>
internal sealed class PolledLoopbackCapture : WasapiCapture
{
    public PolledLoopbackCapture(MMDevice device)
        : base(device, useEventSync: false, audioBufferMillisecondsLength: 20)
    {
    }

    protected override AudioClientStreamFlags GetAudioClientStreamFlags()
        => AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
}
