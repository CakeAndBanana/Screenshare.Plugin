using System.Globalization;

namespace ScreenShare.Plugin.Ui.Helpers;

internal static class NumericEntry
{
    public static string Format(int value, string unit) =>
        value.ToString(CultureInfo.InvariantCulture) + unit;

    public static string Format(float value, int decimals, string unit) =>
        value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + unit;

    public static bool TryParseClamped(string? text, float min, float max, out float value)
    {
        if (!TryParse(text, out float parsed))
        {
            value = 0f;
            return false;
        }

        value = Math.Clamp(parsed, min, max);
        return true;
    }

    public static bool TryParseClamped(string? text, int min, int max, out int value)
    {
        if (!TryParse(text, out float parsed))
        {
            value = 0;
            return false;
        }

        value = (int)MathF.Round(Math.Clamp(parsed, min, max));
        return true;
    }

    private static bool TryParse(string? text, out float value)
    {
        value = 0f;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string trimmed = text.Trim();

        if (!float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !float.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            value = 0f;
            return false;
        }

        if (!float.IsFinite(value))
        {
            value = 0f;
            return false;
        }

        return true;
    }
}
