namespace AudioMatrixRouter.Audio;

/// <summary>
/// Lock-free single-producer single-consumer ring buffer for interleaved float audio frames.
/// </summary>
public class RingBuffer
{
    private readonly float[] _buffer;
    private readonly int _capacity; // total floats
    private volatile int _writePos;
    private volatile int _readPos;
    private readonly int _channels;

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
            int avail = (_writePos - _readPos);
            if (avail < 0) avail += _capacity;
            return avail / _channels;
        }
    }

    public bool Write(float[] data, int offset, int frameCount)
    {
        int samples = frameCount * _channels;
        int wp = _writePos;
        int rp = _readPos;

        int free = _capacity - 1 - ((wp - rp + _capacity) % _capacity);
        if (samples > free) return false; // overflow

        for (int i = 0; i < samples; i++)
        {
            _buffer[(wp + i) % _capacity] = data[offset + i];
            // Note: modulo on power-of-2 * channels works because capacity is power-of-2 * channels
        }
        _writePos = (wp + samples) % _capacity;
        return true;
    }

    public int Read(float[] dest, int offset, int frameCount)
    {
        int samples = frameCount * _channels;
        int wp = _writePos;
        int rp = _readPos;

        int available = (wp - rp + _capacity) % _capacity;
        if (samples > available) samples = available;

        int frames = samples / _channels;
        samples = frames * _channels; // ensure whole frames

        for (int i = 0; i < samples; i++)
        {
            dest[offset + i] = _buffer[(rp + i) % _capacity];
        }
        _readPos = (rp + samples) % _capacity;
        return frames;
    }

    public int Peek(float[] dest, int offset, int frameCount)
    {
        int samples = frameCount * _channels;
        int wp = _writePos;
        int rp = _readPos;

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
        _readPos = _writePos;
    }
}
