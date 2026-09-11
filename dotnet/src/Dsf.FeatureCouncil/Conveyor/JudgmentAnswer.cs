namespace Dsf.FeatureCouncil.Conveyor;

internal static class JudgmentAnswer
{
    public static (LensPosition Position, string Rationale) Parse(string? answer, bool allowAbstain)
    {
        var text = answer?.Trim() ?? "";
        foreach (var (token, position) in new[]
        {
            ("NO-GO", LensPosition.NoGo), ("GO", LensPosition.Go), ("ABSTAIN", LensPosition.Abstain),
        })
        {
            if ((!allowAbstain && position == LensPosition.Abstain)
                || !text.StartsWith(token, StringComparison.OrdinalIgnoreCase)
                || text.Length <= token.Length
                || (!char.IsWhiteSpace(text[token.Length]) && text[token.Length] != ':'))
            {
                continue;
            }

            var rationale = text[token.Length..].Trim().TrimStart(':', '-', '.').Trim();
            if (rationale.Any(char.IsLetterOrDigit))
            {
                return (position, rationale);
            }
        }

        throw new InvalidOperationException(
            $"malformed or incomplete judgment: expected a complete {(allowAbstain ? "GO/NO-GO/ABSTAIN" : "GO/NO-GO")} token and rationale");
    }
}
