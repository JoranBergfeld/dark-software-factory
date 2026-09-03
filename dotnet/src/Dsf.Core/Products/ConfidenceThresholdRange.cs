using System.Globalization;

namespace Dsf.Core.Products;

/// <summary>Shared confidence-threshold range enforced by governance writers and runtime readers.</summary>
public static class ConfidenceThresholdRange
{
    public const double Minimum = 0d;
    public const double Maximum = 1d;

    public static bool Contains(double value) =>
        double.IsFinite(value) && value >= Minimum && value <= Maximum;

    public static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
