using NAudio.Wave;

namespace ScreenShare.Plugin.Capture.AudioCapture.Interfaces;

public interface IAudioSource : IDisposable
{
    WaveFormat WaveFormat { get; }

    float CurrentPeak { get; }

    Exception? LastError { get; }

    event Action<float[]>? SamplesAvailable;
}
