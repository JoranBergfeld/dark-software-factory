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

    private static GroundedCharterDraft Draft() => new("shiftboard", "Coordinate volunteer shifts without double booking.",
        "Volunteer coordinators arranging staffed shifts.", ["Assign volunteers to available shifts."],
        new(new(CharterKnowledgeState.Unknown, "No authoritative non-goals are recorded."), []),
        new(new(CharterKnowledgeState.Unknown, "No measured targets are recorded."), []));
}
