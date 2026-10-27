using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class PreviewTelemetryConsumerTests
{
    [Fact]
    public async Task Operator_gets_genuine_projected_application_content_through_council_and_telemetry_identity()
    {
        using var transport = await PreviewContextTransport.CreateAsync(telemetryEvidence: true);
        var response = await transport.RunTelemetryAsync(coordinator: true);
        Assert.True(response.Status == HttpStatusCode.OK, response.Body);
        var evidence = JsonSerializer.Deserialize<PreviewTelemetryEvidence>(response.Body, FactorySourceValidation.RecordJson)!;
        var row = Assert.Single(evidence.Capture.Rows);
        Assert.Equal("POST /shifts/confirm", row.Fields["Name"].GetString());
        Assert.False(row.Fields["Success"].GetBoolean());
        Assert.Equal("409", row.Fields["ResultCode"].GetString());
        Assert.Equal(42.5, row.Fields["DurationMs"].GetDouble());
        Assert.Equal("application-record-1", row.Reference.ItemId);
        Assert.Equal(TelemetryState.Ready, evidence.Capture.Proof.State);
        Assert.Contains("NOT exhaustive", evidence.Capture.Proof.CoverageLimitations);
        Assert.Equal(transport.Request.RequestId, evidence.Capture.Proof.CheckId);
        Assert.Single(transport.TelemetryQueries);
        Assert.Equal("AppRequests | top 2 by TimeGenerated desc | project TimeGenerated, _ResourceId, _ItemId, TenantId, Type, Name, Success, ResultCode, DurationMs",
            transport.TelemetryQueries[0].GetProperty("query").GetString());
        Assert.All(transport.Requests.Where(r => r.Uri.Host == "api.loganalytics.io"),
            r => Assert.Equal(transport.Telemetry!.Configuration.SourceIdentity.PrincipalId, r.Principal));
    }

    [Theory]
    [InlineData("empty", "Empty")]
    [InlineData("stale", "Stale")]
    [InlineData("foreign-app", "WrongScope")]
    [InlineData("foreign-workspace", "WrongScope")]
    [InlineData("foreign-table", "WrongScope")]
    [InlineData("old-time", "WrongScope")]
    [InlineData("future-time", "WrongScope")]
    [InlineData("missing-content", "MalformedResponse")]
    [InlineData("bad-content-schema", "MalformedResponse")]
    [InlineData("ragged", "MalformedResponse")]
    [InlineData("sensitive", "SensitiveContent")]
    [InlineData("protected", "Unsupported")]
    public async Task Native_unusable_or_unreviewed_content_has_no_usable_evidence(string scenario, string reason)
    {
        using var transport = await PreviewContextTransport.CreateAsync(telemetryEvidence: true);
        if (scenario == "protected")
            transport.Native[transport.Telemetry!.Configuration.Backends[0].ResourceId + "/tables/AppRequests"]["properties"]!["protectionLevel"] = "Restricted";
        transport.ChangeTelemetryQueryResponse = body =>
        {
            var table = body["tables"]![0]!;
            var row = table["rows"]![0]!;
            switch (scenario)
            {
                case "empty": table["rows"] = new JsonArray(); break;
                case "stale":
                    if (table["columns"]!.AsArray().Count > 5) table["rows"] = new JsonArray();
                    else row[0] = transport.Source.Clock.Now.AddSeconds(-transport.Telemetry!.Configuration.FreshnessSeconds - 1).ToString("O");
                    break;
                case "foreign-app": row[1] = FactoryTelemetryHostTests.TelemetryTransport.Component + "-foreign"; break;
                case "foreign-workspace": row[3] = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"; break;
                case "foreign-table": row[4] = "AppTraces"; break;
                case "old-time": row[0] = transport.Source.Clock.Now.AddDays(-2).ToString("O"); break;
                case "future-time": row[0] = transport.Source.Clock.Now.AddSeconds(1).ToString("O"); break;
                case "missing-content": row[5] = null; break;
                case "bad-content-schema": table["columns"]![5]!["name"] = "Properties"; break;
                case "ragged": row.AsArray().RemoveAt(8); break;
                case "sensitive": row[5] = "password=do-not-publish-this"; break;
            }
            return body;
        };
        var response = await transport.RunTelemetryAsync(coordinator: true);
        Assert.True(response.Status == HttpStatusCode.Conflict, response.Body);
        var evidence = JsonSerializer.Deserialize<PreviewTelemetryEvidence>(response.Body, FactorySourceValidation.RecordJson)!;
        Assert.Equal(reason, evidence.Capture.Proof.Reason.ToString());
        Assert.Empty(evidence.Capture.Rows);
        Assert.DoesNotContain("do-not-publish-this", response.Body);
    }

    [Theory]
    [InlineData(false, "operator")]
    [InlineData(false, "code")]
    [InlineData(false, "foreign")]
    [InlineData(false, "wrong-client")]
    [InlineData(true, "council")]
    [InlineData(true, "code")]
    [InlineData(true, "foreign")]
    public async Task Consumption_caller_and_operator_routes_remain_separate(bool coordinator, string caller)
    {
        using var transport = await PreviewContextTransport.CreateAsync(telemetryEvidence: true);
        var principal = caller switch { "operator" => transport.Source.Manifest.Identity.AzureActorId,
            "code" => transport.Source.Code.PrincipalId!, "council" or "wrong-client" => transport.Models.Bindings![0].PrincipalId!,
            _ => "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee" };
        var response = await transport.RunTelemetryAsync(coordinator, principal,
            caller == "wrong-client" ? transport.Source.Code.ClientId : null);
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Contains("CallerRequired", response.Body);
        Assert.Empty(transport.TelemetryQueries);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("tenant")]
    public async Task Native_telemetry_token_must_be_the_original_source_identity(string change)
    {
        using var transport = await PreviewContextTransport.CreateAsync(telemetryEvidence: true);
        if (change == "identity") transport.TelemetryTokenIdentity = transport.Models.Bindings![0].PrincipalId;
        else transport.TelemetryTokenTenant = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";
        var response = await transport.RunTelemetryAsync(coordinator: true);
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Contains("Blocked", response.Body);
        Assert.Empty(transport.TelemetryQueries);
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("projection")]
    [InlineData("configuration")]
    [InlineData("grant")]
    [InlineData("role")]
    [InlineData("mapping")]
    [InlineData("metadata")]
    public async Task Current_authority_drift_during_native_collection_withholds_evidence(string change)
    {
        using var transport = await PreviewContextTransport.CreateAsync(telemetryEvidence: true);
        transport.OnTelemetryQuery = () =>
        {
            var backend = transport.Telemetry!.Configuration.Backends[0];
            switch (change)
            {
                case "capability": transport.CapabilityETag = "new-native-etag"; break;
                case "projection":
                    ChangeRecord(ProductConfigurationKeys.FactoryPreview, r => r["configuration"]!["telemetry"]!["rowsPerTable"] = 3);
                    break;
                case "configuration":
                    ChangeRecord(ProductConfigurationKeys.FactoryTelemetry, r => r["configuration"]!["freshnessSeconds"] = 10);
                    break;
                case "grant":
                    transport.Native[transport.Telemetry.Configuration.AccessGrants.Single(g => g.Scope == backend.ResourceId).Target]["properties"]!["principalId"] = transport.Models.Bindings![0].PrincipalId;
                    break;
                case "role":
                    transport.Native[transport.Telemetry.Configuration.AccessGrants.Single(g => g.Scope == backend.ResourceId).RoleDefinitionId]["properties"]!["permissions"]![0]!["actions"]!.AsArray().Add("*");
                    break;
                case "mapping": transport.Native[backend.ApplicationResourceIds[0]]["properties"]!["WorkspaceResourceId"] = backend.ResourceId + "-foreign"; break;
                case "metadata": transport.Native[backend.ResourceId]["properties"]!["customerId"] = Guid.NewGuid().ToString(); break;
            }
        };
        var result = await transport.RunTelemetryAsync(coordinator: true);
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.DoesNotContain("\"capture\"", result.Body);
        Assert.Single(transport.TelemetryQueries);

        void ChangeRecord(string key, Action<JsonNode> edit)
        {
            var value = JsonNode.Parse(transport.Source.Values[key])!;
            edit(value);
            transport.Source.Values[key] = value.ToJsonString();
        }
    }

    [Theory]
    [InlineData("request")]
    [InlineData("governance")]
    [InlineData("selection")]
    [InlineData("content")]
    [InlineData("missing-row")]
    [InlineData("missing-native")]
    [InlineData("proof-record")]
    [InlineData("null-capture")]
    [InlineData("null-fields")]
    [InlineData("null-native")]
    public async Task Incomplete_or_misbound_source_response_is_not_usable_evidence(string change)
    {
        using var transport = await PreviewContextTransport.CreateAsync(telemetryEvidence: true);
        transport.ChangeTelemetryResponse = text =>
        {
            var body = JsonNode.Parse(text)!;
            switch (change)
            {
                case "request": body["requestId"] = Guid.NewGuid().ToString(); break;
                case "governance": body["governanceRevision"] = new string('0', 64); break;
                case "selection": body["selectionRevision"] = new string('0', 64); break;
                case "content": body["capture"]!["rows"]![0]!["fields"]!["Name"] = "Invented success"; break;
                case "missing-row": body["capture"]!["rows"] = new JsonArray(); break;
                case "missing-native": body["capture"]!["nativeRevisions"]!.AsObject().Remove(body["capture"]!["nativeRevisions"]!.AsObject().First().Key); break;
                case "proof-record": body["capture"]!["proof"]!["records"]![0]!["itemId"] = "different-record"; break;
                case "null-capture": body["capture"] = null; break;
                case "null-fields": body["capture"]!["rows"]![0]!["fields"] = null; break;
                case "null-native": body["capture"]!["nativeRevisions"] = null; break;
            }
            return body.ToJsonString();
        };
        var response = await transport.RunTelemetryAsync(coordinator: true);
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Contains("Unknown", response.Body);
        Assert.DoesNotContain("\"capture\"", response.Body);
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("denied")]
    public async Task Native_errors_and_size_limits_never_become_complete_samples(string failure)
    {
        using var transport = await PreviewContextTransport.CreateAsync(telemetryEvidence: true);
        transport.TelemetryNativeResponse = request => request.RequestUri!.Host != "api.loganalytics.io" ? null : failure switch
        {
            "partial" => SourceInventoryTransport.Json(new { error = new { code = "PartialError", message = "private-backend-details" }, tables = Array.Empty<object>() }),
            "malformed" => new(HttpStatusCode.OK) { Content = new StringContent("{not-native-json") },
            "oversized" => new(HttpStatusCode.OK) { Content = new StringContent("{\"secret\":\"" + new string('x', 131072) + "\"}") },
            _ => new(HttpStatusCode.Forbidden) { Content = new StringContent("""{"error":{"code":"InsufficientAccessError","message":"private-backend-details"}}""") },
        };
        var response = await transport.RunTelemetryAsync(coordinator: true);
        Assert.NotEqual(HttpStatusCode.OK, response.Status);
        Assert.DoesNotContain("private-backend-details", response.Body);
        Assert.DoesNotContain("application-record-1", response.Body);
        Assert.Single(transport.TelemetryQueries);
        if (failure == "oversized") Assert.Contains("Partial", response.Body);
    }

    [Theory]
    [InlineData("unapproved")]
    [InlineData("missing-capability")]
    [InlineData("missing-projection")]
    [InlineData("missing-telemetry")]
    [InlineData("missing-proof")]
    [InlineData("corrupt-proof")]
    [InlineData("unknown-source")]
    [InlineData("missing-grant")]
    [InlineData("changed-host")]
    [InlineData("orphaned-effect")]
    public async Task Missing_or_unconfirmed_current_preparation_cannot_authorize_collection(string change)
    {
        using var transport = await PreviewContextTransport.CreateAsync(telemetryEvidence: true);
        switch (change)
        {
            case "unapproved": Change(ProductConfigurationKeys.FactoryPreview, r => r["state"] = "Partial"); break;
            case "missing-capability": transport.Source.Values.Remove(ProductConfigurationKeys.FactoryPreview); break;
            case "missing-projection": Change(ProductConfigurationKeys.FactoryPreview, r => r["configuration"]!["telemetry"] = null); break;
            case "missing-telemetry": transport.Source.Values.Remove(ProductConfigurationKeys.FactoryTelemetry); break;
            case "missing-proof": transport.Source.Values.Remove(transport.Telemetry!.VerifiedProofReference!); break;
            case "corrupt-proof": transport.Source.Values[transport.Telemetry!.VerifiedProofReference!] = "{}"; break;
            case "unknown-source": Change(ProductConfigurationKeys.FactoryTelemetry, r => r["state"] = "Unknown"); break;
            case "missing-grant":
                transport.TelemetryNativeResponse = request => request.RequestUri!.AbsolutePath == transport.Telemetry!.Configuration.AccessGrants[1].Target
                    ? new(HttpStatusCode.NotFound) : null;
                break;
            case "changed-host":
                var host = FactoryPreviewValidation.HostTarget(transport.Group, transport.Source.Manifest.Identity.FactoryId, PreviewHostKind.TelemetryEvidence);
                transport.Native[host]["identity"]!["userAssignedIdentities"]!.AsObject().Add(transport.Source.Code.Target, new JsonObject());
                break;
            case "orphaned-effect": transport.Source.Values.Remove(ProductConfigurationKeys.TelemetryOperation(transport.Telemetry!.OperationId, 0)); break;
        }
        var response = await transport.RunTelemetryAsync(coordinator: true);
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Empty(transport.TelemetryQueries);
        if (change is "missing-proof" or "corrupt-proof" or "unknown-source") Assert.Contains("Unknown", response.Body);

        void Change(string key, Action<JsonNode> edit)
        {
            var body = JsonNode.Parse(transport.Source.Values[key])!;
            edit(body);
            transport.Source.Values[key] = body.ToJsonString();
        }
    }

    [Theory]
    [InlineData("shared-workspace")]
    [InlineData("protected-workspace")]
    [InlineData("missing-table-protection")]
    [InlineData("non-analytics")]
    [InlineData("wrong-backend")]
    public async Task Native_workspace_and_table_authorization_are_checked_even_without_rows(string change)
    {
        using var transport = await PreviewContextTransport.CreateAsync(telemetryEvidence: true);
        var backend = transport.Telemetry!.Configuration.Backends[0];
        switch (change)
        {
            case "shared-workspace": transport.Native[backend.ResourceId]["properties"]!["features"]!["enableLogAccessUsingOnlyResourcePermissions"] = true; break;
            case "protected-workspace": transport.Native[backend.ResourceId]["properties"]!["features"]!["dataAuthorizationMode"] = true; break;
            case "missing-table-protection": transport.Native[backend.ResourceId + "/tables/AppRequests"]["properties"]!.AsObject().Remove("protectionLevel"); break;
            case "non-analytics": transport.Native[backend.ResourceId + "/tables/AppRequests"]["properties"]!["plan"] = "Basic"; break;
            case "wrong-backend": transport.Native[backend.ResourceId]["id"] = backend.ResourceId + "-foreign"; break;
        }
        var result = await transport.RunTelemetryAsync(coordinator: true);
        Assert.Equal(HttpStatusCode.Conflict, result.Status);
        Assert.Contains(change == "wrong-backend" ? "WrongScope" : "Unsupported", result.Body);
        Assert.Empty(transport.TelemetryQueries);
    }

    [Theory]
    [InlineData("sample", true)]
    [InlineData("excess", false)]
    [InlineData("duplicate", false)]
    [InlineData("field-budget", false)]
    public async Task Reviewed_sampling_does_not_silently_truncate_or_duplicate_content(string change, bool complete)
    {
        using var transport = await PreviewContextTransport.CreateAsync(telemetryEvidence: true);
        transport.ChangeTelemetryQueryResponse = body =>
        {
            var rows = body["tables"]![0]!["rows"]!.AsArray();
            if (change == "field-budget") rows[0]![5] = new string('x', 2049);
            else
            {
                rows.Add(rows[0]!.DeepClone());
                if (change != "duplicate") rows[1]![2] = "application-record-2";
                if (change == "excess") { rows.Add(rows[0]!.DeepClone()); rows[2]![2] = "application-record-3"; }
            }
            return body;
        };
        var result = await transport.RunTelemetryAsync(coordinator: true);
        Assert.Equal(complete, result.Status == HttpStatusCode.OK);
        if (complete)
        {
            var evidence = JsonSerializer.Deserialize<PreviewTelemetryEvidence>(result.Body, FactorySourceValidation.RecordJson)!;
            Assert.Equal(2, evidence.Capture.Rows.Count);
            Assert.Equal(2, evidence.Capture.Proof.Queries.Single().Records);
            Assert.Contains("sample", evidence.Capture.Proof.CoverageLimitations);
        }
        else Assert.DoesNotContain("application-record-2", result.Body);
    }

    [Fact]
    public async Task All_selected_tables_and_backends_are_queried_by_independently_serialized_workers()
    {
        using var original = await PreviewContextTransport.CreateAsync(telemetryEvidence: true, mixedTables: true, multipleBackends: true);
        var first = await original.RunTelemetryAsync(coordinator: true);
        Assert.True(first.Status == HttpStatusCode.OK, first.Body);
        using var worker = await PreviewContextTransport.ReconstructAsync(original);
        worker.EnforceCouncilScopes = true;
        var second = await worker.RunTelemetryAsync(coordinator: true);
        Assert.True(second.Status == HttpStatusCode.OK, second.Body);
        var capture = JsonSerializer.Deserialize<PreviewTelemetryEvidence>(second.Body, FactorySourceValidation.RecordJson)!;
        Assert.Equal(3, capture.Capture.Rows.Count);
        Assert.Equal(3, capture.Capture.Proof.Queries.Count);
        Assert.Equal(3, worker.TelemetryQueries.Count);
        Assert.Contains(capture.Capture.Rows, row => row.Reference.Table == "AppTraces"
            && row.Fields["Message"].GetString() == "Shift confirmation rejected: already assigned.");
    }

    [Fact]
    public async Task Ordinary_new_application_rows_do_not_change_captured_configuration_authority()
    {
        using var transport = await PreviewContextTransport.CreateAsync(telemetryEvidence: true);
        var first = await transport.RunTelemetryAsync(coordinator: true);
        Assert.True(first.Status == HttpStatusCode.OK, first.Body);
        var before = JsonSerializer.Deserialize<PreviewTelemetryEvidence>(first.Body, FactorySourceValidation.RecordJson)!;
        transport.ChangeTelemetryQueryResponse = body => { body["tables"]![0]!["rows"]![0]![2] = "new-application-record"; return body; };
        var second = await transport.RunTelemetryAsync(coordinator: true, request: transport.Request with { RequestId = Guid.NewGuid() });
        Assert.True(second.Status == HttpStatusCode.OK, second.Body);
        var after = JsonSerializer.Deserialize<PreviewTelemetryEvidence>(second.Body, FactorySourceValidation.RecordJson)!;
        Assert.Equal(before.GovernanceRevision, after.GovernanceRevision);
        Assert.Equal(before.SourceAuthorityRevision, after.SourceAuthorityRevision);
        Assert.Equal("application-record-1", before.Capture.Rows.Single().Reference.ItemId);
        Assert.Equal("new-application-record", after.Capture.Rows.Single().Reference.ItemId);
    }

    [Theory]
    [InlineData("AppDependencies", "Name", "Shift reservation")]
    [InlineData("AppExceptions", "OuterMessage", "Shift already assigned.")]
    [InlineData("AppEvents", "Name", "ShiftConfirmationRejected")]
    public async Task Documented_first_party_content_projections_use_actual_native_columns(string table, string field, string expected)
    {
        using var transport = await PreviewContextTransport.CreateAsync(telemetryEvidence: true, telemetryTable: table);
        var result = await transport.RunTelemetryAsync(coordinator: true);
        Assert.True(result.Status == HttpStatusCode.OK, result.Body);
        var evidence = JsonSerializer.Deserialize<PreviewTelemetryEvidence>(result.Body, FactorySourceValidation.RecordJson)!;
        Assert.Equal(expected, evidence.Capture.Rows.Single().Fields[field].GetString());
        Assert.Equal(table, evidence.Capture.Rows.Single().Reference.Table);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_selected_tables_are_checked_but_an_empty_required_backend_is_not_ready(bool emptyBackend)
    {
        using var transport = await PreviewContextTransport.CreateAsync(telemetryEvidence: true, mixedTables: true, multipleBackends: true);
        transport.ChangeTelemetryQueryResponse = body =>
        {
            var query = transport.TelemetryQueries[^1].GetProperty("query").GetString()!;
            var rows = body["tables"]![0]!["rows"]!;
            if (emptyBackend ? (string?)rows[0]![3] != FactoryTelemetryHostTests.TelemetryTransport.WorkspaceId : query.StartsWith("AppTraces ", StringComparison.Ordinal))
                body["tables"]![0]!["rows"] = new JsonArray();
            return body;
        };
        var result = await transport.RunTelemetryAsync(coordinator: true);
        Assert.Equal(emptyBackend ? HttpStatusCode.Conflict : HttpStatusCode.OK, result.Status);
        var evidence = JsonSerializer.Deserialize<PreviewTelemetryEvidence>(result.Body, FactorySourceValidation.RecordJson)!;
        Assert.Equal(3, evidence.Capture.Proof.Queries.Count);
        Assert.Equal(4, transport.TelemetryQueries.Count);
        if (emptyBackend) Assert.Empty(evidence.Capture.Rows);
    }

    [Fact]
    public async Task Captured_evidence_expiring_before_operator_release_is_withheld()
    {
        using var transport = await PreviewContextTransport.CreateAsync(telemetryEvidence: true);
        transport.ChangeTelemetryQueryResponse = body =>
        {
            body["tables"]![0]!["rows"]![0]![0] = transport.Source.Clock.Now.AddSeconds(-transport.Telemetry!.Configuration.FreshnessSeconds + 5).ToString("O");
            return body;
        };
        transport.ChangeTelemetryResponse = body => { transport.Source.Clock.Now = transport.Source.Clock.Now.AddSeconds(10); return body; };
        var result = await transport.RunTelemetryAsync(coordinator: true);
        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.DoesNotContain("application-record-1", result.Body);
    }

    [Fact]
    public async Task Freshness_is_rechecked_after_the_final_current_capability_reads()
    {
        using var transport = await PreviewContextTransport.CreateAsync(telemetryEvidence: true);
        transport.ChangeTelemetryQueryResponse = body =>
        {
            body["tables"]![0]!["rows"]![0]![0] = transport.Source.Clock.Now.AddSeconds(-transport.Telemetry!.Configuration.FreshnessSeconds + 5).ToString("O");
            return body;
        };
        var returned = false;
        var advanced = false;
        transport.ChangeTelemetryResponse = body => { returned = true; return body; };
        transport.BeforeRequest = request =>
        {
            if (returned && !advanced && request.RequestUri!.Host == "management.azure.com")
            {
                advanced = true;
                transport.Source.Clock.Now = transport.Source.Clock.Now.AddSeconds(10);
            }
        };
        var result = await transport.RunTelemetryAsync(coordinator: true);
        Assert.True(advanced);
        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
        Assert.DoesNotContain("application-record-1", result.Body);
    }

    [Fact]
    public async Task Metadata_only_stale_probe_never_releases_sensitive_record_identifiers()
    {
        using var transport = await PreviewContextTransport.CreateAsync(telemetryEvidence: true);
        transport.ChangeTelemetryQueryResponse = body =>
        {
            var table = body["tables"]![0]!;
            if (table["columns"]!.AsArray().Count > 5) table["rows"] = new JsonArray();
            else
            {
                table["rows"]![0]![0] = transport.Source.Clock.Now.AddDays(-2).ToString("O");
                table["rows"]![0]![2] = "password=private-record-secret";
            }
            return body;
        };
        var result = await transport.RunTelemetryAsync(coordinator: true);
        Assert.Equal(HttpStatusCode.Conflict, result.Status);
        Assert.Contains("SensitiveContent", result.Body);
        Assert.DoesNotContain("private-record-secret", result.Body);
    }

    [Fact]
    public async Task Native_unknown_length_response_is_bounded_before_sdk_buffering()
    {
        using var transport = await PreviewContextTransport.CreateAsync(telemetryEvidence: true);
        var content = new CountingTelemetryContent();
        transport.TelemetryNativeResponse = request => request.RequestUri!.Host == "api.loganalytics.io"
            ? new(HttpStatusCode.OK) { Content = content } : null;
        var result = await transport.RunTelemetryAsync(coordinator: true);
        Assert.NotEqual(HttpStatusCode.OK, result.Status);
        Assert.Contains("Partial", result.Body);
        Assert.InRange(content.ReadBytes, 1, 131072 + 8192);
    }

    internal sealed class CountingTelemetryContent : HttpContent
    {
        private readonly CountingStream stream = new(System.Text.Encoding.UTF8.GetBytes("{\"data\":\"" + new string('x', 2 * 131072) + "\"}"));
        internal long ReadBytes => stream.ReadBytes;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(stream);
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => stream.CopyToAsync(target);
        private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
        {
            internal long ReadBytes;
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            { var read = await base.ReadAsync(buffer, token); ReadBytes += read; return read; }
        }
    }

    [Fact]
    public async Task Council_telemetry_consumption_names_missing_approved_content_projection()
    {
        using var transport = await PreviewContextTransport.CreateAsync();
        using var http = new HttpClient(transport, false);
        var environment = transport.Source.Environment;
        environment["AZURE_CLIENT_ID"] = transport.Models.Bindings![0].ClientId;
        var runner = new PreviewContextRunner(transport.Source) { Route = "/telemetry-evidence", Request = transport.Request };
        var exit = await RuntimeCliApplication.InvokeAsync(["serve-preview"], environment, new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with { WebHostRunner = runner, PreviewHttp = http,
                PreviewCredential = new IntentModelCredential(transport.Source), PreviewClock = transport.Source.Clock }, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Equal(HttpStatusCode.Forbidden, runner.Status);
        Assert.Contains("PreviewTelemetryProjectionRequired", runner.Body);
    }
}
