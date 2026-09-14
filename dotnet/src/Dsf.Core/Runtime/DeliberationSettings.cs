using System.Globalization;
using System.Text.Json.Serialization;

namespace Dsf.Core.Runtime;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeliberationLensSettings([property: JsonRequired] string Name, bool Enabled = true, double Weight = 1d);

public sealed record DeliberationSettings(int Rounds, IReadOnlyList<DeliberationLensSettings> Lenses)
{
    public const string RoundsKey = "DSF_DELIBERATION_ROUNDS";
    public static IReadOnlyList<string> LensNames { get; } = ["value", "cost", "feasibility", "security", "strategic-fit"];
    public static string Key(string lens, string field) => $"DSF_LENS_{lens.Replace('-', '_').ToUpperInvariant()}_{field}";
    public static IReadOnlyList<string> Keys { get; } =
        [RoundsKey, .. LensNames.SelectMany(lens => new[] { Key(lens, "ENABLED"), Key(lens, "WEIGHT") })];

    public static DeliberationSettings Read(IReadOnlyDictionary<string, string?> environment)
    {
        string ReadValue(string key) =>
            environment.TryGetValue(key, out var value) ? value?.Trim() ?? "" : "";

        var rounds = 2;
        if (ReadValue(RoundsKey) is { Length: > 0 } text
            && (!int.TryParse(text, out rounds) || rounds is < 1 or > 2))
        {
            throw Invalid(RoundsKey, "must be one or two");
        }

        var lenses = new List<DeliberationLensSettings>();
        foreach (var name in LensNames)
        {
            var enabledKey = Key(name, "ENABLED");
            var weightKey = Key(name, "WEIGHT");
            var enabled = true;
            var weight = 1d;
            if (ReadValue(enabledKey) is { Length: > 0 } enabledText && !bool.TryParse(enabledText, out enabled))
            {
                throw Invalid(enabledKey, "must be true or false");
            }

            if (ReadValue(weightKey) is { Length: > 0 } weightText
                && (!double.TryParse(weightText, NumberStyles.Float, CultureInfo.InvariantCulture, out weight)
                    || !double.IsFinite(weight) || weight <= 0))
            {
                throw Invalid(weightKey, "must be a finite positive weight");
            }

            lenses.Add(new DeliberationLensSettings(name, enabled, weight));
        }

        return Create(rounds, lenses);
    }

    public static DeliberationSettings Create(int rounds = 2, IReadOnlyList<DeliberationLensSettings>? lenses = null)
    {
        if (rounds is < 1 or > 2)
        {
            throw Invalid(RoundsKey, "must be one or two");
        }

        var overrides = new Dictionary<string, DeliberationLensSettings>(StringComparer.Ordinal);
        foreach (var lens in lenses ?? [])
        {
            if (lens is null || string.IsNullOrWhiteSpace(lens.Name)
                || !LensNames.Contains(lens.Name.Trim().ToLowerInvariant(), StringComparer.Ordinal))
            {
                throw Invalid("DSF_LENS_*", "contains an unknown deliberation lens");
            }

            var name = lens.Name.Trim().ToLowerInvariant();
            if (!double.IsFinite(lens.Weight) || lens.Weight <= 0)
            {
                throw Invalid(Key(name, "WEIGHT"), "must be a finite positive weight");
            }

            if (!overrides.TryAdd(name, lens with { Name = name }))
            {
                throw Invalid(Key(name, "WEIGHT"), "contains a duplicate lens configuration");
            }
        }

        var configured = LensNames.Select(name => overrides.TryGetValue(name, out var lens)
            ? lens : new DeliberationLensSettings(name)).ToArray();
        if (configured.All(lens => !lens.Enabled))
        {
            throw new RuntimeConfigurationException(
                "at least one DSF_LENS_*_ENABLED setting must be true", LensNames.Select(name => Key(name, "ENABLED")).ToArray());
        }

        return new DeliberationSettings(rounds, configured);
    }

    private static RuntimeConfigurationException Invalid(string key, string message) => new($"{key} {message}", [key]);
}
