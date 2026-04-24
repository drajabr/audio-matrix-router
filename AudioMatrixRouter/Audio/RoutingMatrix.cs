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
            var oldFront = _front;
            int oldIn = _inputChannels;
            int oldOut = _outputChannels;

            _inputChannels = inputChannels;
            _outputChannels = outputChannels;
            _bufferA = new Crosspoint[inputChannels * outputChannels];
            _bufferB = new Crosspoint[inputChannels * outputChannels];

            int copyIn = Math.Min(oldIn, inputChannels);
            int copyOut = Math.Min(oldOut, outputChannels);
            for (int inCh = 0; inCh < copyIn; inCh++)
            {
                for (int outCh = 0; outCh < copyOut; outCh++)
                {
                    int oldIdx = inCh * oldOut + outCh;
                    int newIdx = inCh * outputChannels + outCh;
                    if (oldIdx >= 0 && oldIdx < oldFront.Length)
                    {
                        _bufferA[newIdx] = oldFront[oldIdx];
                        _bufferB[newIdx] = oldFront[oldIdx];
                    }
                }
            }

            _front = _bufferA;
        }
    }

    public bool SetCrosspoint(int inCh, int outCh, bool active, float gainDb)
    {
        lock (_writeLock)
        {
            var back = GetWritableBuffer();
            int idx = inCh * _outputChannels + outCh;
            if (idx < 0 || idx >= back.Length) return false;

            float newGain = (!active || gainDb <= -60f) ? 0f : MathF.Pow(10f, gainDb / 20f);
            bool changed = back[idx].Active != active || MathF.Abs(back[idx].Gain - newGain) > 0.000001f;
            if (!changed) return false;

            back[idx].Active = active;
            back[idx].Gain = newGain;
            return true;
        }
    }

    public int SetCrosspoints(IEnumerable<(int InCh, int OutCh, bool Active, float GainDb)> updates)
    {
        int changed = 0;
        lock (_writeLock)
        {
            var back = GetWritableBuffer();
            foreach (var update in updates)
            {
                int idx = update.InCh * _outputChannels + update.OutCh;
                if (idx < 0 || idx >= back.Length) continue;

                float newGain = (!update.Active || update.GainDb <= -60f)
                    ? 0f
                    : MathF.Pow(10f, update.GainDb / 20f);

                bool isChanged = back[idx].Active != update.Active || MathF.Abs(back[idx].Gain - newGain) > 0.000001f;
                if (!isChanged) continue;

                back[idx].Active = update.Active;
                back[idx].Gain = newGain;
                changed++;
            }

            if (changed > 0)
            {
                _front = back;
            }
        }

        return changed;
    }

    public void ToggleCrosspoint(int inCh, int outCh)
    {
        lock (_writeLock)
        {
            var back = GetWritableBuffer();
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
        var front = _front;
        int idx = inCh * _outputChannels + outCh;
        if (idx < 0 || idx >= front.Length) return default;
        return front[idx];
    }

    public float GetGainDb(int inCh, int outCh)
    {
        var cp = GetCrosspoint(inCh, outCh);
        if (!cp.Active || cp.Gain <= 0f) return -60f;
        return 20f * MathF.Log10(cp.Gain);
    }

    public bool HasAnyCrosspoints()
    {
        var front = _front;
        for (int i = 0; i < front.Length; i++)
            if (front[i].Active) return true;
        return false;
    }

    public void ClearAll()
    {
        lock (_writeLock)
        {
            var back = GetWritableBuffer();
            Array.Clear(back, 0, back.Length);
            _front = back;
        }
    }

    private Crosspoint[] GetWritableBuffer()
    {
        var front = _front;
        var back = ReferenceEquals(front, _bufferA) ? _bufferB : _bufferA;

        if (back.Length != front.Length)
        {
            back = new Crosspoint[front.Length];
            if (ReferenceEquals(front, _bufferA))
                _bufferB = back;
            else
                _bufferA = back;
        }

        if (front.Length > 0)
        {
            Array.Copy(front, back, front.Length);
        }

        return back;
    }

    private Crosspoint[] GetBackBuffer()
    {
        return ReferenceEquals(_front, _bufferA) ? _bufferB : _bufferA;
    }
}
