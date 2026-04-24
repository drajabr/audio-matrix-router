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
            }
        }

        int unread = (wp - rp + _capacity) % _capacity;
        return unread / _channels;
    }

    public void RemoveConsumer(string consumerId)
    {
        lock (_cursorLock)
        {
            _consumerReadPos.Remove(consumerId);
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
                var keys = new List<string>(_consumerReadPos.Keys);
                foreach (var key in keys)
                {
                    int rp = _consumerReadPos[key];
                    int unread = (wp - rp + _capacity) % _capacity;
                    if (unread <= allowedUnread) continue;

                    int advance = unread - allowedUnread;
                    _consumerReadPos[key] = (rp + advance) % _capacity;
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
