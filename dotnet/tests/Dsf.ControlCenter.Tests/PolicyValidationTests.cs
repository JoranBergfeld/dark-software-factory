using System.Globalization;
using Dsf.ControlCenter;
using Xunit;

namespace Dsf.ControlCenter.Tests;

/// <summary>
/// Numeric policy inputs are validated before any write reaches the configuration
/// authority, so a mistyped threshold can never be persisted.
/// </summary>
public sealed class PolicyValidationTests
{
    [Theory]
    [InlineData("0", 0d)]
    [InlineData("1", 1d)]
    [InlineData("0.75", 0.75d)]
    [InlineData(" 0.6 ", 0.6d)]
    public void Accepts_in_range_invariant_numbers(string raw, double expected)
    {
        Assert.True(PolicyValidation.TryValidateConfidenceThreshold(raw, out var value, out var error));
        Assert.Equal(expected, value);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0,7")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Rejects_values_that_are_not_invariant_finite_numbers(string raw)
    {
        Assert.False(PolicyValidation.TryValidateConfidenceThreshold(raw, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.0001")]
    [InlineData("42")]
    public void Rejects_out_of_range_values_and_states_the_range(string raw)
    {
        Assert.False(PolicyValidation.TryValidateConfidenceThreshold(raw, out _, out var error));
        Assert.NotNull(error);
        Assert.Contains("0", error, StringComparison.Ordinal);
        Assert.Contains("1", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_culture_dependent_reading_of_the_same_text()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
        try
        {
            Assert.False(PolicyValidation.TryValidateConfidenceThreshold("0,7", out _, out _));
            Assert.True(PolicyValidation.TryValidateConfidenceThreshold("0.7", out var value, out _));
            Assert.Equal(0.7d, value);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
