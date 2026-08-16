using EveDeck.ViewModels;
using Xunit;

namespace EveDeck.Tests;

// The panel sits in the corner of a preview tile, so the formatted value has to keep a roughly stable
// width -- a number that jumps between "847" and "12480" during a fight would resize the panel every
// second and drag the eye away from the preview underneath.
public class DpsFormatRateTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(-5, "0")]          // a negative rate is not meaningful; show idle rather than "-5"
    [InlineData(0.4, "0.4")]
    [InlineData(3.25, "3.3")]      // sub-10 keeps one decimal so small logi/cap numbers stay visible
    [InlineData(9.99, "10")]
    [InlineData(42.6, "43")]       // 10..999 rounds to whole numbers
    [InlineData(999, "999")]
    [InlineData(1000, "1.0k")]
    [InlineData(1240, "1.2k")]
    [InlineData(9949, "9.9k")]
    [InlineData(10000, "10k")]     // past 10k the decimal stops earning its width
    [InlineData(124800, "125k")]
    public void FormatsCompactly(double value, string expected)
    {
        Assert.Equal(expected, MainWindowViewModel.FormatRate(value));
    }

    // Formatting must not follow the machine's locale: a comma decimal separator would render "1,2k"
    // and, worse, shift meaning for anyone reading it as a thousands separator.
    [Fact]
    public void UsesInvariantDecimalSeparatorRegardlessOfCulture()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1.2k", MainWindowViewModel.FormatRate(1240));
            Assert.Equal("3.3", MainWindowViewModel.FormatRate(3.25));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }
}
