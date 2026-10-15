using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.FeatureCouncil.Conveyor;
using Xunit;

namespace Dsf.FeatureCouncil.Tests;

public sealed class OnboardingEvaluationFailureTests
{
    [Theory]
    [InlineData("all-selected")]
    [InlineData("optional-empty")]
    [InlineData("optional-unavailable")]
    public async Task Every_selected_source_is_gathered_and_optional_failures_are_not_silently_dropped(string condition)
    {
        var fixture = new EvaluationFixture { Store = new SerializedEvaluationStore() };
        var azure = new CountingEvidenceGatherer("azuremonitor", [EvaluationFixture.Record]);
        EvidenceItem[] foundryItems = condition == "all-selected"
            ? [new("foundryiq", "private:assignment-guide", "The authorized assignment guide describes rejecting overlapping shifts.")] : [];
        EvidenceItem[] webItems = condition == "all-selected"
            ? [new("webiq", "webiq:sha256:" + new string('b', 64), "A public scheduling guide describes explaining conflicting shift assignments.")] : [];
        var foundry = new CountingEvidenceGatherer("foundryiq", foundryItems);
        var web = new CountingEvidenceGatherer("webiq", webItems);
        var context = EvaluationFixture.Context();
        var originals = new[] { EvaluationFixture.Record }.Concat(foundryItems).Concat(webItems).ToArray();
        var run = new ConveyorRun
        {
            Profile = ConveyorProfile.OnboardingPreview,
            EvaluationContext = context with
            {
                Evidence = originals.Select(item => new CouncilEvidenceProvenance(item.SourceKind, item.Reference,
                    FactoryModelsValidation.Hash(item.Summary), EvaluationFixture.Revision, "private:observation", EvaluationFixture.Now)).ToArray(),
            },
            ProductHints = ["acme"], SourceKinds = ["azuremonitor", "foundryiq", "webiq"], DryRun = true,
        };
        var services = fixture.Services() with
        {
            EvidenceGatherers = condition == "optional-unavailable" ? [azure, foundry] : [azure, foundry, web],
        };

        await ConveyorLine.RunAsync(run, services, CancellationToken.None);

        Assert.Equal(1, azure.Calls);
        Assert.Equal(1, foundry.Calls);
        if (condition == "optional-unavailable")
        {
            Assert.Equal(RunStatus.Error, run.Status);
            Assert.DoesNotContain("s2_investigation", run.Checkpoints);
            Assert.Empty(fixture.Models.Requests);
        }
        else
        {
            Assert.Equal(1, web.Calls);
            Assert.True(run.Status == RunStatus.Previewed, run.FailureReason);
            Assert.Equal(condition == "all-selected" ? 3 : 1, Assert.Single(run.Proposals).EvidenceReferences.Count);
        }
        Assert.Empty(fixture.Filer.Filed);
    }

    [Theory]
    [InlineData("synthesis", "invalid-json")]
    [InlineData("synthesis", "null")]
    [InlineData("synthesis", "incomplete")]
    [InlineData("synthesis", "missing")]
    [InlineData("synthesis", "duplicate")]
    [InlineData("synthesis", "duplicate-case")]
    [InlineData("synthesis", "unknown-property")]
    [InlineData("synthesis", "invented-citation")]
    [InlineData("grounding", "invalid-json")]
    [InlineData("grounding", "null")]
    [InlineData("grounding", "incomplete")]
    [InlineData("grounding", "missing")]
    [InlineData("grounding", "duplicate")]
    [InlineData("grounding", "numeric-support")]
    [InlineData("grounding", "invented-citation")]
    [InlineData("grounding", "invented-excerpt")]
    [InlineData("grounding", "contradicted")]
    [InlineData("grounding", "uncertain")]
    [InlineData("grounding", "unsupported-body")]
    public async Task Malformed_or_unsupported_native_model_content_never_reaches_judgment(string stage, string defect)
    {
        var fixture = new EvaluationFixture { Store = new SerializedEvaluationStore() };
        var result = JsonNode.Parse(stage == "synthesis" ? fixture.Models.SynthesisAnswer : fixture.Models.GroundingAnswer)!;
        if (defect == "incomplete") result["complete"] = false;
        if (defect == "unknown-property") result["syntheticEvidence"] = "invented";
        if (stage == "synthesis")
        {
            if (defect == "missing") result["candidate"]!.AsObject().Remove("claims");
            if (defect == "invented-citation") result["candidate"]!["claims"]![0]!["evidenceReferences"]![0] = "charter:goal";
        }
        else
        {
            if (defect == "missing") result["checks"]!.AsArray().Clear();
            if (defect == "duplicate") result["checks"]!.AsArray().Add(result["checks"]![0]!.DeepClone());
            if (defect == "invented-citation") result["checks"]![0]!["citations"]![0]!["reference"] = "charter:goal";
            if (defect == "invented-excerpt") result["checks"]![0]!["citations"]![0]!["excerpt"] = "All coordinators abandoned the application.";
            if (defect == "contradicted") result["checks"]![0]!["support"] = "Contradicted";
            if (defect == "uncertain") result["checks"]![0]!["support"] = "Uncertain";
            if (defect == "numeric-support") result["checks"]![0]!["support"] = 0;
            if (defect == "unsupported-body") result["contentSupported"] = false;
        }
        var answer = defect == "invalid-json" ? "not structured JSON" : defect == "null" ? "null" : result.ToJsonString();
        if (stage == "synthesis" && defect == "duplicate") answer = answer.Replace("\"complete\":true", "\"complete\":true,\"complete\":true");
        if (stage == "synthesis" && defect == "duplicate-case") answer = answer.Replace("\"complete\":true", "\"complete\":true,\"Complete\":true");
        if (stage == "synthesis") fixture.Models.SynthesisAnswer = answer;
        else fixture.Models.GroundingAnswer = answer;

        var run = await ConveyorLine.RunAsync(fixture.Run(), fixture.Services(), CancellationToken.None);

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.DoesNotContain(stage == "synthesis" ? "s3_synthesis" : "s4_grounding", run.Checkpoints);
        Assert.DoesNotContain("s5_council", run.Checkpoints);
        Assert.Empty(run.PreviewedIssues);
        Assert.All(run.Proposals, proposal => Assert.Null(proposal.IntendedPublication));
        Assert.DoesNotContain(fixture.Models.Requests, request => request.Route.StartsWith("juror-", StringComparison.Ordinal));
        Assert.Empty(fixture.Filer.Filed);
    }

    [Theory]
    [InlineData("missing-juror")]
    [InlineData("malformed-juror")]
    [InlineData("wrong-native-family")]
    [InlineData("changed-model-revision")]
    [InlineData("wrong-output-hash")]
    [InlineData("unavailable")]
    public async Task Missing_or_unverified_individual_juror_never_counts_as_a_complete_evaluation(string defect)
    {
        var fixture = new EvaluationFixture { Store = new SerializedEvaluationStore() };
        fixture.Models.AnswerOverride = (route, _) => route != "juror-2" ? null
            : defect == "missing-juror" ? "" : defect == "malformed-juror" ? "MAYBE: undecided" : null;
        fixture.Models.ObservationOverride = observation => observation.Route != "juror-2" ? observation : defect switch
        {
            "wrong-native-family" => observation with { ResponseModel = "gpt-4.1" },
            "changed-model-revision" => observation with { ConfigurationRevision = new string('b', 64) },
            "wrong-output-hash" => observation with { OutputSha256 = new string('c', 64) },
            "unavailable" => observation with { State = ModelCapabilityState.Blocked },
            _ => observation,
        };

        var run = await ConveyorLine.RunAsync(fixture.Run(), fixture.Services(), CancellationToken.None);

        var review = Assert.Single(run.Proposals).CouncilReview!;
        Assert.Equal(2, review.Rounds.Count);
        Assert.Single(review.JuryVerdicts);
        Assert.Equal(ProposalVerdict.Error, review.Outcome);
        Assert.Equal(RunStatus.Error, run.Status);
        Assert.DoesNotContain("s5_council", run.Checkpoints);
        Assert.DoesNotContain(fixture.Models.Requests, request => request.Route == "juror-3");
        Assert.Empty(run.PreviewedIssues);
        Assert.Empty(fixture.Filer.Filed);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("no-candidate")]
    [InlineData("early-triage")]
    public async Task Empty_or_early_terminal_runs_do_not_invent_an_evaluated_candidate(string outcome)
    {
        var fixture = new EvaluationFixture { Store = new SerializedEvaluationStore() };
        if (outcome == "empty") fixture.Items = [];
        if (outcome == "no-candidate")
            fixture.Models.SynthesisAnswer = """{"complete":true,"candidate":null,"reason":"This observation does not justify a distinct proposed feature."}""";
        var run = outcome == "early-triage"
            ? new ConveyorRun { Profile = ConveyorProfile.OnboardingPreview, EvaluationContext = EvaluationFixture.Context(), DryRun = true }
            : fixture.Run();

        await ConveyorLine.RunAsync(run, fixture.Services(), CancellationToken.None);

        Assert.Empty(run.Proposals);
        Assert.Empty(run.PreviewedIssues);
        Assert.Empty(run.FiledIssues);
        Assert.Empty(fixture.Filer.Filed);
        Assert.DoesNotContain(fixture.Models.Requests, request => request.Route.StartsWith("juror-", StringComparison.Ordinal));
        if (outcome == "early-triage")
        {
            Assert.Equal(RunStatus.Killed, run.Status);
            Assert.Equal(["s1_triage"], run.Checkpoints);
        }
        else Assert.Equal(RunStatus.Previewed, run.Status);
        if (outcome == "no-candidate") Assert.Null(Assert.Single(run.SynthesisEvaluations).Result.Candidate);
        else Assert.Empty(fixture.Models.Requests);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("source")]
    [InlineData("reference")]
    public async Task Gathered_evidence_must_match_pinned_original_provenance_before_any_model_use(string changed)
    {
        var fixture = new EvaluationFixture();
        var run = fixture.Run();
        fixture.Items = [changed switch
        {
            "summary" => EvaluationFixture.Record with { Summary = "A different application's coordinator failed to assign a shift." },
            "source" => EvaluationFixture.Record with { SourceKind = "webiq" },
            _ => EvaluationFixture.Record with { Reference = "telemetry:different-application" },
        }];

        await ConveyorLine.RunAsync(run, fixture.Services(), CancellationToken.None);

        Assert.Equal(RunStatus.Error, run.Status);
        Assert.Empty(fixture.Models.Requests);
        Assert.Empty(run.Proposals);
        Assert.Empty(fixture.Filer.Filed);
    }
}
