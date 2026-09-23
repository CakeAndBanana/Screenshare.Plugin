using System.Globalization;
using ScreenShare.Plugin.Ui.Helpers;

namespace ScreenShare.Plugin.Tests.Ui;

public class NumericEntryTests
{
    [Theory]
    [InlineData("0.85", 0.85f)]
    [InlineData("  0.85  ", 0.85f)]
    [InlineData("1", 1f)]
    [InlineData("+0.5", 0.5f)]
    [InlineData(".5", 0.5f)]
    public void TryParseClamped_ParsesInvariantText(string text, float expected)
    {
        Assert.True(NumericEntry.TryParseClamped(text, 0.05f, 1f, out float value));
        Assert.Equal(expected, value, 4);
    }

    [Fact]
    public void TryParseClamped_RoundTripsWhatFormatProduced()
    {
        string formatted = NumericEntry.Format(0.85f, 2, string.Empty);

        Assert.True(NumericEntry.TryParseClamped(formatted, 0.05f, 1f, out float value));
        Assert.Equal(0.85f, value, 4);
    }

    [Theory]
    [InlineData("0,85")]
    [InlineData("0.85")]
    public void TryParseClamped_AcceptsEitherSeparatorUnderCommaCulture(string text)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");

            Assert.True(NumericEntry.TryParseClamped(text, 0.05f, 1f, out float value));
            Assert.Equal(0.85f, value, 4);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("2", 1f)]
    [InlineData("-7", 0.05f)]
    public void TryParseClamped_ClampsToBounds(string text, float expected)
    {
        Assert.True(NumericEntry.TryParseClamped(text, 0.05f, 1f, out float value));
        Assert.Equal(expected, value, 4);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData("abc")]
    [InlineData("1.2.3")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e40")]
    public void TryParseClamped_RejectsWhatIsNotAFiniteNumber(string? text)
    {
        Assert.False(NumericEntry.TryParseClamped(text, 0.05f, 1f, out float value));
        Assert.Equal(0f, value);

        Assert.False(NumericEntry.TryParseClamped(text, 5, 60, out int intValue));
        Assert.Equal(0, intValue);
    }

    [Theory]
    [InlineData("29.6", 30)]
    [InlineData("29.4", 29)]
    [InlineData("30", 30)]
    public void TryParseClamped_RoundsRatherThanTruncatingToInt(string text, int expected)
    {
        Assert.True(NumericEntry.TryParseClamped(text, 5, 60, out int value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("999999999999", 64)]
    [InlineData("-999999999999", 8)]
    [InlineData("1e30", 64)]
    public void TryParseClamped_ClampsBeyondIntRange(string text, int expected)
    {
        Assert.True(NumericEntry.TryParseClamped(text, 8, 64, out int value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void Format_AppendsTheUnitAndHonoursDecimals()
    {
        Assert.Equal("42%", NumericEntry.Format(42, "%"));
        Assert.Equal("42", NumericEntry.Format(42, string.Empty));
        Assert.Equal("0.85", NumericEntry.Format(0.85f, 2, string.Empty));
        Assert.Equal("0.9", NumericEntry.Format(0.85f, 1, string.Empty));
        Assert.Equal("-300", NumericEntry.Format(-300f, 0, string.Empty));
    }

    [Fact]
    public void Format_StaysInvariantUnderCommaCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");

            Assert.Equal("0.85", NumericEntry.Format(0.85f, 2, string.Empty));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
