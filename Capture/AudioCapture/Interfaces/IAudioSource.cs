using NAudio.Wave;

namespace ScreenShare.Plugin.Capture.AudioCapture.Interfaces;

/// <summary>
/// Audio Source interface that provides audio samples and metadata about the audio stream.
/// </summary>
public interface IAudioSource : IDisposable
{
    WaveFormat WaveFormat { get; }

    float CurrentPeak { get; }

    Exception? LastError { get; }

    event Action<float[]>? SamplesAvailable;
}
