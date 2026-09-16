using System.Text;

namespace Dsf.Core.Charters;

/// <summary>Explicit onboarding profile; does not relax the schema-1 parser.</summary>
public static class OnboardingCharterMarkdown
{
    public const string Marker = "<!-- dsf:charter profile=grounded-onboarding-v1 -->";

    public static GroundedCharterDraft Parse(string content, string product)
    {
        if (content.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new CharterParseException(["Unsafe control character in human-owned charter."]);
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        if (lines[0] != Marker)
        {
            var legacy = CharterMarkdown.Parse(content, product);
            var draft = new GroundedCharterDraft(product, HumanProse(legacy.Vision), HumanProse(legacy.TargetUsers), legacy.Goals,
                new(new(legacy.NonGoals.Count == 0 ? CharterKnowledgeState.Unknown : CharterKnowledgeState.Known,
                    "Existing human charter records these boundaries."), legacy.NonGoals),
                new(new(CharterKnowledgeState.Known, "Existing human charter records these measures."), legacy.SuccessMetrics));
            draft.Validate();
            return draft;
        }
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                current = [];
                if (!sections.TryAdd(line[3..], current)) throw Invalid();
            }
            else if (current is not null) current.Add(line);
            else if (line is not ("" or "# Product charter")) throw Invalid();
        }
        if (sections.Count != 5 || sections.Keys.Except(["Purpose", "Intended Users", "Goals", "Non-Goals", "Success Measures"]).Any())
            throw Invalid();
        var result = new GroundedCharterDraft(product, Text("Purpose"), Text("Intended Users"), Items("Goals"),
            Optional("Non-Goals"), Optional("Success Measures"));
        result.Validate();
        return result;

        string Text(string name) => HumanProse(string.Join('\n', sections[name]));
        string[] Items(string name) => sections[name].Where(l => l.Length > 0)
            .Select(l => l.StartsWith("- ", StringComparison.Ordinal) ? l[2..] : throw Invalid()).ToArray();
        CharterKnowledgeList Optional(string name) => Text(name).StartsWith("Unknown: ", StringComparison.Ordinal)
            ? new(new(CharterKnowledgeState.Unknown, Text(name)["Unknown: ".Length..]), [])
            : new(new(CharterKnowledgeState.Known, "Existing human charter explicitly records these statements."), Items(name));
        static CharterParseException Invalid() => new(["Invalid explicit onboarding charter profile."]);
    }

    // Fold Markdown prose only for semantic validation; callers retain original content and native blob identity.
    private static string HumanProse(string text) =>
        string.Join(' ', text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    public static string Render(GroundedCharterDraft draft)
    {
        draft.Validate();
        var text = new StringBuilder(Marker + "\n\n# Product charter\n\n");
        text.Append("## Purpose\n").Append(draft.Purpose).Append("\n\n");
        text.Append("## Intended Users\n").Append(draft.IntendedUsers).Append("\n\n");
        text.Append("## Goals\n").AppendJoin('\n', draft.Goals.Select(g => "- " + g)).Append("\n\n");
        Field("Non-Goals", draft.NonGoals);
        Field("Success Measures", draft.SuccessMeasures);
        return text.ToString();

        void Field(string heading, CharterKnowledgeList value)
        {
            text.Append("## ").Append(heading).Append('\n');
            text.Append(value.Knowledge.State == CharterKnowledgeState.Unknown ? "Unknown: " + value.Knowledge.Reason
                : string.Join('\n', value.Values.Select(v => "- " + v))).Append("\n\n");
        }
    }
}
