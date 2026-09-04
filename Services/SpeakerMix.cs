using System.Numerics;

namespace ScreenShare.Plugin.Services;

/// <summary>
/// The 2x2 matrix mixing the stream's two source channels into the two the listener actually
/// hears: <c>outLeft = LeftToLeft * inLeft + RightToLeft * inRight</c>, and correspondingly for
/// the right. <see cref="Identity"/> is plain untouched stereo.
///
/// <see cref="MonoGain"/> is the fallback for a single-channel stream, where there is no second
/// output channel to pan across and only distance can be expressed.
/// </summary>
public readonly record struct SpeakerGains(
    float LeftToLeft,
    float LeftToRight,
    float RightToLeft,
    float RightToRight,
    float MonoGain)
{
    public static SpeakerGains Identity => new(1f, 0f, 0f, 1f, 1f);
}

/// <summary>
/// Turns "where are the two virtual speakers, and where is the listener" into channel gains.
///
/// Pure and allocation-free on purpose: this is the only part of the spatial-audio path with any
/// real logic in it, and keeping it free of NAudio, ImGui and game state is what makes it
/// testable without a running game.
/// </summary>
public static class SpeakerMix
{
    private const float QuarterTurn = MathF.PI / 2f;

    /// <summary>
    /// Computes the mix for a listener at <paramref name="listener"/> facing
    /// <paramref name="listenerForward"/>, with the stream's left channel emitted from
    /// <paramref name="speakerLeft"/> and its right from <paramref name="speakerRight"/>.
    ///
    /// Each speaker contributes a distance gain (full inside <paramref name="falloffStart"/>,
    /// smoothly to silent at <paramref name="falloffEnd"/>) times a constant-power stereo pan
    /// taken from its horizontal bearing relative to the listener's facing. Elevation is ignored -
    /// two-channel amplitude panning can't express it, and pretending otherwise would only distort
    /// the horizontal image that it can express.
    ///
    /// A consequence worth knowing rather than being surprised by: a speaker directly behind the
    /// listener pans to centre, exactly like one directly in front. Distinguishing the two needs
    /// HRTF filtering, not amplitude panning. What this does capture is the part players actually
    /// notice - walking past the screen swaps which side the sound comes from, and walking away
    /// fades it out.
    /// </summary>
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

    /// <summary>Full volume out to <paramref name="falloffStart"/>, silent from
    /// <paramref name="falloffEnd"/>, smoothstepped between so there's no audible edge at either
    /// end. A degenerate range (end at or before start) becomes a hard cutoff at start rather than
    /// a division by zero.</summary>
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
