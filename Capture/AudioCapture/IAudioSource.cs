using NAudio.Wave;

namespace ScreenShare.Plugin.Capture.AudioCapture;

/// <summary>
/// Common surface shared by <see cref="SystemAudioCapture"/> (full system loopback) and
/// <see cref="ProcessAudioCapture"/> (one specific process's render streams) - lets
/// MediaSessionService/ShareWindow wire either one up identically without caring which is active.
/// </summary>
public interface IAudioSource : IDisposable
{
    WaveFormat WaveFormat { get; }

    float CurrentPeak { get; }

    Exception? LastError { get; }

    event Action<float[]>? SamplesAvailable;
}
