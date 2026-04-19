using NAudio.CoreAudioApi;

namespace AudioMatrixRouter.Audio;

public record DeviceInfo(
    string Id,
    string Name,
    int Channels,
    int SampleRate,
    DataFlow Flow
);
