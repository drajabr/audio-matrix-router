using System.Threading;

namespace AudioMatrixRouter.Audio;

/// <summary>
/// Multi-consumer ring buffer for interleaved float audio frames.
///
/// REWRITE NOTES (sync overhaul):
///  * Every consumer cursor is now tracked as an absolute 64-bit FRAME POSITION on the
///    stream timeline (frames written since construction), not a wrapped byte index.
///    This makes "how many source frames has consumer X consumed (including frames it
///    was force-skipped past by overflow trimming)" an exact, overflow-free quantity:
///    it IS the consumer's position. The sync controller uses these positions as its
///    ground-truth phase signal instead of estimating phase from wall-clock projections.
///  * Overflow trimming advances a lagging consumer's position; that advancement is
///    visible both in GetDroppedFramesForConsumer and, crucially, in the consumer's
///    position itself — so the phase measurement stays truthful through drops.
///  * All public members of the previous implementation are preserved.
/// </summary>
public class RingBuffer
{
    private readonly float[] _buffer;
    private readonly int _capacityFrames;     // power of two
    private readonly int _capacitySamples;
    private readonly int _channels;
    private readonly int _frameMask;          // _capacityFrames - 1
    private readonly object _cursorLock = new();

    // Absolute frame position of the writer (frames written since construction).
    private long _writeFramePos;

    private sealed class ConsumerState
    {
        public long ReadFramePos;   // absolute frame position on the stream timeline
        public long DroppedFrames;  // frames skipped past by overflow trimming
    }

    private readonly Dictionary<string, ConsumerState> _consumers = new(StringComparer.Ordinal);
    private readonly List<string> _trimKeysScratch = new();
    private string _preferredConsumerId = string.Empty;
    private long _totalFramesDropped;

    public long TotalFramesDropped => Interlocked.Read(ref _totalFramesDropped);
    public int CapacityFrames => _capacityFrames;

    public RingBuffer(int frameCount, int channels)
    {
        _channels = Math.Max(1, channels);
        int frames = 1;
        while (frames < frameCount) frames <<= 1;
        _capacityFrames = frames;
        _frameMask = frames - 1;
        _capacitySamples = frames * _channels;
        _buffer = new float[_capacitySamples];
    }

    /// <summary>Frames available to the most-lagging consumer (legacy semantics: max unread).</summary>
    public int AvailableFrames
    {
        get
        {
            lock (_cursorLock)
            {
                long wp = _writeFramePos;
                long maxUnread = 0;
                foreach (var c in _consumers.Values)
                {
                    long unread = wp - c.ReadFramePos;
                    if (unread > maxUnread) maxUnread = unread;
                }
                return (int)Math.Min(maxUnread, _capacityFrames);
            }
        }
    }

    public int GetAvailableFrames(string consumerId)
    {
        lock (_cursorLock)
        {
            var c = GetOrAddConsumerNoLock(consumerId);
            long unread = _writeFramePos - c.ReadFramePos;
            if (unread < 0) unread = 0;
            return (int)Math.Min(unread, _capacityFrames);
        }
    }

    /// <summary>
    /// Registers a consumer cursor at the CURRENT write position. Call for every consumer
    /// before the producer starts so all cursors share an identical zero reference.
    /// </summary>
    public void RegisterConsumer(string consumerId)
    {
        lock (_cursorLock)
        {
            GetOrAddConsumerNoLock(consumerId);
        }
    }

    /// <summary>
    /// Absolute stream-timeline position (in frames) of this consumer's read cursor.
    /// Includes frames skipped by overflow trimming. This is the exact ground truth used
    /// by the sync controller — two consumers with equal positions have played exactly
    /// the same audio history from this ring.
    /// </summary>
    public long GetConsumerPositionFrames(string consumerId)
    {
        lock (_cursorLock)
        {
            return _consumers.TryGetValue(consumerId, out var c) ? c.ReadFramePos : 0;
        }
    }

    /// <summary>Absolute frames written since construction.</summary>
    public long GetWritePositionFrames()
    {
        lock (_cursorLock)
        {
            return _writeFramePos;
        }
    }

    /// <summary>
    /// Signed positional divergence between two consumers in frames.
    /// Positive: master cursor is ahead of follower (follower has more unread).
    /// Exact — no wrap ambiguity, since positions are absolute 64-bit.
    /// </summary>
    public int GetReadPointerDiffFrames(string masterId, string followerId)
    {
        lock (_cursorLock)
        {
            if (!_consumers.TryGetValue(masterId, out var m)) return 0;
            if (!_consumers.TryGetValue(followerId, out var f)) return 0;
            long diff = m.ReadFramePos - f.ReadFramePos;
            return (int)Math.Clamp(diff, int.MinValue, int.MaxValue);
        }
    }

    public void RemoveConsumer(string consumerId)
    {
        lock (_cursorLock)
        {
            _consumers.Remove(consumerId);
        }
    }

    public long GetDroppedFramesForConsumer(string consumerId)
    {
        lock (_cursorLock)
        {
            return _consumers.TryGetValue(consumerId, out var c) ? c.DroppedFrames : 0;
        }
    }

    public void SetPreferredConsumer(string consumerId)
    {
        lock (_cursorLock)
        {
            _preferredConsumerId = consumerId ?? string.Empty;
        }
    }

    /// <summary>
    /// Producer write. If a consumer lags so far that the new data would overwrite its
    /// unread region, that consumer's cursor is advanced (oldest frames dropped for it)
    /// — the producer never stalls. The preferred (master) consumer is trimmed last.
    /// Returns false only for absurd requests (block >= capacity).
    /// </summary>
    public bool Write(float[] data, int offset, int frameCount)
    {
        if (frameCount <= 0) return true;
        if (frameCount >= _capacityFrames) return false;

        lock (_cursorLock)
        {
            long wp = _writeFramePos;
            long newWp = wp + frameCount;
            // Any consumer whose unread region would exceed capacity after this write
            // must be advanced so its unread fits in (capacity - small guard).
            long minAllowedPos = newWp - (_capacityFrames - 1);

            bool anyLagging = false;
            foreach (var c in _consumers.Values)
            {
                if (c.ReadFramePos < minAllowedPos) { anyLagging = true; break; }
            }

            if (anyLagging)
            {
                _trimKeysScratch.Clear();
                foreach (var k in _consumers.Keys) _trimKeysScratch.Add(k);
                // Preferred consumer trimmed last (kept for parity with previous policy;
                // with absolute positions trim order has no cross-consumer effect, but
                // ordering is harmless and cheap).
                if (!string.IsNullOrWhiteSpace(_preferredConsumerId) && _trimKeysScratch.Remove(_preferredConsumerId))
                {
                    _trimKeysScratch.Add(_preferredConsumerId);
                }

                for (int ki = 0; ki < _trimKeysScratch.Count; ki++)
                {
                    var c = _consumers[_trimKeysScratch[ki]];
                    if (c.ReadFramePos >= minAllowedPos) continue;
                    long advance = minAllowedPos - c.ReadFramePos;
                    c.ReadFramePos += advance;
                    c.DroppedFrames += advance;
                    Interlocked.Add(ref _totalFramesDropped, advance);
                }
            }

            // Copy samples into the ring (frame-indexed, power-of-two mask).
            int srcSample = offset;
            for (int f = 0; f < frameCount; f++)
            {
                int dstFrame = (int)((wp + f) & _frameMask);
                int dstSample = dstFrame * _channels;
                for (int ch = 0; ch < _channels; ch++)
                {
                    _buffer[dstSample + ch] = data[srcSample + ch];
                }
                srcSample += _channels;
            }

            _writeFramePos = newWp;
        }

        return true;
    }

    public int ReadForConsumer(string consumerId, float[] dest, int offset, int frameCount)
    {
        lock (_cursorLock)
        {
            var c = GetOrAddConsumerNoLock(consumerId);
            int frames = CopyOutNoLock(c.ReadFramePos, dest, offset, frameCount);
            c.ReadFramePos += frames;
            return frames;
        }
    }

    public int PeekForConsumer(string consumerId, float[] dest, int offset, int frameCount)
    {
        lock (_cursorLock)
        {
            var c = GetOrAddConsumerNoLock(consumerId);
            return CopyOutNoLock(c.ReadFramePos, dest, offset, frameCount);
        }
    }

    /// <summary>
    /// Advances a consumer's cursor by up to <paramref name="frameCount"/> frames without
    /// copying data out (used to discard late frames so an input stays time-aligned).
    /// Returns frames actually skipped.
    /// </summary>
    public int SkipForConsumer(string consumerId, int frameCount)
    {
        if (frameCount <= 0) return 0;
        lock (_cursorLock)
        {
            var c = GetOrAddConsumerNoLock(consumerId);
            long available = _writeFramePos - c.ReadFramePos;
            int skip = (int)Math.Min(available, frameCount);
            if (skip > 0) c.ReadFramePos += skip;
            return skip;
        }
    }

    public void Clear()
    {
        lock (_cursorLock)
        {
            foreach (var c in _consumers.Values)
            {
                c.ReadFramePos = _writeFramePos;
            }
        }
    }

    private ConsumerState GetOrAddConsumerNoLock(string consumerId)
    {
        if (!_consumers.TryGetValue(consumerId, out var c))
        {
            c = new ConsumerState { ReadFramePos = _writeFramePos };
            _consumers[consumerId] = c;
        }
        return c;
    }

    private int CopyOutNoLock(long readPos, float[] dest, int offset, int frameCount)
    {
        long available = _writeFramePos - readPos;
        if (available <= 0 || frameCount <= 0) return 0;
        int frames = (int)Math.Min(available, frameCount);

        int dstSample = offset;
        for (int f = 0; f < frames; f++)
        {
            int srcFrame = (int)((readPos + f) & _frameMask);
            int srcSample = srcFrame * _channels;
            for (int ch = 0; ch < _channels; ch++)
            {
                dest[dstSample + ch] = _buffer[srcSample + ch];
            }
            dstSample += _channels;
        }
        return frames;
    }
}
