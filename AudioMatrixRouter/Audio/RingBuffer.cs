using System.Threading;

namespace AudioMatrixRouter.Audio;

/// <summary>
/// Lock-free single-producer single-consumer ring buffer for interleaved float audio frames.
/// </summary>
public class RingBuffer
{
    private readonly float[] _buffer;
    private readonly int _capacity; // total floats
    private volatile int _writePos;
    private readonly int _channels;
    private readonly object _cursorLock = new();
    private readonly Dictionary<string, int> _consumerReadPos = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _consumerDroppedFrames = new(StringComparer.Ordinal);
    // Reused buffer for ordering consumer keys during the Write() trim path. Allocating a
    // fresh List<string> on every overflowed write created GC pressure on the WASAPI capture
    // thread under load. Always accessed under _cursorLock so it's single-threaded.
    private readonly List<string> _trimKeysScratch = new();
    private string _preferredConsumerId = string.Empty;
    private long _totalFramesDropped;

    public long TotalFramesDropped => Interlocked.Read(ref _totalFramesDropped);
    public int CapacityFrames => _channels > 0 ? _capacity / _channels : 0;

    public RingBuffer(int frameCount, int channels)
    {
        _channels = channels;
        // Round up to power of 2 in frames
        int frames = 1;
        while (frames < frameCount) frames <<= 1;
        _capacity = frames * channels;
        _buffer = new float[_capacity];
    }

    public int AvailableFrames
    {
        get
        {
            int wp = _writePos;
            int maxUnread = 0;
            lock (_cursorLock)
            {
                foreach (var rp in _consumerReadPos.Values)
                {
                    int unread = (wp - rp + _capacity) % _capacity;
                    if (unread > maxUnread) maxUnread = unread;
                }
            }

            return maxUnread / _channels;
        }
    }

    public int GetAvailableFrames(string consumerId)
    {
        int wp = _writePos;
        int rp;
        lock (_cursorLock)
        {
            if (!_consumerReadPos.TryGetValue(consumerId, out rp))
            {
                rp = wp;
                _consumerReadPos[consumerId] = rp;
                _consumerDroppedFrames[consumerId] = 0;
            }
        }

        int unread = (wp - rp + _capacity) % _capacity;
        return unread / _channels;
    }

    /// <summary>
    /// Returns the absolute read-pointer offset between two consumers, in frames.
    /// Computed atomically so the shared write pointer cancels out — this is pure
    /// positional divergence with no write-timing measurement noise.
    /// Positive: follower has more frames available (master is ahead in playback).
    /// </summary>
    public int GetReadPointerDiffFrames(string masterId, string followerId)
    {
        lock (_cursorLock)
        {
            if (!_consumerReadPos.TryGetValue(masterId, out int mr)) return 0;
            if (!_consumerReadPos.TryGetValue(followerId, out int fr)) return 0;
            // mr - fr (mod capacity): positive = master read pointer is ahead of follower
            int raw = (mr - fr + _capacity) % _capacity;
            // Convert to signed so offsets near capacity/2 are handled correctly
            if (raw > _capacity / 2) raw -= _capacity;
            return raw / _channels;
        }
    }

    public void RemoveConsumer(string consumerId)
    {
        lock (_cursorLock)
        {
            _consumerReadPos.Remove(consumerId);
            _consumerDroppedFrames.Remove(consumerId);
        }
    }

    public long GetDroppedFramesForConsumer(string consumerId)
    {
        lock (_cursorLock)
        {
            return _consumerDroppedFrames.TryGetValue(consumerId, out var dropped)
                ? dropped
                : 0;
        }
    }

    public void SetPreferredConsumer(string consumerId)
    {
        lock (_cursorLock)
        {
            _preferredConsumerId = consumerId ?? string.Empty;
        }
    }

    public bool Write(float[] data, int offset, int frameCount)
    {
        int samples = frameCount * _channels;
        if (samples <= 0) return true;
        if (samples >= _capacity) return false;

        int wp = _writePos;

        int maxUnread = 0;
        lock (_cursorLock)
        {
            foreach (var rp in _consumerReadPos.Values)
            {
                int unread = (wp - rp + _capacity) % _capacity;
                if (unread > maxUnread) maxUnread = unread;
            }

            int free = _capacity - 1 - maxUnread;
            if (samples > free)
            {
                // Realtime policy: if a consumer lags too far behind, drop its oldest samples
                // so producer never stalls all outputs.
                int allowedUnread = _capacity - 1 - samples;
                _trimKeysScratch.Clear();
                foreach (var k in _consumerReadPos.Keys)
                {
                    _trimKeysScratch.Add(k);
                }

                // Followers are trimmed first. Keep the preferred consumer as the last one
                // to be advanced so master timing remains as stable as possible.
                if (!string.IsNullOrWhiteSpace(_preferredConsumerId) && _trimKeysScratch.Remove(_preferredConsumerId))
                {
                    _trimKeysScratch.Add(_preferredConsumerId);
                }

                for (int ki = 0; ki < _trimKeysScratch.Count; ki++)
                {
                    var key = _trimKeysScratch[ki];
                    int rp = _consumerReadPos[key];
                    int unread = (wp - rp + _capacity) % _capacity;
                    if (unread <= allowedUnread) continue;

                    int advance = unread - allowedUnread;
                    _consumerReadPos[key] = (rp + advance) % _capacity;
                    if (_channels > 0)
                    {
                        int droppedFrames = advance / _channels;
                        Interlocked.Add(ref _totalFramesDropped, droppedFrames);
                        _consumerDroppedFrames[key] = _consumerDroppedFrames.TryGetValue(key, out var currentDropped)
                            ? currentDropped + droppedFrames
                            : droppedFrames;
                    }
                }
            }
        }

        for (int i = 0; i < samples; i++)
        {
            _buffer[(wp + i) % _capacity] = data[offset + i];
            // Note: modulo on power-of-2 * channels works because capacity is power-of-2 * channels
        }
        _writePos = (wp + samples) % _capacity;
        return true;
    }

    public int ReadForConsumer(string consumerId, float[] dest, int offset, int frameCount)
    {
        int samples = frameCount * _channels;
        int wp = _writePos;
        int rp;
        lock (_cursorLock)
        {
            if (!_consumerReadPos.TryGetValue(consumerId, out rp))
            {
                rp = wp;
                _consumerReadPos[consumerId] = rp;
                _consumerDroppedFrames[consumerId] = 0;
            }
        }

        int available = (wp - rp + _capacity) % _capacity;
        if (samples > available) samples = available;

        int frames = samples / _channels;
        samples = frames * _channels; // ensure whole frames

        for (int i = 0; i < samples; i++)
        {
            dest[offset + i] = _buffer[(rp + i) % _capacity];
        }

        lock (_cursorLock)
        {
            _consumerReadPos[consumerId] = (rp + samples) % _capacity;
        }

        return frames;
    }

    public int PeekForConsumer(string consumerId, float[] dest, int offset, int frameCount)
    {
        int samples = frameCount * _channels;
        int wp = _writePos;
        int rp;
        lock (_cursorLock)
        {
            if (!_consumerReadPos.TryGetValue(consumerId, out rp))
            {
                rp = wp;
                _consumerReadPos[consumerId] = rp;
                _consumerDroppedFrames[consumerId] = 0;
            }
        }

        int available = (wp - rp + _capacity) % _capacity;
        if (samples > available) samples = available;

        int frames = samples / _channels;
        samples = frames * _channels;

        for (int i = 0; i < samples; i++)
        {
            dest[offset + i] = _buffer[(rp + i) % _capacity];
        }
        return frames;
    }

    public void Clear()
    {
        int wp = _writePos;
        lock (_cursorLock)
        {
            var keys = new List<string>(_consumerReadPos.Keys);
            foreach (var key in keys)
            {
                _consumerReadPos[key] = wp;
            }
        }
    }
}
