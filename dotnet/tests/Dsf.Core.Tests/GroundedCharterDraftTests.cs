using Dsf.Core.Charters;
using Xunit;

namespace Dsf.Core.Tests;

public sealed class GroundedCharterDraftTests
{
    [Fact]
    public void Explicit_unknown_optional_fields_do_not_require_fabricated_values()
    {
        var draft = Draft();
        draft.Validate();
        Assert.Empty(draft.NonGoals.Values);
        Assert.Empty(draft.SuccessMeasures.Values);
        Assert.Equal("grounded-onboarding-draft-v1", draft.Profile);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("To be determined after review.")]
    [InlineData("The product goal remains unknown.")]
    [InlineData("Goals are not documented yet.")]
    public void Unknown_markers_are_not_substantive_goals(string goal) =>
        Assert.Throws<CharterParseException>(() => (Draft() with { Goals = [goal] }).Validate());

    [Fact]
    public void An_unknown_measure_cannot_hide_a_known_value()
    {
        var draft = Draft();
        Assert.Throws<CharterParseException>(() => (draft with
            { SuccessMeasures = draft.SuccessMeasures with { Values = ["Reach a million users"] } }).Validate());
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\t")]
    [InlineData("\u000b")]
    [InlineData("\u001b")]
    public void Machine_drafts_still_reject_multiline_or_control_characters(string separator) =>
        Assert.Throws<CharterParseException>(() => (Draft() with
            { Purpose = "Coordinate" + separator + "volunteer shifts without double booking." }).Validate());

    [Theory]
    [InlineData("\u000b")]
    [InlineData("\u000c")]
    [InlineData("\u0085")]
    [InlineData("\u001b")]
    [InlineData("\0")]
    public void Human_charter_parsing_cannot_normalize_unsafe_controls_into_valid_prose(string control)
    {
        var content = OnboardingCharterMarkdown.Render(Draft())
            .Replace("Coordinate volunteer", "Coordinate" + control + "volunteer", StringComparison.Ordinal);
        Assert.Throws<CharterParseException>(() => OnboardingCharterMarkdown.Parse(content, "shiftboard"));
    }

    [Theory]
    [InlineData("Unknown\npurpose requires future authoring.")]
    [InlineData("The product\npurpose is unknown.")]
    public void Human_charter_line_wrapping_cannot_make_unknown_core_intent_substantive(string purpose)
    {
        var content = OnboardingCharterMarkdown.Render(Draft()).Replace(Draft().Purpose, purpose, StringComparison.Ordinal);
        Assert.Throws<CharterParseException>(() => OnboardingCharterMarkdown.Parse(content, "shiftboard"));
    }

    private static GroundedCharterDraft Draft() => new("shiftboard", "Coordinate volunteer shifts without double booking.",
        "Volunteer coordinators arranging staffed shifts.", ["Assign volunteers to available shifts."],
        new(new(CharterKnowledgeState.Unknown, "No authoritative non-goals are recorded."), []),
        new(new(CharterKnowledgeState.Unknown, "No measured targets are recorded."), []));
}
