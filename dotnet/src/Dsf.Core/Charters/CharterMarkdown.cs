using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Dsf.Core.Charters;

/// <summary>Raised when <c>.dsf/charter.md</c> is malformed; carries every diagnostic.</summary>
public sealed class CharterParseException(IReadOnlyList<string> diagnostics)
    : Exception(string.Join("; ", diagnostics))
{
    public IReadOnlyList<string> Diagnostics { get; } = diagnostics;
}

/// <summary>
/// Deterministic parser for the human-owned charter markdown. Parsing is strict and
/// collects every diagnostic before failing, so an author sees all problems at once.
/// </summary>
public static partial class CharterMarkdown
{
    /// <summary>Canonical path of the human-owned charter file in a product repo.</summary>
    public const string CharterPath = ".dsf/charter.md";

    private static readonly string[] Headings =
    [
        "Vision", "Target Users", "Goals", "Non-Goals", "Success Metrics", "Constraints", "Glossary",
    ];

    [GeneratedRegex(@"<!--\s*dsf:charter\s+schema_version=(\d+)\s*-->")]
    private static partial Regex MarkerPattern();

    public static Charter Parse(string text, string product)
    {
        var diagnostics = new List<string>();
        var lines = text.ReplaceLineEndings("\n").Split('\n');

        foreach (var line in lines)
        {
            var stripped = line.Trim();
            if (stripped.StartsWith("<<<<<<<", StringComparison.Ordinal)
                || stripped.StartsWith(">>>>>>>", StringComparison.Ordinal)
                || stripped == "=======")
            {
                diagnostics.Add("merge conflict markers present in charter");
                break;
            }
        }

        var marker = MarkerPattern().Match(text);
        if (!marker.Success)
        {
            diagnostics.Add("missing or malformed '<!-- dsf:charter schema_version=N -->' marker");
        }
        else if (marker.Groups[1].Value != "1")
        {
            diagnostics.Add($"unsupported schema_version {marker.Groups[1].Value} (expected 1)");
        }

        var (sections, order) = SplitSections(lines);
        foreach (var heading in order.Where(heading => !Headings.Contains(heading, StringComparer.Ordinal)))
        {
            diagnostics.Add($"unknown section '## {heading}'");
        }

        foreach (var heading in Headings)
        {
            var count = order.Count(candidate => string.Equals(candidate, heading, StringComparison.Ordinal));
            if (count == 0)
            {
                diagnostics.Add($"missing required section '## {heading}'");
            }
            else if (count > 1)
            {
                diagnostics.Add($"duplicate section '## {heading}'");
            }
        }

        var vision = Prose(sections, "Vision");
        var targetUsers = Prose(sections, "Target Users");
        var goals = Items(sections, "Goals", diagnostics);
        var nonGoals = Items(sections, "Non-Goals", diagnostics);
        var metrics = Items(sections, "Success Metrics", diagnostics);
        var glossary = Glossary(sections, diagnostics);

        if (vision.Length == 0 && order.Contains("Vision", StringComparer.Ordinal))
        {
            diagnostics.Add("section '## Vision' is empty");
        }

        if (targetUsers.Length == 0 && order.Contains("Target Users", StringComparer.Ordinal))
        {
            diagnostics.Add("section '## Target Users' is empty");
        }

        if (goals.Count == 0)
        {
            diagnostics.Add("at least one Goal is required");
        }

        if (metrics.Count == 0)
        {
            diagnostics.Add("at least one Success Metric is required");
        }

        if (diagnostics.Count > 0)
        {
            throw new CharterParseException(diagnostics);
        }

        return new Charter(
            product,
            vision,
            targetUsers,
            goals,
            nonGoals,
            metrics,
            Prose(sections, "Constraints"),
            glossary)
        {
            SchemaVersion = int.Parse(marker.Groups[1].Value, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Git's blob object id for <paramref name="text"/> (matches <c>git hash-object</c>).</summary>
    public static string GitBlobSha(string text) => GitBlobSha(Encoding.UTF8.GetBytes(text));

    /// <summary>Git's blob object id for <paramref name="data"/> (matches <c>git hash-object</c>).</summary>
    public static string GitBlobSha(byte[] data)
    {
        var header = Encoding.ASCII.GetBytes($"blob {data.Length}\0");
        var payload = new byte[header.Length + data.Length];
        header.CopyTo(payload, 0);
        data.CopyTo(payload, header.Length);
        return Convert.ToHexStringLower(SHA1.HashData(payload));
    }

    private static (Dictionary<string, List<string>> Sections, List<string> Order) SplitSections(string[] lines)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();
        string? current = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                current = line[3..].Trim();
                order.Add(current);
                if (!sections.ContainsKey(current))
                {
                    sections[current] = [];
                }
            }
            else if (current is not null)
            {
                sections[current].Add(line);
            }
        }

        return (sections, order);
    }

    private static string Prose(Dictionary<string, List<string>> sections, string name) =>
        sections.TryGetValue(name, out var body) ? string.Join("\n", body).Trim() : string.Empty;

    private static IReadOnlyList<string> Items(
        Dictionary<string, List<string>> sections,
        string name,
        List<string> diagnostics)
    {
        var items = new List<string>();
        if (!sections.TryGetValue(name, out var body))
        {
            return items;
        }

        foreach (var raw in body)
        {
            var entry = raw.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            if (!entry.StartsWith("- ", StringComparison.Ordinal))
            {
                diagnostics.Add($"section '## {name}' expects '- ' bullets; found '{entry}'");
                continue;
            }

            items.Add(entry[2..].Trim());
        }

        return items;
    }

    private static IReadOnlyDictionary<string, string> Glossary(
        Dictionary<string, List<string>> sections,
        List<string> diagnostics)
    {
        var glossary = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Items(sections, "Glossary", diagnostics))
        {
            var separator = entry.IndexOf(": ", StringComparison.Ordinal);
            if (separator < 0)
            {
                diagnostics.Add($"glossary entry '{entry}' must be '- term: definition'");
                continue;
            }

            glossary[entry[..separator].Trim()] = entry[(separator + 2)..].Trim();
        }

        return glossary;
    }
}
