using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Runtime;
using Dsf.Core.Onboarding;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class PreviewCapabilityHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approved_council_reads_actual_adopted_context_without_source_credentials_or_writes(bool coordinator)
    {
        using var transport = await PreviewContextTransport.CreateAsync();
        var before = transport.Source.Progress.ToDictionary(p => p.Key, p => p.Value);
        var records = transport.Source.Values.ToDictionary(p => p.Key, p => p.Value);
        var inference = transport.Intent.Models.Inference.Count;
        var response = await transport.RunAsync(coordinator);
        Assert.True(response.Status == HttpStatusCode.OK, response.Body + "\n" + string.Join("\n", transport.Requests.TakeLast(3)));
        var result = JsonSerializer.Deserialize<PreviewSourceContext>(response.Body, FactorySourceValidation.RecordJson)!;
        Assert.Equal(FactoryCharterState.Adopted, result.Context.State);
        Assert.NotNull(result.Context.Adoption);
        Assert.NotNull(result.Context.Current);
        Assert.Contains("coordinate volunteer shifts", result.Context.Current.Source.Content);
        Assert.NotEmpty(result.Context.Current.Analysis.Chunks);
        Assert.NotEmpty(result.Context.Current.Analysis.Limitations);
        Assert.Equal(before, transport.Source.Progress);
        Assert.Equal(records, transport.Source.Values);
        Assert.Equal(inference, transport.Intent.Models.Inference.Count);
        Assert.Equal(coordinator ? 1 : 0, transport.ContextCalls);
        Assert.DoesNotContain(SourceInventoryTransport.InstallationToken, response.Body);
        Assert.DoesNotContain(transport.Requests, r => r.Method is "PUT" or "PATCH" or "DELETE");
        Assert.All(transport.Source.BrokerClaims, claim => Assert.Equal(transport.Source.Code.PrincipalId, (string?)claim["oid"]));
        Assert.All(transport.Models.Routes!, route =>
            Assert.Contains(transport.Intent.Models.ArmRequests, path => path.Contains(FactoryModelsValidation.DeploymentTarget(route.Selection), StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("code")]
    [InlineData("foreign")]
    [InlineData("client")]
    [InlineData("operator-route")]
    public async Task Preview_routes_preserve_council_operator_and_code_caller_isolation(string scenario)
    {
        using var transport = await PreviewContextTransport.CreateAsync();
        var council = transport.Models.Bindings![0];
        var caller = scenario == "operator" ? transport.Source.Manifest.Identity.AzureActorId
            : scenario == "code" ? transport.Source.Code.PrincipalId : scenario == "foreign" ? Guid.NewGuid().ToString() : council.PrincipalId;
        var response = await transport.RunAsync(scenario == "operator-route", caller,
            scenario == "client" ? transport.Source.Code.ClientId : council.ClientId);
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Equal("ApprovedPreviewCallerRequired", JsonSerializer.Deserialize<PreviewContextFailure>(response.Body, FactorySourceValidation.RecordJson)!.Reason);
        Assert.DoesNotContain(transport.Requests, r => r.Uri.Host == new Uri(transport.Source.ProgressEndpoint).Host);
        Assert.Equal(0, transport.ContextCalls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unapproved")]
    [InlineData("foreign")]
    [InlineData("state-erased")]
    [InlineData("null-config")]
    [InlineData("receipt")]
    [InlineData("binding")]
    [InlineData("manifest")]
    [InlineData("adoption-pin")]
    [InlineData("expired-model")]
    public async Task Protected_preview_capability_requires_complete_current_approved_records(string scenario)
    {
        using var transport = await PreviewContextTransport.CreateAsync();
        var values = transport.Source.Values;
        var capability = JsonNode.Parse(values[ProductConfigurationKeys.FactoryPreview])!;
        if (scenario == "missing") values.Remove(ProductConfigurationKeys.FactoryPreview);
        else if (scenario == "receipt") values.Remove(ProductConfigurationKeys.PreviewOperation(transport.Request.OperationId, 3));
        else if (scenario == "binding") values.Remove(ProductConfigurationKeys.PreviewBinding(transport.Request.OperationId, "runs"));
        else if (scenario == "manifest")
        {
            var manifest = JsonNode.Parse(values[ProductConfigurationKeys.FactoryManifest])!;
            manifest.AsObject().Remove("previewApprovalFingerprint");
            values[ProductConfigurationKeys.FactoryManifest] = manifest.ToJsonString();
        }
        else if (scenario == "expired-model") transport.Source.Clock.Now = transport.Source.Clock.Now.AddHours(2);
        else
        {
            if (scenario == "unapproved") capability["state"] = "Partial";
            if (scenario == "foreign") capability["approvedBy"] = Guid.NewGuid().ToString();
            if (scenario == "state-erased") capability.AsObject().Remove("state");
            if (scenario == "null-config") capability["configuration"] = null;
            if (scenario == "adoption-pin") capability["configuration"]!["adoptedCharterRevision"] = Guid.NewGuid();
            values[ProductConfigurationKeys.FactoryPreview] = capability.ToJsonString();
        }
        var response = await transport.RunAsync();
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.DoesNotContain("coordinate volunteer shifts", response.Body);
        Assert.DoesNotContain(transport.Requests, r => r.Uri.Host == new Uri(transport.Source.ProgressEndpoint).Host);
    }

    [Theory]
    [InlineData("host-image")]
    [InlineData("host-caller")]
    [InlineData("extra-identity")]
    [InlineData("model-host")]
    [InlineData("scope")]
    [InlineData("partition")]
    [InlineData("deployment")]
    [InlineData("broad-sql")]
    [InlineData("broad-arm")]
    public async Task Native_preview_bindings_cannot_drift_from_approved_create_receipts(string scenario)
    {
        using var transport = await PreviewContextTransport.CreateAsync();
        var config = transport.Capability.Configuration;
        var context = FactoryPreviewValidation.HostTarget(transport.Group, config.FactoryId, PreviewHostKind.SourceContext);
        if (scenario == "host-image") transport.Native[context]["properties"]!["template"]!["containers"]![0]!["image"] = "foreign:latest";
        if (scenario == "host-caller")
            transport.Native[context + "/authconfigs/current"]["properties"]!["identityProviders"]!["azureActiveDirectory"]!["validation"]!["defaultAuthorizationPolicy"]!["allowedPrincipals"]!["identities"]!
                .AsArray().Add(transport.Source.Manifest.Identity.AzureActorId);
        if (scenario == "extra-identity") transport.Native[context]["identity"]!["userAssignedIdentities"]![transport.Models.Bindings![0].Target] = new JsonObject();
        if (scenario == "model-host") transport.Intent.Models.Resources[FactoryModelsValidation.HostTarget(transport.Models)]["properties"]!["template"]!["containers"]![0]!["image"] = "foreign:latest";
        var targets = FactoryPreviewValidation.Targets(transport.Source.Manifest, transport.Models, transport.Source.GitHub,
            transport.Request.OperationId, config.MetadataRoleDefinitionId);
        if (scenario == "scope") transport.Native[targets[2].Target]["properties"]!["scope"] = transport.Account;
        if (scenario == "partition") transport.Native[targets[0].Target]["properties"]!["resource"]!["partitionKey"]!["paths"] = new JsonArray("/id");
        if (scenario == "deployment") transport.Native.First(p => p.Key.Contains("/operations/", StringComparison.Ordinal)).Value["properties"]!["provisioningOperation"] = "Read";
        if (scenario is "broad-sql" or "broad-arm")
        {
            var target = scenario == "broad-sql" ? transport.Account + "/sqlroleassignments/" + Guid.NewGuid()
                : transport.Group + "/providers/microsoft.authorization/roleassignments/" + Guid.NewGuid();
            transport.Native[target] = JsonSerializer.SerializeToNode(new
            {
                id = target,
                properties = new
                {
                    principalId = transport.Models.Bindings![0].PrincipalId,
                    scope = scenario == "broad-sql" ? transport.Account : transport.Group,
                    roleDefinitionId = scenario == "broad-sql" ? targets[1].Target : config.MetadataRoleDefinitionId,
                }
            })!;
        }
        var response = await transport.RunAsync();
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.DoesNotContain("coordinate volunteer shifts", response.Body);
    }

    [Theory]
    [InlineData("numeric-status")]
    [InlineData("etag-account")]
    public async Task Supported_native_receipts_do_not_require_invented_resource_metadata(string shape)
    {
        using var transport = await PreviewContextTransport.CreateAsync();
        if (shape == "numeric-status")
        {
            foreach (var operation in transport.Native.Where(p => p.Key.Contains("/operations/", StringComparison.Ordinal)).Select(p => p.Value))
                operation["properties"]!["statusCode"] = (string?)operation["properties"]!["statusCode"] == "Created" ? "201" : "200";
        }
        else transport.UseAccountEtag();
        var response = await transport.RunAsync();
        Assert.True(response.Status == HttpStatusCode.OK, response.Body);
    }

    [Theory]
    [InlineData("adoption", FactoryCharterState.Unknown)]
    [InlineData("source-unknown", FactoryCharterState.Unknown)]
    [InlineData("source", FactoryCharterState.Blocked)]
    [InlineData("model", FactoryCharterState.Blocked)]
    [InlineData("model-native", FactoryCharterState.Blocked)]
    [InlineData("charter", FactoryCharterState.Blocked)]
    public async Task Real_current_source_adoption_and_native_model_failures_release_no_context(string revoked, FactoryCharterState state)
    {
        using var transport = await PreviewContextTransport.CreateAsync();
        if (revoked == "adoption") transport.Source.Values.Remove(ProductConfigurationKeys.CharterAdopted);
        if (revoked == "source-unknown")
        {
            var key = "source/" + transport.Source.Authority.OperationId.ToString("N") + "/current";
            transport.Source.Progress[key] = ("{", transport.Source.Progress[key].Revision + 1);
        }
        if (revoked == "source") transport.Source.GitHubStatus = 403;
        if (revoked == "model") transport.Intent.AuthorityUnavailable = true;
        if (revoked == "model-native")
            transport.Intent.Models.Resources[FactoryModelsValidation.DeploymentTarget(transport.Models.Routes![4].Selection)]["properties"]!["model"]!["version"] = "changed";
        if (revoked == "charter") transport.Source.Head = new string('b', 40);
        var response = await transport.RunAsync(coordinator: true);
        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        var result = JsonSerializer.Deserialize<PreviewSourceContext>(response.Body, FactorySourceValidation.RecordJson)!;
        Assert.Equal(state, result.Context.State);
        Assert.Null(result.Context.Adoption);
        Assert.Null(result.Context.Current);
        Assert.DoesNotContain("coordinate volunteer shifts", response.Body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capability_revocation_during_real_source_reads_is_not_released_as_current(bool erase)
    {
        using var transport = await PreviewContextTransport.CreateAsync();
        transport.Intent.AfterAuthorityResponse = () =>
        {
            if (erase) transport.Source.Values.Remove(ProductConfigurationKeys.FactoryPreview);
            else transport.Source.Values[ProductConfigurationKeys.FactoryPreview] =
                JsonSerializer.Serialize(transport.Capability with { State = PreviewCapabilityState.Blocked }, FactoryGitHubJson.Options);
        };
        var response = await transport.RunAsync();
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.DoesNotContain("coordinate volunteer shifts", response.Body);
    }

    [Fact]
    public async Task Council_preview_uses_only_exact_resource_metadata_scopes()
    {
        using var transport = await PreviewContextTransport.CreateAsync();
        transport.EnforceCouncilScopes = true;
        var response = await transport.RunAsync(coordinator: true);
        Assert.True(response.Status == HttpStatusCode.OK, response.Body);
    }

    [Fact]
    public async Task Council_host_cannot_borrow_a_source_workload_credential()
    {
        using var transport = await PreviewContextTransport.CreateAsync();
        using var http = new HttpClient(transport, false);
        var environment = transport.Source.Environment;
        environment["AZURE_CLIENT_ID"] = transport.Models.Bindings![0].ClientId;
        var runner = new PreviewContextRunner(transport.Source) { Route = "/source-context", Request = transport.Request };
        Assert.Equal(0, await RuntimeCliApplication.InvokeAsync(["serve-preview"], environment, new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with
            {
                WebHostRunner = runner,
                PreviewHttp = http,
                PreviewCredential = new SourceTestCredential(transport.Source),
                PreviewClock = transport.Source.Clock
            }, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Forbidden, runner.Status);
        Assert.Equal(0, transport.ContextCalls);
        Assert.DoesNotContain(transport.Requests, r => r.Uri.Host == new Uri(transport.Source.ProgressEndpoint).Host);
    }

    [Fact]
    public async Task Protected_capability_native_ETag_changes_are_not_hidden_by_identical_record_content()
    {
        using var transport = await PreviewContextTransport.CreateAsync();
        transport.Intent.AfterAuthorityResponse = () => transport.CapabilityETag = "changed-native-version";
        var response = await transport.RunAsync();
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.DoesNotContain("coordinate volunteer shifts", response.Body);
    }

    [Fact]
    public async Task Council_preserves_unknown_when_capability_disappears_before_source_host_admission()
    {
        using var transport = await PreviewContextTransport.CreateAsync();
        transport.BeforeRequest = request =>
        {
            if (request.RequestUri!.Host == new Uri(transport.ContextEndpoint).Host)
                transport.Source.Values.Remove(ProductConfigurationKeys.FactoryPreview);
        };
        var response = await transport.RunAsync(coordinator: true);
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Equal(FactoryCharterState.Unknown, JsonSerializer.Deserialize<PreviewContextFailure>(response.Body, FactorySourceValidation.RecordJson)!.State);
    }

    [Fact]
    public async Task Operator_approval_cannot_substitute_for_original_native_creation_provenance()
    {
        using var transport = await PreviewContextTransport.CreateAsync();
        var key = ProductConfigurationKeys.PreviewBinding(transport.Request.OperationId, "account");
        var binding = JsonSerializer.Deserialize<FoundationOperation>(transport.Source.Values[key], FactorySourceValidation.RecordJson)!;
        transport.ReplaceBinding("account", binding with { ProviderRequestId = null });
        var result = await transport.RunAsync();
        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
    }

    [Fact]
    public async Task Current_product_governance_must_still_require_Entra_not_access_keys()
    {
        using var transport = await PreviewContextTransport.CreateAsync();
        transport.Native[transport.Models.Grants![0].Scope]["properties"]!["disableLocalAuth"] = false;
        Assert.Equal(HttpStatusCode.Forbidden, (await transport.RunAsync(coordinator: true)).Status);
    }

    [Fact]
    public async Task Independent_serialized_workers_reconstruct_current_context_not_cached_authorization()
    {
        using var first = await PreviewContextTransport.CreateAsync();
        var initial = await first.RunAsync(coordinator: true);
        Assert.True(initial.Status == HttpStatusCode.OK, initial.Body);
        using var second = await PreviewContextTransport.ReconstructAsync(first);
        var reconstructed = await second.RunAsync(coordinator: true);
        Assert.True(reconstructed.Status == HttpStatusCode.OK, reconstructed.Body);
        var before = JsonSerializer.Deserialize<PreviewSourceContext>(initial.Body, FactorySourceValidation.RecordJson)!;
        var after = JsonSerializer.Deserialize<PreviewSourceContext>(reconstructed.Body, FactorySourceValidation.RecordJson)!;
        Assert.Equal(before.CapabilityRevision, after.CapabilityRevision);
        Assert.Equal(before.Context.Adoption!.Adoption, after.Context.Adoption!.Adoption);
        Assert.Equal(before.Context.Current!.Source, after.Context.Current!.Source);
        Assert.Equal(before.Context.Current.Analysis.Progress!.ChainSha256, after.Context.Current.Analysis.Progress!.ChainSha256);
        second.Source.Values.Remove(ProductConfigurationKeys.FactoryPreview);
        Assert.Equal(HttpStatusCode.Forbidden, (await second.RunAsync(coordinator: true)).Status);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("capability")]
    [InlineData("erased-context")]
    [InlineData("oversized")]
    public async Task Council_rejects_unbound_missing_or_over_budget_source_responses(string corruption)
    {
        using var transport = await PreviewContextTransport.CreateAsync();
        transport.ChangeContextResponse = text =>
        {
            var response = JsonNode.Parse(text)!;
            if (corruption == "request") response["requestId"] = Guid.NewGuid();
            if (corruption == "capability") response["capabilityRevision"] = new string('0', 64);
            if (corruption == "erased-context") response.AsObject().Remove("context");
            if (corruption == "oversized") response["context"]!["detail"] = new string('a', FactoryPreviewValidation.ResponseBytes);
            return response.ToJsonString();
        };
        var result = await transport.RunAsync(coordinator: true);
        Assert.Equal(corruption == "oversized" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden, result.Status);
        Assert.DoesNotContain("coordinate volunteer shifts", result.Body);
    }

    [Fact]
    public async Task Source_read_deadline_remains_explicit_partial_through_council_host()
    {
        using var transport = await PreviewContextTransport.CreateAsync();
        var advanced = false;
        transport.Source.BeforeRequestAsync = (request, token) =>
        {
            if (!advanced && request.RequestUri!.Host == new Uri(transport.Source.ProgressEndpoint).Host)
            {
                advanced = true;
                transport.Source.Clock.Now += FactoryIntentLimits.RequestBudget + TimeSpan.FromSeconds(1);
                token.ThrowIfCancellationRequested();
            }
            return Task.CompletedTask;
        };
        var result = await transport.RunAsync(coordinator: true);
        Assert.Equal(HttpStatusCode.Conflict, result.Status);
        Assert.Equal(FactoryCharterState.Partial, JsonSerializer.Deserialize<PreviewSourceContext>(result.Body, FactorySourceValidation.RecordJson)!.Context.State);
        Assert.DoesNotContain("coordinate volunteer shifts", result.Body);
    }

    [Theory]
    [InlineData("/charter-review")]
    [InlineData("/materialize")]
    public async Task Existing_source_routes_do_not_inherit_new_council_permission(string route)
    {
        using var transport = await PreviewContextTransport.CreateAsync();
        using var http = new HttpClient(transport, false);
        var runner = new PreviewContextRunner(transport.Source)
        { Route = route, Caller = transport.Models.Bindings![0].PrincipalId!, Client = transport.Models.Bindings[0].ClientId!, Request = new { } };
        Assert.Equal(0, await RuntimeCliApplication.InvokeAsync(["serve-source"], transport.Source.Environment, new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with
            {
                WebHostRunner = runner,
                SourceHttp = http,
                SourceCredential = new SourceTestCredential(transport.Source),
                SourceClock = transport.Source.Clock
            }, CancellationToken.None));
        Assert.Equal(route == "/charter-review" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden, runner.Status);
        Assert.DoesNotContain("coordinate volunteer shifts", runner.Body);
    }

    [Fact]
    public async Task Source_context_host_requires_protected_preview_capability_before_releasing_context()
    {
        using var transport = await IntentWorkflowTransport.CreateAsync();
        using var http = new HttpClient(transport, false);
        var runner = new PreviewContextRunner(transport.Source);
        var exit = await RuntimeCliApplication.InvokeAsync(["serve-source-context"], transport.Source.Environment,
            new StringWriter(), new StringWriter(), RuntimeDependencies.Production() with
            {
                WebHostRunner = runner,
                SourceHttp = http,
                SourceCredential = new SourceTestCredential(transport.Source),
                SourceClock = transport.Source.Clock,
            }, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Equal(HttpStatusCode.Forbidden, runner.Status);
        Assert.Equal(FactoryCharterState.Unknown, JsonSerializer.Deserialize<PreviewContextFailure>(runner.Body, FactorySourceValidation.RecordJson)!.State);
        Assert.DoesNotContain("Coordinate volunteer", runner.Body);
        Assert.Empty(transport.Source.Writes);
    }
}

internal sealed class PreviewContextRunner(SourceInventoryTransport source) : IWebHostRunner
{
    internal string Caller = source.Manifest.Identity.AzureActorId;
    internal string Client = source.GitHub.CodeClientId!;
    internal object Request = new { operationId = Guid.NewGuid(), approvalFingerprint = new string('a', 64), requestId = Guid.NewGuid() };
    internal string Route = "/council-context";
    internal HttpStatusCode Status;
    internal string Body = "";

    public async Task RunAsync(WebApplication app, CancellationToken token)
    {
        app.Urls.Clear();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync(token);
        try
        {
            using var http = new HttpClient { BaseAddress = new(app.Urls.Single()), Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-IDP", "aad");
            http.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL-ID", Caller);
            http.DefaultRequestHeaders.Add("X-MS-CLIENT-PRINCIPAL", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
            {
                auth_typ = "aad",
                claims = new[] { new { typ = "oid", val = Caller }, new { typ = "appid", val = Client },
                    new { typ = "tid", val = source.Manifest.Identity.TenantId } },
            })));
            var route = Route;
            while (true)
            {
                using var response = await http.PostAsJsonAsync(route, Request, FactoryGitHubJson.Options, token);
                Status = response.StatusCode;
                Body = await response.Content.ReadAsStringAsync(token);
                if (Status is not (HttpStatusCode.OK or HttpStatusCode.Accepted)) break;
                using var body = JsonDocument.Parse(Body);
                if (!body.RootElement.TryGetProperty("protocol", out var protocol) || protocol.GetInt32() != 2) break;
                var observation = body.RootElement.Deserialize<PreviewHttpObservation>(FactorySourceValidation.RecordJson)!;
                if (observation.Readiness is PreviewReadiness.Queued or PreviewReadiness.Running or PreviewReadiness.Checking)
                {
                    route = route == "/evaluate" ? "/evaluation" : route;
                    await Task.Delay(10, token);
                    continue;
                }
                if (observation.Evaluation is { } evaluation) Body = evaluation.GetRawText();
                else
                {
                    var state = observation.Readiness switch
                    {
                        PreviewReadiness.Blocked => FactoryCharterState.Blocked,
                        PreviewReadiness.Partial => FactoryCharterState.Partial,
                        PreviewReadiness.Failed => FactoryCharterState.Failed,
                        _ => FactoryCharterState.Unknown
                    };
                    Status = HttpStatusCode.Forbidden;
                    Body = JsonSerializer.Serialize(new PreviewContextFailure(state, observation.Reason ?? "PreviewNotReady"), FactoryGitHubJson.Options);
                }
                break;
            }
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }
}
