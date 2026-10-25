namespace Dsf.Core.Charters;

public enum CharterKnowledgeState { Unknown, Known }
public sealed record CharterKnowledge(CharterKnowledgeState State, string Reason);
public sealed record CharterKnowledgeList(CharterKnowledge Knowledge, IReadOnlyList<string> Values);

/// <summary>Review-only onboarding contract; neither a schema-1 charter nor governing authority.</summary>
public sealed record GroundedCharterDraft(string Product, string Purpose, string IntendedUsers,
    IReadOnlyList<string> Goals, CharterKnowledgeList NonGoals, CharterKnowledgeList SuccessMeasures)
{
    public string Profile { get; init; } = "grounded-onboarding-draft-v1";

    public void Validate()
    {
        var errors = new List<string>();
        if (Profile != "grounded-onboarding-draft-v1" || string.IsNullOrWhiteSpace(Product))
            errors.Add("Explicit onboarding draft profile and product required.");
        if (!Substantive(Purpose)) errors.Add("Substantive grounded purpose required.");
        if (!Substantive(IntendedUsers)) errors.Add("Substantive grounded intended users required.");
        if (Goals is null || Goals.Count == 0 || Goals.Any(goal => !Substantive(goal)))
            errors.Add("At least one substantive grounded goal required; unknown markers are not goals.");
        Check(NonGoals, "Non-goals");
        Check(SuccessMeasures, "Success measures");
        if (errors.Count > 0) throw new CharterParseException(errors);

        void Check(CharterKnowledgeList? field, string name)
        {
            if (field?.Knowledge is null || field.Values is null || !Enum.IsDefined(field.Knowledge.State)
                || !Substantive(field.Knowledge.Reason)
                || field.Knowledge.State == CharterKnowledgeState.Unknown && field.Values.Count != 0
                || field.Knowledge.State == CharterKnowledgeState.Known
                    && (field.Values.Count == 0 || field.Values.Any(value => !Substantive(value))))
                errors.Add(name + " must be explicitly unknown without values, or substantively known.");
        }
    }

    public static bool Substantive(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 12 || text.Length > 512
            || text.Any(char.IsControl)) return false;
        var normalized = text.Trim().ToLowerInvariant();
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3
            && !new[] { "unknown", "tbd", "todo", "not known", "not yet known", "to be determined", "unspecified", "not documented" }
                .Any(marker => normalized.StartsWith(marker, StringComparison.Ordinal))
            && !System.Text.RegularExpressions.Regex.IsMatch(normalized,
                @"\b(?:goals?|purpose|vision|users?)\b.{0,40}\b(?:unknown|tbd|not (?:known|documented|specified)|to be determined)\b",
                System.Text.RegularExpressions.RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    }
}
