namespace AudioMatrixRouter.Audio;

public struct Crosspoint
{
    public bool Active;
    public float Gain; // linear gain, 1.0 = 0dB
}

/// <summary>
/// Double-buffered routing matrix. UI writes to back buffer, then publishes.
/// Audio thread reads front buffer via volatile reference — zero locks on audio path.
/// </summary>
public class RoutingMatrix
{
    private Crosspoint[] _bufferA = [];
    private Crosspoint[] _bufferB = [];
    private volatile Crosspoint[] _front = [];
    private readonly object _writeLock = new();
    private int _inputChannels;
    private int _outputChannels;

    public int InputChannels => _inputChannels;
    public int OutputChannels => _outputChannels;

    public void Resize(int inputChannels, int outputChannels)
    {
        lock (_writeLock)
        {
            _inputChannels = inputChannels;
            _outputChannels = outputChannels;
            _bufferA = new Crosspoint[inputChannels * outputChannels];
            _bufferB = new Crosspoint[inputChannels * outputChannels];
            _front = _bufferA;
        }
    }

    public void SetCrosspoint(int inCh, int outCh, bool active, float gainDb)
    {
        lock (_writeLock)
        {
            var back = GetBackBuffer();
            int idx = inCh * _outputChannels + outCh;
            if (idx < 0 || idx >= back.Length) return;

            back[idx].Active = active;
            if (!active || gainDb <= -60f)
                back[idx].Gain = 0f;
            else
                back[idx].Gain = MathF.Pow(10f, gainDb / 20f);
        }
    }

    public void ToggleCrosspoint(int inCh, int outCh)
    {
        lock (_writeLock)
        {
            var back = GetBackBuffer();
            int idx = inCh * _outputChannels + outCh;
            if (idx < 0 || idx >= back.Length) return;

            back[idx].Active = !back[idx].Active;
            back[idx].Gain = back[idx].Active ? 1f : 0f;
        }
    }

    public void Publish()
    {
        lock (_writeLock)
        {
            _front = GetBackBuffer();
        }
    }

    /// <summary>Audio thread reads this — no locks.</summary>
    public Crosspoint[] GetFrontBuffer() => _front;

    /// <summary>UI thread reads for display.</summary>
    public Crosspoint GetCrosspoint(int inCh, int outCh)
    {
        lock (_writeLock)
        {
            var back = GetBackBuffer();
            int idx = inCh * _outputChannels + outCh;
            if (idx < 0 || idx >= back.Length) return default;
            return back[idx];
        }
    }

    public float GetGainDb(int inCh, int outCh)
    {
        var cp = GetCrosspoint(inCh, outCh);
        if (!cp.Active || cp.Gain <= 0f) return -60f;
        return 20f * MathF.Log10(cp.Gain);
    }

    public bool HasAnyCrosspoints()
    {
        lock (_writeLock)
        {
            var back = GetBackBuffer();
            for (int i = 0; i < back.Length; i++)
                if (back[i].Active) return true;
            return false;
        }
    }

    public void ClearAll()
    {
        lock (_writeLock)
        {
            var back = GetBackBuffer();
            Array.Clear(back, 0, back.Length);
            _front = back;
        }
    }

    private Crosspoint[] GetBackBuffer()
    {
        return ReferenceEquals(_front, _bufferA) ? _bufferB : _bufferA;
    }
}
