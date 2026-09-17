using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Runtime;
using Dsf.Core.Onboarding;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class PreviewModelConsumerTests
{
    private const string Input = "The supplied application observation describes a failed volunteer assignment request.";
    private const string Answer = "GO: The supplied failure describes a specific application problem with attributable observations and bounded uncertainty.";

    [Fact]
    public async Task Operator_reads_current_native_model_authority_through_council_and_model_hosts()
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true);
        using var http = new HttpClient(transport, false);
        var environment = transport.Source.Environment;
        environment["AZURE_CLIENT_ID"] = transport.Models.Bindings![0].ClientId;
        var runner = new PreviewContextRunner(transport.Source) { Route = "/model-authority", Request = transport.Request };
        var before = transport.Intent.Models.Inference.Count;
        Assert.Equal(0, await RuntimeCliApplication.InvokeAsync(["serve-preview"], environment, new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with { WebHostRunner = runner, PreviewHttp = http,
                PreviewCredential = new IntentModelCredential(transport.Source), PreviewClock = transport.Source.Clock }, CancellationToken.None));
        Assert.True(runner.Status == HttpStatusCode.OK, runner.Body);
        var body = JsonDocument.Parse(runner.Body).RootElement;
        Assert.Equal(transport.Request.RequestId, body.GetProperty("requestId").GetGuid());
        Assert.Equal(5, body.GetProperty("authority").GetProperty("routes").GetArrayLength());
        Assert.Equal(before, transport.Intent.Models.Inference.Count);
    }

    [Fact]
    public async Task Council_consumer_runs_native_singleton_embedding_and_all_reviewed_text_routes()
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true);
        transport.Intent.Models.Completion = input => input.StartsWith("Independently judge", StringComparison.Ordinal)
            ? "GO: The supplied failure describes a specific application problem with attributable observations and bounded uncertainty."
            : "The observed application request failure prevented saving a volunteer shift assignment and warrants evidence-grounded evaluation.";
        using var http = new HttpClient(transport, false);
        var environment = transport.Source.Environment;
        environment["AZURE_CLIENT_ID"] = transport.Models.Bindings![0].ClientId;
        var settings = FactoryHostSettings.Read(environment, "Preview models");
        var consumer = await RuntimeVerbs.OpenPreviewModelsAsync(settings, RuntimeDependencies.Production() with
        { PreviewHttp = http, PreviewCredential = new IntentModelCredential(transport.Source), PreviewClock = transport.Source.Clock },
            transport.Request, CancellationToken.None);
        var before = transport.Intent.Models.Inference.Count;
        var text = "A real selected application request failed while saving a volunteer shift assignment.";
        var grouped = await consumer.DeduplicateAsync([new("observation-1", text)], CancellationToken.None);
        Assert.Equal(new[] { "observation-1" }, Assert.Single(grouped.Groups));
        var payload = transport.Intent.Models.Inference[before];
        Assert.Equal(text, (string?)Assert.Single(payload["input"]!.AsArray()));
        Assert.Equal(1536, grouped.Observation.Dimensions);
        var synthesis = await consumer.SynthesizeAsync(text, CancellationToken.None);
        Assert.Equal(FactoryModelsValidation.Hash(synthesis.Text), synthesis.Observation.OutputSha256);
        foreach (var juror in new[] { "juror-1", "juror-2", "juror-3" })
        {
            var result = await consumer.JudgeAsync(juror, text, CancellationToken.None);
            Assert.Equal(juror, result.Observation.Route);
            Assert.StartsWith("GO:", result.Text);
        }
        var current = await consumer.ReadAuthorityAsync(CancellationToken.None);
        Assert.Equal(new[] { "synthesis", "embedding", "juror-1", "juror-2", "juror-3" }, current.Routes.Select(r => r.Route));
        Assert.Equal(5, transport.Intent.Models.Inference.Count - before);
        Assert.DoesNotContain(transport.Requests, r => r.Uri.Host == new Uri(transport.Models.Routes![0].Selection.Endpoint).Host);
    }

    [Fact]
    public async Task Model_evaluation_host_requires_explicit_protected_model_consumption_binding()
    {
        using var transport = await PreviewContextTransport.CreateAsync();
        using var http = new HttpClient(transport, false);
        var environment = transport.Source.Environment;
        environment["AZURE_CLIENT_ID"] = transport.Models.Bindings![0].ClientId;
        var runner = new PreviewContextRunner(transport.Source)
        {
            Route = "/evaluate", Caller = transport.Models.Bindings[0].PrincipalId!, Client = transport.Models.Bindings[0].ClientId!,
            Request = new { preview = transport.Request, route = "authority", input = (string?)null, items = (object?)null },
        };
        var exit = await RuntimeCliApplication.InvokeAsync(["serve-preview-models"], environment, new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with { WebHostRunner = runner, ModelHttp = http, ModelCredential = new IntentModelCredential(transport.Source),
                ModelClock = transport.Source.Clock }, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Equal(HttpStatusCode.Forbidden, runner.Status);
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("code")]
    [InlineData("foreign")]
    [InlineData("client")]
    public async Task Model_service_never_admits_operator_code_or_foreign_callers(string caller)
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true);
        var principal = caller == "operator" ? transport.Source.Manifest.Identity.AzureActorId
            : caller == "code" ? transport.Source.Code.PrincipalId : caller == "foreign" ? Guid.NewGuid().ToString() : transport.Models.Bindings![0].PrincipalId;
        var before = transport.Intent.Models.Inference.Count;
        var result = await transport.RunModelAsync(new(transport.Request, "synthesis", Input, null), principal,
            caller == "client" ? transport.Source.Code.ClientId : transport.Models.Bindings![0].ClientId);
        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.Equal(before, transport.Intent.Models.Inference.Count);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("input")]
    [InlineData("capability")]
    [InlineData("authority")]
    [InlineData("text")]
    [InlineData("erased")]
    [InlineData("oversized")]
    public async Task Unbound_or_malformed_model_results_never_become_confirmed_observations(string corruption)
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true);
        using var http = new HttpClient(transport, false);
        var consumer = await OpenAsync(transport, http);
        var recorded = consumer.Observations.Count;
        transport.ChangeModelResponse = text =>
        {
            var result = JsonNode.Parse(text)!;
            if (corruption == "request") result["requestId"] = Guid.NewGuid();
            if (corruption == "input") result["inputSha256"] = new string('0', 64);
            if (corruption == "capability") result["capabilityRevision"] = new string('0', 64);
            if (corruption == "authority") result["authority"]!["checkId"] = Guid.NewGuid();
            if (corruption == "text") result["completion"]!["text"] = "This response was substituted without matching the native output observation.";
            if (corruption == "erased") result.AsObject().Remove("completion");
            if (corruption == "oversized") result["completion"]!["text"] = new string('x', 129 * 1024);
            return result.ToJsonString();
        };
        var error = await Assert.ThrowsAsync<PreviewCapabilityException>(() => consumer.SynthesizeAsync(Input, CancellationToken.None));
        Assert.Equal(FactoryCharterState.Failed, error.State);
        Assert.Equal(recorded, consumer.Observations.Count);
        var inference = transport.Intent.Models.Inference.Count;
        Assert.Equal(FactoryCharterState.Unknown, (await Assert.ThrowsAsync<PreviewCapabilityException>(() =>
            consumer.SynthesizeAsync(Input, CancellationToken.None))).State);
        Assert.Equal(inference, transport.Intent.Models.Inference.Count);
    }

    [Theory]
    [InlineData("juror")]
    [InlineData("grant")]
    [InlineData("capability")]
    [InlineData("host")]
    public async Task Revoked_current_authority_blocks_dispatch_despite_retained_successful_model_proof(string change)
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true);
        using var http = new HttpClient(transport, false);
        var consumer = await OpenAsync(transport, http);
        if (change == "juror")
            transport.Intent.Models.Resources[FactoryModelsValidation.DeploymentTarget(transport.Models.Routes![4].Selection)]["properties"]!["model"]!["version"] = "changed";
        if (change == "grant") transport.Intent.Models.Resources[transport.Models.Grants![1].Target]["properties"]!["principalId"] = Guid.NewGuid().ToString();
        if (change == "capability") transport.Source.Values.Remove(ProductConfigurationKeys.FactoryPreview);
        if (change == "host")
            transport.Native[FactoryPreviewValidation.HostTarget(transport.Group, transport.Capability.Configuration.FactoryId, PreviewHostKind.ModelEvaluation)]
                ["properties"]!["template"]!["containers"]![0]!["image"] = "unapproved:latest";
        var before = transport.Intent.Models.Inference.Count;
        await Assert.ThrowsAsync<PreviewCapabilityException>(() => consumer.SynthesizeAsync(Input, CancellationToken.None));
        Assert.Equal(before, transport.Intent.Models.Inference.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authority_changes_during_native_inference_withhold_the_output(bool native)
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true);
        using var http = new HttpClient(transport, false);
        var consumer = await OpenAsync(transport, http);
        transport.Intent.Models.OnInference = () =>
        {
            if (native)
                transport.Intent.Models.Resources[FactoryModelsValidation.DeploymentTarget(transport.Models.Routes![4].Selection)]["properties"]!["model"]!["version"] = "changed";
            else transport.Source.Values[ProductConfigurationKeys.FactoryPreview] =
                JsonSerializer.Serialize(transport.Capability with { State = PreviewCapabilityState.Blocked }, FactoryGitHubJson.Options);
        };
        Assert.Equal(FactoryCharterState.Blocked, (await Assert.ThrowsAsync<PreviewCapabilityException>(() =>
            consumer.SynthesizeAsync(Input, CancellationToken.None))).State);
        Assert.Single(consumer.Observations);
    }

    [Fact]
    public async Task Lost_native_result_is_unknown_and_never_automatically_replayed()
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true);
        using var http = new HttpClient(transport, false);
        var consumer = await OpenAsync(transport, http);
        transport.ChangeModelResponse = _ => throw new HttpRequestException("private-provider-diagnostic");
        var before = transport.Intent.Models.Inference.Count;
        var error = await Assert.ThrowsAsync<PreviewCapabilityException>(() => consumer.SynthesizeAsync(Input, CancellationToken.None));
        Assert.Equal(FactoryCharterState.Unknown, error.State);
        Assert.DoesNotContain("private-provider", error.Message);
        Assert.Equal(before + 1, transport.Intent.Models.Inference.Count);
        await Assert.ThrowsAsync<PreviewCapabilityException>(() => consumer.SynthesizeAsync(Input, CancellationToken.None));
        Assert.Equal(before + 1, transport.Intent.Models.Inference.Count);
    }

    [Fact]
    public async Task Serialized_independent_consumers_recheck_native_authority_and_keep_original_configuration()
    {
        using var first = await PreviewContextTransport.CreateAsync(modelEvaluation: true);
        using var firstHttp = new HttpClient(first, false);
        var initial = await OpenAsync(first, firstHttp);
        var a = await initial.JudgeAsync("juror-3", Input, CancellationToken.None);
        using var second = await PreviewContextTransport.ReconstructAsync(first);
        using var secondHttp = new HttpClient(second, false);
        var restored = await OpenAsync(second, secondHttp);
        var b = await restored.JudgeAsync("juror-3", Input, CancellationToken.None);
        Assert.Equal(a.Observation.ConfigurationRevision, b.Observation.ConfigurationRevision);
        Assert.Equal(a.Observation.ResponseModel, b.Observation.ResponseModel);
        Assert.NotEqual(initial.Observations.Last().RequestId, restored.Observations.Last().RequestId);
        Assert.Equal(5, restored.Observations.Last().Authority.Routes.Count);
        Assert.Equal(FactoryModelsValidation.Hash(b.Text), restored.Observations.Last().Output!.OutputSha256);
    }

    private static Task<IFactoryPreviewModelWorkloads> OpenAsync(PreviewContextTransport transport, HttpClient http)
    {
        transport.Intent.Models.Completion = _ => Answer;
        var environment = transport.Source.Environment;
        environment["AZURE_CLIENT_ID"] = transport.Models.Bindings![0].ClientId;
        return RuntimeVerbs.OpenPreviewModelsAsync(FactoryHostSettings.Read(environment, "Preview models"), RuntimeDependencies.Production() with
        { PreviewHttp = http, PreviewCredential = new IntentModelCredential(transport.Source), PreviewClock = transport.Source.Clock },
            transport.Request, CancellationToken.None);
    }

    [Fact]
    public async Task Native_model_response_is_bounded_while_streaming_not_after_allocation()
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true);
        using var http = new HttpClient(transport, false);
        var consumer = await OpenAsync(transport, http);
        using var stream = new OversizedNativeStream();
        transport.ModelNativeResponse = request => request.Method == HttpMethod.Post
            ? new(HttpStatusCode.OK) { Content = new StreamContent(stream) } : null;
        Assert.Equal(FactoryCharterState.Failed, (await Assert.ThrowsAsync<PreviewCapabilityException>(() =>
            consumer.SynthesizeAsync(Input, CancellationToken.None))).State);
        Assert.InRange(stream.BytesRead, 1, 1024 * 1024 + 8192);
    }

    [Fact]
    public async Task Model_consumer_admission_has_a_controlled_deadline()
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true);
        using var http = new HttpClient(transport, false);
        transport.Source.BeforeRequestAsync = (request, token) =>
        {
            transport.Source.Clock.Now += FactoryPreviewValidation.RequestBudget + TimeSpan.FromSeconds(1);
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        var failure = await Assert.ThrowsAsync<PreviewCapabilityException>(() => OpenAsync(transport, http));
        Assert.Equal(FactoryCharterState.Unknown, failure.State);
        Assert.DoesNotContain(transport.Requests, r => r.Uri.Host == new Uri(PreviewContextTransport.ModelEndpoint).Host);
    }

    [Theory]
    [InlineData("dimensions")]
    [InlineData("lost-group")]
    [InlineData("duplicate")]
    public async Task Singleton_embeddings_cannot_accept_malformed_native_vectors_or_substituted_groups(string failure)
    {
        using var transport = await PreviewContextTransport.CreateAsync(modelEvaluation: true);
        using var http = new HttpClient(transport, false);
        var consumer = await OpenAsync(transport, http);
        if (failure == "dimensions") transport.Intent.Models.Scenario = "embedding-dimensions";
        else transport.ChangeModelResponse = text =>
        {
            var result = JsonNode.Parse(text)!;
            result["deduplication"]!["groups"] = failure == "duplicate"
                ? new JsonArray(new JsonArray("observation-1", "observation-1")) : new JsonArray();
            return result.ToJsonString();
        };
        await Assert.ThrowsAsync<PreviewCapabilityException>(() => consumer.DeduplicateAsync([new("observation-1", Input)], CancellationToken.None));
        Assert.Single(consumer.Observations);
    }

    private sealed class OversizedNativeStream : Stream
    {
        internal int BytesRead;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var size = Math.Min(count, 2 * 1024 * 1024 - BytesRead);
            buffer.AsSpan(offset, size).Fill((byte)' ');
            BytesRead += size;
            return size;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var size = Math.Min(buffer.Length, 2 * 1024 * 1024 - BytesRead);
            buffer.Span[..size].Fill((byte)' ');
            BytesRead += size;
            return ValueTask.FromResult(size);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
