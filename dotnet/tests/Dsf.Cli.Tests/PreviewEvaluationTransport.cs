using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.FeatureCouncil.Conveyor;
using Xunit;

namespace Dsf.Cli.Tests;

internal sealed partial class PreviewPreparationTransport
{
    internal List<string> EvaluationInference = [];
    internal List<string> SourceInferenceInputs = [];
    internal Func<string, string, string>? AlterCompletion { get; set; }
    internal Action<JsonNode>? AfterEvaluationWrite { get; set; }
    internal Func<JsonNode, JsonNode>? AlterApplication { get; set; }
    internal HttpStatusCode ApplicationQueryStatus { get; set; } = HttpStatusCode.OK;
    internal void Restart(DateTimeOffset? now = null)
    {
        StopPreviewHosts();
        if (now is not null) scenario.Source.Source!.Clock.Now = now.Value;
        var documents = JsonSerializer.Deserialize<Dictionary<string, string>>(JsonSerializer.Serialize(SeedDocuments))!;
        var resources = JsonSerializer.Deserialize<Dictionary<string, JsonNode>>(
            JsonSerializer.Serialize(scenario.Source.Source!.Resources))!;
        SeedDocuments.Clear();
        foreach (var pair in documents) SeedDocuments.Add(pair.Key, pair.Value);
        scenario.Reconstruct();
        scenario.Source.Source!.Resources.Clear();
        foreach (var pair in resources) scenario.Source.Source.Resources.Add(pair.Key, pair.Value);
        scenario.Machine.Boundary.Repository.ReservationClock = scenario.Source.Source!.Clock;
    }

    private async Task<HttpResponseMessage> ApplicationQueryAsync(HttpRequestMessage request, CancellationToken token)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        var query = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
        var projected = ((string)query["query"]!).Contains(", Name", StringComparison.Ordinal);
        TelemetryQueries++;
        if (ApplicationQueryStatus != HttpStatusCode.OK) return new(ApplicationQueryStatus);
        var columns = new List<object> { new { name = "TimeGenerated", type = "datetime" }, new { name = "_ResourceId", type = "string" },
            new { name = "_ItemId", type = "string" }, new { name = "TenantId", type = "string" }, new { name = "Type", type = "string" } };
        var row = new List<object> { scenario.Source.Source!.Clock.Now.AddMinutes(-1).ToString("O"),
            FactoryTelemetryWorkflowTests.Component, "genuine-application-record-214", "44444444-4444-4444-4444-444444444444", "AppRequests" };
        if (projected)
        {
            columns.AddRange(new object[] { new { name = "Name", type = "string" }, new { name = "Success", type = "bool" },
                new { name = "ResultCode", type = "string" }, new { name = "DurationMs", type = "real" } });
            row.AddRange(["POST /shifts/confirm", false, "409", 42.5]);
        }
        var body = JsonSerializer.SerializeToNode(new { tables = new[] { new { name = "PrimaryResult", columns, rows = new[] { row } } } })!;
        var response = Reply(AlterApplication?.Invoke(body) ?? body);
        response.Headers.Add("x-ms-request-id", Guid.NewGuid().ToString());
        return response;
    }

    private HttpResponseMessage NativeEvaluationResponse(ModelRouteSelection route, JsonNode body, string? prompt)
    {
        EvaluationInference.Add(route.Name);
        HttpResponseMessage response;
        if (route.Name == "embedding")
        {
            response = Reply(new { model = route.Model, data = body["input"]!.AsArray().Select((_, i) => new
                { index = i, embedding = Enumerable.Range(0, route.Dimensions).Select(d => d == 0 ? 1.0 : 0.0).ToArray() }).ToArray() });
        }
        else
        {
            if (prompt!.Contains("INFER_INTENT", StringComparison.Ordinal)
                || prompt.Contains("CHECK_EVERY_CLAIM", StringComparison.Ordinal)
                || prompt.StartsWith("CHECK_UNCHANGED_GOVERNING_INTENT", StringComparison.Ordinal))
                SourceInferenceInputs.Add(prompt);
            var text = Completion(prompt ?? throw new JsonException());
            text = AlterCompletion?.Invoke(prompt!, text) ?? text;
            response = Reply(new { model = route.Model, choices = new[] { new { finish_reason = "stop", message = new { content = text } } } });
        }
        response.Headers.Add("x-request-id", Guid.NewGuid().ToString());
        return response;
    }

    private static string Completion(string prompt)
    {
        if (prompt.Contains("UNTRUSTED_JSON_DATA:", StringComparison.Ordinal))
        {
            var input = JsonNode.Parse(prompt[(prompt.IndexOf("UNTRUSTED_JSON_DATA:", StringComparison.Ordinal) + 20)..])!;
            var content = (string)input["source"]!;
            if (prompt.StartsWith("CHECK_UNCHANGED_GOVERNING_INTENT", StringComparison.Ordinal))
                return JsonSerializer.Serialize(new SourceCompatibilityResult(true, input["targets"]!.AsArray()
                    .Select(t => new SourceCompatibilityJudgment((string)t!, SourceCompatibilityVerdict.Compatible,
                        "The scoped scheduling evidence remains consistent with the unchanged human charter.", content[..Math.Min(64, content.Length)])).ToArray()),
                    FactoryGitHubJson.Options);
            if (prompt.Contains("CHECK_EVERY_CLAIM", StringComparison.Ordinal))
                return JsonSerializer.Serialize(new IntentVerification(true, input["previous"]!["claims"]!.AsArray().Select((c, i) =>
                {
                    var excerpt = (string)c!["evidence"]![0]!["excerpt"]!;
                    return content.Contains(excerpt, StringComparison.Ordinal)
                        ? new IntentClaimCheck(i, IntentSupportVerdict.Supports, "The original source expressly supports this scheduling claim.", excerpt)
                        : new IntentClaimCheck(i, IntentSupportVerdict.Neutral, "This separate original chunk does not substantively address this claim.", "");
                }).ToArray(), []), FactoryGitHubJson.Options);
            if (input["previous"] is { } previous) return previous.ToJsonString();
            return Dsf.Runtime.Tests.IntentModelFixture.Complete(prompt).Replace("\"pathIndex\":1", "\"pathIndex\":" + (int)input["pathIndex"]!, StringComparison.Ordinal);
        }
        if (prompt.StartsWith("PROPOSAL_SYNTHESIS", StringComparison.Ordinal))
        {
            var evidence = JsonNode.Parse(prompt[(prompt.LastIndexOf(" Evidence: ", StringComparison.Ordinal) + 11)..])!;
            return JsonSerializer.Serialize(new ProposalSynthesis(true,
                new("Explain the observed application request", "Show an actionable explanation of the observed shift conflict.",
                    [new("request", "A shift confirmation request returned 409.", [(string)evidence[0]!["reference"]!])],
                    ["Frequency and root cause remain unknown."]), "A genuine attributable request supports this bounded proposal."));
        }
        if (prompt.StartsWith("SEMANTIC_GROUNDING", StringComparison.Ordinal))
        {
            const string marker = " Original evidence including possible counterevidence: ";
            var evidence = JsonNode.Parse(prompt[(prompt.LastIndexOf(marker, StringComparison.Ordinal) + marker.Length)..])!;
            return JsonSerializer.Serialize(new SemanticGroundingResult(true, true, "The original request supports this bounded claim.",
                [new("request", ClaimSupport.Supported, "The returned code is 409.",
                    [new((string)evidence[0]!["reference"]!, "409")], [])], ["Prevalence and cause remain unknown."]));
        }
        return "GO: The actual scoped request supports a bounded explanation while prevalence and root cause remain unknown.";
    }
}
