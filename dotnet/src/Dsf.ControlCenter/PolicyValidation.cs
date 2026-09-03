using System.Globalization;
using Dsf.Core.Products;

namespace Dsf.ControlCenter;

/// <summary>
/// Validates numeric policy inputs before they reach the configuration
/// authority. Parsing is invariant-culture only, so an operator's locale can
/// never turn "0,7" into a silently different threshold, and every rejection
/// carries the message the UI shows.
/// </summary>
internal static class PolicyValidation
{
    public const double MinimumConfidenceThreshold = ConfidenceThresholdRange.Minimum;
    public const double MaximumConfidenceThreshold = ConfidenceThresholdRange.Maximum;

    public static bool TryValidateConfidenceThreshold(string? raw, out double value, out string? error)
    {
        value = 0d;
        var text = raw?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "Confidence threshold is required; enter a number between "
                + $"{Format(MinimumConfidenceThreshold)} and {Format(MaximumConfidenceThreshold)}.";
            return false;
        }

        if (!double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
            || !double.IsFinite(parsed))
        {
            error = $"Confidence threshold '{text}' is not a number; use a decimal point, for example 0.6.";
            return false;
        }

        if (!TryValidateConfidenceThreshold(parsed, out error))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    public static bool TryValidateConfidenceThreshold(double value, out string? error)
    {
        if (!ConfidenceThresholdRange.Contains(value))
        {
            error = $"Confidence threshold must be between {Format(MinimumConfidenceThreshold)} and "
                + $"{Format(MaximumConfidenceThreshold)}; '{Format(value)}' is out of range.";
            return false;
        }

        error = null;
        return true;
    }

    public static string Format(double value) => ConfidenceThresholdRange.Format(value);
}
