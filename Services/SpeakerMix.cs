using System.Numerics;

namespace ScreenShare.Plugin.Services;

public readonly record struct SpeakerGains(
    float LeftToLeft,
    float LeftToRight,
    float RightToLeft,
    float RightToRight,
    float MonoGain)
{
    public static SpeakerGains Identity => new(1f, 0f, 0f, 1f, 1f);
}

public static class SpeakerMix
{
    private const float QuarterTurn = MathF.PI / 2f;

    public static SpeakerGains Compute(
        Vector3 listener,
        Vector3 listenerForward,
        Vector3 speakerLeft,
        Vector3 speakerRight,
        float falloffStart,
        float falloffEnd)
    {
        Vector3 forward = Flatten(listenerForward);
        if (forward == Vector3.Zero)
            forward = new Vector3(0f, 0f, 1f);
        else
            forward = Vector3.Normalize(forward);

        // The listener's right-hand axis on the horizontal plane. FFXIV is Y-up, so this is the
        // forward vector turned a quarter turn about Y.
        var right = new Vector3(forward.Z, 0f, -forward.X);

        (float leftToLeft, float leftToRight, float leftDistance) =
            Contribution(listener, forward, right, speakerLeft, falloffStart, falloffEnd);
        (float rightToLeft, float rightToRight, float rightDistance) =
            Contribution(listener, forward, right, speakerRight, falloffStart, falloffEnd);

        // A mono stream comes out of both virtual speakers at once, so what it loses to distance is
        // the average of what each speaker loses - there is no pan to apply on top, since there is
        // only one output channel to put it in.
        float monoGain = (leftDistance + rightDistance) * 0.5f;

        return new SpeakerGains(leftToLeft, leftToRight, rightToLeft, rightToRight, monoGain);
    }

    private static (float ToLeft, float ToRight, float DistanceGainOnly) Contribution(
        Vector3 listener,
        Vector3 forward,
        Vector3 right,
        Vector3 speaker,
        float falloffStart,
        float falloffEnd)
    {
        Vector3 toSpeaker = speaker - listener;
        float gain = DistanceGain(toSpeaker.Length(), falloffStart, falloffEnd);
        if (gain <= 0f)
            return (0f, 0f, 0f);

        Vector3 horizontal = Flatten(toSpeaker);

        // Standing exactly on top of a speaker leaves no direction to pan along; centre it rather
        // than normalizing a zero vector into NaNs that would reach the sound card.
        float pan = 0.5f;
        if (horizontal != Vector3.Zero)
        {
            horizontal = Vector3.Normalize(horizontal);

            // How far to the listener's right the speaker sits, as -1..1. This is sin(bearing),
            // which is exactly the quantity a stereo pan wants - and it needs no trigonometry,
            // because the dot product against the right axis already is the sine.
            float rightness = Math.Clamp(Vector3.Dot(horizontal, right), -1f, 1f);
            pan = 0.5f * (1f + rightness);
        }

        // Constant power rather than linear: a linear crossfade dips ~3dB as a source passes
        // through centre, which is audible as a hole in the middle when you walk past the screen.
        float angle = pan * QuarterTurn;
        return (gain * MathF.Cos(angle), gain * MathF.Sin(angle), gain);
    }

    public static float DistanceGain(float distance, float falloffStart, float falloffEnd)
    {
        float start = MathF.Max(falloffStart, 0f);
        if (distance <= start)
            return 1f;

        if (falloffEnd <= start)
            return 0f;

        if (distance >= falloffEnd)
            return 0f;

        float t = (distance - start) / (falloffEnd - start);
        return 1f - (t * t * (3f - (2f * t)));
    }

    private static Vector3 Flatten(Vector3 value) => new(value.X, 0f, value.Z);
}
