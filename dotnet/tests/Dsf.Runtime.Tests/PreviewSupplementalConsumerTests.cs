using System.Net;
using System.Text.Json.Nodes;
using System.Text.Json;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class PreviewSupplementalConsumerTests
{
    [Fact]
    public async Task Malformed_native_vault_timestamp_is_Unknown_on_the_source_host_itself()
    {
        using var transport = await PreviewContextTransport.CreateAsync(supplementals: ["webiq"]);
        transport.Native[transport.SupplementalHosts[0].MetadataScope]["systemData"]!["createdAt"] = "invalid";
        var result = await transport.RunSupplementalsAsync(kind: "webiq");
        Assert.Equal(HttpStatusCode.Conflict, result.Status);
        Assert.Contains("Unknown", result.Body);
        Assert.Empty(transport.SupplementalQueries);
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("null-grant")]
    [InlineData("null-effect")]
    [InlineData("request")]
    [InlineData("query")]
    public async Task Source_authority_callback_must_be_complete_success_and_request_bound_before_any_query(string change)
    {
        using var transport = await PreviewContextTransport.CreateAsync(supplementals: ["webiq"]);
        if (change == "conflict") transport.AuthorityStatus = HttpStatusCode.Conflict;
        else transport.ChangeAuthorityResponse = body =>
        {
            switch (change)
            {
                case "null-grant": body["grants"]![0] = null; break;
                case "null-effect": body["effects"]![0] = null; break;
                case "request": body["request"]!["preview"]!["requestId"] = Guid.NewGuid().ToString(); break;
                case "query": body["workload"]!["source"]!["query"] = "Unapproved changed query"; break;
            }
            return body;
        };
        var result = await transport.RunSupplementalsAsync();
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.Contains("Unknown", result.Body);
        Assert.Empty(transport.SupplementalQueries);
    }

    [Theory]
    [InlineData("inherited-grant")]
    [InlineData("condition")]
    [InlineData("secret-version")]
    [InlineData("secret-disabled")]
    [InlineData("oversized-native")]
    [InlineData("unknown")]
    public async Task Source_native_authority_and_bounded_payload_failures_remain_explicit(string change)
    {
        using var transport = await PreviewContextTransport.CreateAsync(supplementals: ["webiq"]);
        var host = transport.SupplementalHosts[0];
        var grant = FactorySupplementalValidation.Grants(transport.Supplemental!.Configuration!, transport.Supplemental.Configuration!.Selection.Sources[0])[0];
        if (change == "inherited-grant")
        {
            var id = transport.Group + "/providers/microsoft.authorization/roleassignments/" + Guid.NewGuid();
            transport.Native[id] = JsonSerializer.SerializeToNode(new { id, properties = new { scope = transport.Group, principalId = host.PrincipalId,
                principalType = "ServicePrincipal", roleDefinitionId = host.MetadataRoleDefinitionId } })!;
        }
        if (change == "condition") transport.Native[grant.Target]["properties"]!["condition"] = "unapproved condition";
        transport.SupplementalNativeResponse = request =>
        {
            if (request.RequestUri!.Host == "api.microsoft.ai")
            {
                if (change == "unknown") throw new OperationCanceledException();
                if (change == "oversized-native") return new(HttpStatusCode.OK) { Content = new PreviewTelemetryConsumerTests.CountingTelemetryContent() };
            }
            if (request.RequestUri.Host.EndsWith(".vault.azure.net", StringComparison.Ordinal) && change is "secret-version" or "secret-disabled")
                return SourceInventoryTransport.Json(new { id = change == "secret-version" ? request.RequestUri.GetLeftPart(UriPartial.Path) + "-foreign"
                    : request.RequestUri.GetLeftPart(UriPartial.Path), value = "secret-key-do-not-print", attributes = new {
                        enabled = change != "secret-disabled", exp = transport.Source.Clock.Now.AddDays(1).ToUnixTimeSeconds(),
                        created = transport.Source.Clock.Now.AddDays(-1).ToUnixTimeSeconds(), updated = transport.Source.Clock.Now.AddDays(-1).ToUnixTimeSeconds() } });
            return null;
        };
        var result = await transport.RunSupplementalsAsync();
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.DoesNotContain("secret-key-do-not-print", result.Body);
        if (change == "unknown") Assert.Contains("Unknown", result.Body);
        if (change == "oversized-native") Assert.Contains("CapacityExceeded", result.Body);
        if (change is "inherited-grant" or "condition") Assert.Empty(transport.SupplementalQueries);
    }

    [Fact]
    public async Task Evidence_that_expires_during_final_current_checks_is_withheld()
    {
        using var transport = await PreviewContextTransport.CreateAsync(supplementals: ["webiq"]);
        transport.WebPayload = """{"webResults":[{"url":"https://provider.example","content":"Public checkout keyboard guidance."}]}""";
        var returned = false;
        var advanced = false;
        transport.ChangeSupplementalResponse = body => { returned = true; return body; };
        transport.BeforeRequest = request =>
        {
            if (returned && !advanced && request.RequestUri!.Host == "management.azure.com")
            {
                advanced = true;
                transport.Source.Clock.Now = transport.Source.Clock.Now.AddSeconds(121);
            }
        };
        var result = await transport.RunSupplementalsAsync();
        Assert.True(advanced);
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.DoesNotContain("Public checkout keyboard guidance.", result.Body);
    }

    [Theory]
    [InlineData("orphan-effect")]
    [InlineData("orphan-proof")]
    [InlineData("orphan-preview-selection")]
    [InlineData("labeled-history")]
    [InlineData("labeled-preview-selection")]
    [InlineData("unreadable-namespace")]
    public async Task Absent_selection_is_not_pristine_when_history_survives_or_cannot_be_read(string scenario)
    {
        using var transport = await PreviewContextTransport.CreateAsync();
        if (scenario == "orphan-effect") transport.Source.Values[ProductConfigurationKeys.SupplementalOperation(Guid.NewGuid(), 0)] = "{}";
        if (scenario == "orphan-proof") transport.Source.Values[ProductConfigurationKeys.SupplementalProof(Guid.NewGuid(), Guid.NewGuid(), "webiq")] = "{}";
        if (scenario == "orphan-preview-selection") transport.Source.Values[FactoryPreviewSupplementalValidation.ConfigurationKey(Guid.NewGuid())] = "{}";
        if (scenario == "labeled-history") transport.SupplementalNamespaceResponse = _ => SourceInventoryTransport.Json(new { items = new[]
        { new { key = ProductConfigurationKeys.FactorySupplemental, value = "{}", etag = "1", label = "retained", content_type = "application/json", locked = false } } });
        if (scenario == "labeled-preview-selection") transport.SupplementalNamespaceResponse = filter =>
            filter.Contains(FactoryPreviewSupplementalValidation.ConfigurationPrefix, StringComparison.Ordinal)
                ? SourceInventoryTransport.Json(new { items = new[] { new { key = FactoryPreviewSupplementalValidation.ConfigurationKey(Guid.NewGuid()),
                    value = "{}", etag = "1", label = "retained", content_type = FactoryPreviewValidation.RecordContentType, locked = false } } }) : null;
        if (scenario == "unreadable-namespace") transport.SupplementalNamespaceResponse = _ => new(HttpStatusCode.Forbidden);
        var result = await transport.RunSupplementalsAsync();
        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.Contains("Unknown", result.Body);
        Assert.DoesNotContain("NotSelected", result.Body);
        Assert.DoesNotContain(transport.Requests, r => r.Method != "GET");
    }

    [Theory]
    [InlineData("""{}""", 200, "MalformedResponse")]
    [InlineData("""{"webResults":null}""", 200, "MalformedResponse")]
    [InlineData("""{"webResults":[],"partial":true}""", 200, "Incomplete")]
    [InlineData("""{"webResults":[]}""", 206, "Incomplete")]
    [InlineData("""{"webResults":[]}""", 403, "PermissionDenied")]
    [InlineData("""{"webResults":[{"url":"https://provider.example","content":"secret-key-do-not-print"}]}""", 200, "MalformedResponse")]
    [InlineData("""{"webResults":[],"nextLink":"https://provider.example/private-provider-detail"}""", 200, "Incomplete")]
    public async Task Selected_failure_is_not_empty_and_does_not_remove_another_selected_source(string body, int status, string reason)
    {
        using var transport = await PreviewContextTransport.CreateAsync(supplementals: ["webiq", "foundryiq"]);
        transport.SupplementalNativeResponse = request => request.RequestUri!.Host == "api.microsoft.ai"
            ? FactorySupplementalHostTests.Json((HttpStatusCode)status, body) : null;
        var result = await transport.RunSupplementalsAsync();
        Assert.True(result.Status == HttpStatusCode.Conflict, result.Body);
        var evidence = Read(result.Body);
        Assert.Equal(SupplementalState.Blocked, evidence.State);
        Assert.Equal(2, evidence.Sources.Count);
        Assert.Equal(reason, evidence.Sources[0].Evidence.Proof.Reason.ToString());
        Assert.Equal(SupplementalReason.Empty, evidence.Sources[1].Evidence.Proof.Reason);
        Assert.All(evidence.Sources, s => Assert.Empty(s.Evidence.Items));
        Assert.Equal(new[] { "webiq", "foundryiq" }, transport.SupplementalQueries.Select(q => q.Kind));
        Assert.DoesNotContain("secret-key-do-not-print", result.Body);
        Assert.DoesNotContain("private-provider-detail", result.Body);
    }

    [Theory]
    [InlineData("other-index", "AuthorityChanged")]
    [InlineData("other-kb", "AuthorityChanged")]
    [InlineData("web-source", "Unsupported")]
    [InlineData("unsupported-activity", "Unsupported")]
    [InlineData("partial-metadata", "Incomplete")]
    [InlineData("denied-data", "PermissionDenied")]
    public async Task Foundry_consumption_reuses_current_typed_knowledge_metadata_and_data_checks(string scenario, string reason)
    {
        using var transport = await PreviewContextTransport.CreateAsync(supplementals: ["foundryiq"]);
        transport.FoundryScenario = scenario;
        var result = await transport.RunSupplementalsAsync();
        Assert.True(result.Status == HttpStatusCode.Conflict, result.Body);
        var evidence = Read(result.Body);
        Assert.Equal(reason, evidence.Sources.Single().Evidence.Proof.Reason.ToString());
        Assert.Empty(evidence.Sources[0].Evidence.Items);
        Assert.DoesNotContain("secret-provider-detail", result.Body);
    }

    [Theory]
    [InlineData("missing-preview-config")]
    [InlineData("changed-preview-config")]
    [InlineData("unapproved")]
    [InlineData("root-missing")]
    [InlineData("proof-missing")]
    [InlineData("proof-corrupt")]
    [InlineData("old-host-image")]
    [InlineData("new-host-identity")]
    [InlineData("role-widened")]
    [InlineData("grant-revoked")]
    [InlineData("native-source-identity")]
    [InlineData("native-source-client")]
    [InlineData("native-source-tenant")]
    [InlineData("native-source-resource")]
    public async Task Current_selected_configuration_receipts_hosts_and_permissions_are_mandatory(string change)
    {
        using var transport = await PreviewContextTransport.CreateAsync(supplementals: ["webiq"]);
        var selected = transport.SupplementalHosts[0];
        var configKey = FactoryPreviewSupplementalValidation.ConfigurationKey(transport.Request.OperationId);
        var oldHost = FactorySupplementalValidation.HostTarget(transport.Supplemental!.Configuration!, "webiq");
        var newHost = FactoryPreviewValidation.HostTarget(transport.Group, transport.Source.Manifest.Identity.FactoryId, PreviewHostKind.WebEvidence);
        switch (change)
        {
            case "missing-preview-config": transport.Source.Values.Remove(configKey); break;
            case "changed-preview-config":
                var value = JsonNode.Parse(transport.Source.Values[configKey])!;
                value["hosts"]![0]!["audienceClientId"] = "19191919-1919-1919-1919-191919191919";
                transport.Source.Values[configKey] = value.ToJsonString(); break;
            case "unapproved":
                var root = JsonNode.Parse(transport.Source.Values[ProductConfigurationKeys.FactoryPreview])!;
                root["state"] = "Partial";
                transport.Source.Values[ProductConfigurationKeys.FactoryPreview] = root.ToJsonString(); break;
            case "root-missing": transport.Source.Values.Remove(ProductConfigurationKeys.FactorySupplemental); break;
            case "proof-missing": transport.Source.Values.Remove(transport.Supplemental.Checks[0].ProofKey); break;
            case "proof-corrupt": transport.Source.Values[transport.Supplemental.Checks[0].ProofKey] = "{}"; break;
            case "old-host-image": transport.Native[oldHost]["properties"]!["template"]!["containers"]![0]!["image"] = "foreign"; break;
            case "new-host-identity": transport.Native[newHost]["identity"]!["userAssignedIdentities"] = new JsonObject { [transport.Source.Code.Target] = new JsonObject() }; break;
            case "role-widened": transport.Native[selected.MetadataRoleDefinitionId]["properties"]!["permissions"]![0]!["actions"] = new JsonArray("*"); break;
            case "grant-revoked":
                var grant = FactorySupplementalValidation.Grants(transport.Supplemental.Configuration!, transport.Supplemental.Configuration!.Selection.Sources[0])[0];
                transport.SupplementalNativeResponse = request => request.RequestUri!.AbsolutePath == grant.Target ? new(HttpStatusCode.Forbidden) : null; break;
            case "native-source-identity": transport.SupplementalTokenPrincipal = transport.Source.Code.PrincipalId; break;
            case "native-source-client": transport.SupplementalTokenClient = transport.Source.Code.ClientId; break;
            case "native-source-tenant": transport.SupplementalTokenTenant = "19191919-1919-1919-1919-191919191919"; break;
            case "native-source-resource": transport.SupplementalTokenIdentity = transport.Source.Code.Target; break;
        }
        var result = await transport.RunSupplementalsAsync();
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.Empty(transport.SupplementalQueries);
        Assert.DoesNotContain("\"sources\"", result.Body);
    }

    [Theory]
    [InlineData("proof-request")]
    [InlineData("authority-request")]
    [InlineData("proof-revision")]
    [InlineData("native-references")]
    [InlineData("content-hash")]
    [InlineData("null-items")]
    [InlineData("oversized")]
    public async Task Unbound_or_oversized_source_results_do_not_escape_as_captured_evidence(string change)
    {
        using var transport = await PreviewContextTransport.CreateAsync(supplementals: ["webiq"]);
        transport.WebPayload = """{"webResults":[{"url":"https://provider.example","content":"Public checkout keyboard guidance."}]}""";
        transport.ChangeSupplementalResponse = text =>
        {
            if (change == "oversized") return new string(' ', 256 * 1024) + text;
            var body = JsonNode.Parse(text)!;
            switch (change)
            {
                case "proof-request": body["evidence"]!["proof"]!["checkId"] = Guid.NewGuid().ToString(); break;
                case "authority-request": body["authority"]!["request"]!["preview"]!["requestId"] = Guid.NewGuid().ToString(); break;
                case "proof-revision": body["evidence"]!["proof"]!["configurationRevision"] = new string('f', 64); break;
                case "native-references": body["nativeRevisions"] = new JsonObject(); break;
                case "content-hash": body["evidence"]!["items"]![0]!["content"] = "Unbound changed content"; break;
                case "null-items": body["evidence"]!["items"]![0] = null; break;
            }
            return body.ToJsonString();
        };
        var result = await transport.RunSupplementalsAsync();
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.DoesNotContain("Public checkout keyboard guidance.", result.Body);
        Assert.Contains(change == "oversized" ? "Partial" : "Unknown", result.Body);
        Assert.Single(transport.SupplementalQueries);
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("source")]
    [InlineData("model")]
    [InlineData("adoption")]
    [InlineData("grant")]
    [InlineData("secret")]
    public async Task Authority_revocation_during_native_collection_withholds_previously_captured_content(string change)
    {
        using var transport = await PreviewContextTransport.CreateAsync(supplementals: ["webiq"]);
        transport.WebPayload = """{"webResults":[{"url":"https://provider.example","content":"Public checkout keyboard guidance."}]}""";
        transport.OnSupplementalQuery = _ =>
        {
            switch (change)
            {
                case "capability": transport.Source.Values.Remove(ProductConfigurationKeys.FactoryPreview); break;
                case "source": transport.Source.Values.Remove(ProductConfigurationKeys.FactorySource); break;
                case "model": transport.Source.Values.Remove(ProductConfigurationKeys.FactoryModels); break;
                case "adoption": transport.Source.Values.Remove(ProductConfigurationKeys.CharterAdopted); break;
                case "grant":
                    var host = transport.SupplementalHosts[0];
                    var grant = FactoryModelsValidation.Grant(host.MetadataScope, host.MetadataRoleDefinitionId, host.PrincipalId, "");
                    transport.Native[grant.Target]["properties"]!["principalId"] = transport.Source.Code.PrincipalId; break;
                case "secret": transport.SupplementalNativeResponse = request => request.RequestUri!.Host.EndsWith(".vault.azure.net", StringComparison.Ordinal)
                    ? new(HttpStatusCode.Forbidden) : null; break;
            }
        };
        var result = await transport.RunSupplementalsAsync();
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.DoesNotContain("Public checkout keyboard guidance.", result.Body);
        Assert.Single(transport.SupplementalQueries);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("hosts")]
    [InlineData("items")]
    public async Task Null_selected_records_or_response_elements_remain_explicit_Unknown(string field)
    {
        using var transport = await PreviewContextTransport.CreateAsync(supplementals: ["webiq"]);
        if (field == "selection")
        {
            var root = JsonNode.Parse(transport.Source.Values[ProductConfigurationKeys.FactorySupplemental])!;
            root["configuration"]!["selection"] = null;
            transport.Source.Values[ProductConfigurationKeys.FactorySupplemental] = root.ToJsonString();
        }
        else if (field == "hosts")
        {
            var selected = transport.SupplementalConfiguration! with { Hosts = [null!] };
            transport.RepinSupplementalConfiguration(selected);
        }
        else transport.ChangeSupplementalResponse = text =>
        {
            var body = JsonNode.Parse(text)!;
            body["evidence"]!["items"] = new JsonArray((JsonNode?)null);
            return body.ToJsonString();
        };
        var result = await transport.RunSupplementalsAsync();
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.Contains("Unknown", result.Body);
        Assert.DoesNotContain("\"sources\"", result.Body);
    }

    [Theory]
    [InlineData("source", "operator")]
    [InlineData("source", "code")]
    [InlineData("source", "foreign")]
    [InlineData("source", "wrong-client")]
    [InlineData("authority", "operator")]
    [InlineData("authority", "council")]
    [InlineData("authority", "other-source")]
    [InlineData("authority", "wrong-client")]
    [InlineData("operator", "source")]
    public async Task Only_exact_purpose_bound_callers_can_consume_or_read_own_source_authority(string route, string caller)
    {
        using var transport = await PreviewContextTransport.CreateAsync(supplementals: ["foundryiq", "webiq"]);
        var web = transport.SupplementalHosts.Single(h => h.Kind == "webiq");
        var principal = caller switch
        {
            "operator" => transport.Source.Manifest.Identity.AzureActorId,
            "code" => transport.Source.Code.PrincipalId!,
            "council" => transport.Models.Bindings![0].PrincipalId!,
            "source" => web.PrincipalId,
            "other-source" => transport.SupplementalHosts[0].PrincipalId,
            "wrong-client" => route == "source" ? transport.Models.Bindings![0].PrincipalId! : web.PrincipalId,
            _ => "19191919-1919-1919-1919-191919191919",
        };
        var result = await transport.RunSupplementalsAsync(kind: route == "source" ? "webiq" : null, caller: principal,
            client: caller == "wrong-client" ? "18181818-1818-1818-1818-181818181818" : route == "authority" ? web.ClientId : null,
            request: route == "authority" ? new PreviewSupplementalRequest(transport.Request, "webiq") : null,
            route: route == "authority" ? "/supplemental-authority" : null);
        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.Contains("CallerRequired", result.Body);
        Assert.Empty(transport.SupplementalQueries);
        Assert.DoesNotContain("\"workload\"", result.Body);
    }

    [Fact]
    public async Task Both_selected_consumers_preserve_genuine_content_and_empty_results_in_independent_serialized_workers()
    {
        using var first = await PreviewContextTransport.CreateAsync(modelEvaluation: true, telemetryEvidence: true, supplementals: ["foundryiq", "webiq"]);
        first.FoundryScenario = "evidence";
        var before = await first.RunSupplementalsAsync();
        Assert.True(before.Status == HttpStatusCode.OK, before.Body);
        using var worker = await PreviewContextTransport.ReconstructAsync(first);
        worker.EnforceCouncilScopes = true;
        worker.FoundryScenario = "evidence";
        worker.WebPayload = """{"webResults":[{"url":"https://provider.example/public?access_token=private-provider-detail","content":"Public checkout keyboard navigation guidance."}]}""";
        var after = await worker.RunSupplementalsAsync();
        Assert.True(after.Status == HttpStatusCode.OK, after.Body);
        var old = Read(before.Body);
        var current = Read(after.Body);
        Assert.Equal(old.GovernanceRevision, current.GovernanceRevision);
        Assert.NotEqual(old.RequestId, current.RequestId);
        Assert.Equal(new[] { "foundryiq", "webiq" }, current.Sources.Select(s => s.Evidence.Proof.Kind));
        Assert.Empty(old.Sources[1].Evidence.Items);
        Assert.Contains("Public keyboard accessibility.", Assert.Single(current.Sources[0].Evidence.Items).Content);
        Assert.Equal("Public checkout keyboard navigation guidance.", Assert.Single(current.Sources[1].Evidence.Items).Content);
        Assert.Matches("^webiq:sha256:[0-9a-f]{64}$", current.Sources[1].Evidence.Items[0].Reference);
        Assert.DoesNotContain("private-provider-detail", after.Body);
        Assert.DoesNotContain("provider-document-id", after.Body);
        Assert.Equal(2, worker.SupplementalQueries.Count);
        Assert.Empty(worker.TelemetryQueries);
        Assert.DoesNotContain(worker.Requests, r => r.Uri.Host == "provider.example");
        worker.Requests.Clear();
        Assert.True((await worker.RunAsync(coordinator: true)).Status == HttpStatusCode.OK);
        worker.Requests.Clear();
        Assert.True((await worker.RunTelemetryAsync(coordinator: true)).Status == HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("providerOperationId")]
    [InlineData("deploymentTarget")]
    public async Task Original_receipts_cannot_omit_native_operation_provenance_even_with_matching_protected_hashes(string field)
    {
        using var transport = await PreviewContextTransport.CreateAsync(supplementals: ["webiq"]);
        var key = ProductConfigurationKeys.SupplementalOperation(transport.Supplemental!.OperationId, 2);
        var receipt = JsonNode.Parse(transport.Source.Values[key])!;
        receipt[field] = null;
        transport.Source.Values[key] = receipt.ToJsonString();
        transport.RepinSupplementalEffects();
        var result = await transport.RunSupplementalsAsync();
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.Contains("Unknown", result.Body);
        Assert.Empty(transport.SupplementalQueries);
    }

    [Theory]
    [InlineData("webiq")]
    [InlineData("foundryiq")]
    public async Task Selected_native_source_returns_current_empty_evidence_through_its_own_identity(string kind)
    {
        using var transport = await PreviewContextTransport.CreateAsync(supplementals: [kind]);
        var result = await transport.RunSupplementalsAsync();
        Assert.True(result.Status == HttpStatusCode.OK, result.Body);
        var evidence = JsonSerializer.Deserialize<PreviewSupplementalEvidence>(result.Body, FactorySourceValidation.RecordJson)!;
        Assert.Equal(SupplementalState.ReadySupplemental, evidence.State);
        var source = Assert.Single(evidence.Sources);
        Assert.Equal(kind, source.Evidence.Proof.Kind);
        Assert.Equal(SupplementalReason.Empty, source.Evidence.Proof.Reason);
        Assert.Empty(source.Evidence.Items);
        Assert.Equal(transport.SupplementalHosts.Single().PrincipalId, source.Evidence.Proof.SourcePrincipalId);
        Assert.DoesNotContain(transport.Requests, r => r.Uri.Host.EndsWith(".azconfig.io", StringComparison.Ordinal)
            && r.Principal == source.Evidence.Proof.SourcePrincipalId);
    }

    [Fact]
    public async Task Pristine_AzureOnly_does_not_require_optional_hosts_credentials_or_upstream_calls()
    {
        using var transport = await PreviewContextTransport.CreateAsync();
        using var http = new HttpClient(transport, false);
        var runner = new PreviewContextRunner(transport.Source) { Route = "/supplemental-evidence", Request = transport.Request };
        var environment = transport.Source.Environment;
        environment["AZURE_CLIENT_ID"] = transport.Models.Bindings![0].ClientId;
        Assert.Equal(0, await RuntimeCliApplication.InvokeAsync(["serve-preview"], environment, new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with { WebHostRunner = runner, PreviewHttp = http,
                PreviewCredential = new IntentModelCredential(transport.Source), PreviewClock = transport.Source.Clock }, CancellationToken.None));
        Assert.True(runner.Status == HttpStatusCode.OK, runner.Body);
        var body = JsonNode.Parse(runner.Body)!;
        Assert.Equal("NotSelected", (string?)body["state"]);
        Assert.Empty(body["sources"]!.AsArray());
        Assert.DoesNotContain(transport.Requests, request => request.Method != "GET");
    }

    private static PreviewSupplementalEvidence Read(string body) =>
        JsonSerializer.Deserialize<PreviewSupplementalEvidence>(body, FactorySourceValidation.RecordJson)!;
}
