using System.Text.Json;
using Azure.Identity;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dsf.Runtime;

public static partial class RuntimeVerbs
{
    private static readonly HttpClient ModelHttp = new(new HttpClientHandler { AllowAutoRedirect = false });
    public static WebApplication BuildModelsHost(FactoryHostSettings settings, RuntimeDependencies dependencies,
        string host = DefaultHost, int port = DefaultPort)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 128 * 1024);
        builder.WebHost.UseUrls($"http://{host}:{port}");
        var app = builder.Build();
        app.MapPost("/proof", async (HttpContext context) =>
        {
            try
            {
                if (!Guid.TryParse(settings.ManagedIdentityClientId, out var clientId) || clientId == Guid.Empty
                    || settings.ProfilePin.Length != 64 || !settings.ProfilePin.All(char.IsAsciiHexDigit)) throw new JsonException();
                var credential = dependencies.ModelCredential
                    ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
                var configuration = new AzureConfigurationSettingsGateway(credential, dependencies.ModelHttp);
                var (manifest, authority, _) = await ReadAsync(configuration, context.RequestAborted);
                if (!NativeCaller(context, manifest.Identity.AzureActorId, manifest.Identity.TenantId, null)
                    || authority.Bindings![0].ClientId != settings.ManagedIdentityClientId
                    || authority.State != ModelCapabilityState.Partial || authority.Stage != "checking"
                    || authority.CheckId is null || authority.CheckId == Guid.Empty)
                    throw new JsonException();
                var workloads = new FactoryModelWorkloads(manifest, authority, credential, dependencies.ModelHttp ?? ModelHttp, dependencies.ModelClock);
                var started = dependencies.ModelClock.GetUtcNow();
                var observations = new List<ModelWorkloadObservation>();
                foreach (var route in FactoryModelsValidation.RouteNames)
                {
                    try
                    {
                        ModelWorkloadObservation observation;
                        if (route == "synthesis")
                            observation = (await workloads.SynthesizeAsync(
                                "CAPABILITY PROBE, not application evidence: explain why semantically equivalent descriptions should retain a stable logical problem identifier.", context.RequestAborted)).Observation;
                        else if (route == "embedding")
                            observation = (await workloads.DeduplicateAsync([
                                new("one", "CAPABILITY PROBE: users cannot sign in because their session expires."),
                                new("two", "CAPABILITY PROBE: expired sessions prevent user login."),
                                new("three", "CAPABILITY PROBE: export invoices as PDF documents."),
                            ], context.RequestAborted)).Observation;
                        else observation = (await workloads.JudgeAsync(route,
                            "CAPABILITY PROBE, not a proposal or evidence: should equivalent problem descriptions preserve an existing stable logical identity rather than create duplicate identifiers? Explain your independent judgment.", context.RequestAborted)).Observation;
                        observations.Add(observation);
                    }
                    catch (ModelCapabilityException e)
                    { observations.Add(new(route, authority.ApprovalFingerprint, ModelCapabilityState.Blocked, e.Reason, null, null, null, 0, 0, [])); }
                    catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException)
                    { observations.Add(new(route, authority.ApprovalFingerprint, ModelCapabilityState.Blocked, ModelCapabilityReason.MalformedResponse, null, null, null, 0, 0, [])); }
                }
                var (latestManifest, latestAuthority, _) = await ReadAsync(configuration, context.RequestAborted);
                if (FactoryModelsValidation.Hash(manifest) != FactoryModelsValidation.Hash(latestManifest)
                    || FactoryModelsValidation.Hash(authority) != FactoryModelsValidation.Hash(latestAuthority)) throw new JsonException();
                var now = dependencies.ModelClock.GetUtcNow();
                if (now < started || now - started >= FactoryModelsValidation.ProofExecutionBudget(authority.Configuration.ValidForSeconds))
                    observations[0] = observations[0] with { State = ModelCapabilityState.Blocked, Reason = ModelCapabilityReason.Expired };
                var state = observations.All(o => o.State == ModelCapabilityState.Verified) ? ModelCapabilityState.Verified : ModelCapabilityState.Blocked;
                var proof = new ModelCapabilityProof(authority.Configuration.FactoryId, authority.OperationId, authority.CheckId.Value,
                    authority.ApprovalFingerprint, authority.Bindings[0].PrincipalId!, started, started.AddSeconds(authority.Configuration.ValidForSeconds), state, observations);
                FactoryModelsValidation.ValidateProof(authority, proof, now, requireCurrent: false);
                context.Response.Headers.CacheControl = "no-store";
                return Results.Json(proof, FactoryGitHubJson.Options, statusCode: state == ModelCapabilityState.Verified ? 200 : 409);
            }
            catch (OperationCanceledException)
            { return Results.Json(new { state = "Canceled", detail = "Workload proof canceled; no readiness or revocation asserted." }, statusCode: 409); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException
                or Azure.RequestFailedException or HttpRequestException or AuthenticationFailedException or ModelCapabilityException)
            { return Results.Json(new { state = "Blocked", detail = "Current pinned inactive model authority and native authenticated operator required; no proof released." }, statusCode: 403); }
        });
        Func<HttpContext, Task<IResult>> synthesis = context => ConsumeAsync(context, "synthesis");
        Func<HttpContext, Task<IResult>> deduplication = context => ConsumeAsync(context, "embedding");
        app.MapPost("/synthesis", synthesis);
        app.MapPost("/deduplicate", deduplication);
        Func<HttpContext, Task<IResult>> nativeAuthority = context => ConsumeAsync(context, "authority", authorityOnly: true);
        app.MapPost("/authority", nativeAuthority);
        app.MapPost("/jurors/{juror}", (HttpContext context, string juror) => ConsumeAsync(context, juror));
        return app;

        async Task<IResult> ConsumeAsync(HttpContext context, string route, bool authorityOnly = false)
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                if (!authorityOnly && !FactoryModelsValidation.RouteNames.Contains(route) || !Guid.TryParse(settings.ManagedIdentityClientId, out var id)
                    || id == Guid.Empty || settings.ProfilePin.Length != 64 || !settings.ProfilePin.All(char.IsAsciiHexDigit)) throw new JsonException();
                var credential = dependencies.ModelCredential
                    ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
                var configuration = new AzureConfigurationSettingsGateway(credential, dependencies.ModelHttp);
                var (manifest, authority, values) = await ReadAsync(configuration, context.RequestAborted);
                var caller = authority.Bindings![route.StartsWith("juror-", StringComparison.Ordinal) ? 0 : 1];
                if (authority.Bindings[0].ClientId != settings.ManagedIdentityClientId
                    || !NativeCaller(context, caller.PrincipalId!, manifest.Identity.TenantId, caller.ClientId)) throw new JsonException();
                ModelCapabilityReference reference;
                ModelDeduplicationRequest? deduplication = null;
                ModelWorkloadRequest? completion = null;
                ModelAuthorityRequest? native = null;
                if (route == "authority")
                {
                    native = await context.Request.ReadFromJsonAsync<ModelAuthorityRequest>(FactorySourceValidation.RecordJson, context.RequestAborted) ?? throw new JsonException();
                    if (native.CheckId == Guid.Empty) throw new JsonException();
                    reference = native.Capability;
                }
                else if (route == "embedding")
                {
                    deduplication = await context.Request.ReadFromJsonAsync<ModelDeduplicationRequest>(FactoryGitHubJson.Options, context.RequestAborted) ?? throw new JsonException();
                    reference = deduplication.Capability;
                }
                else
                {
                    completion = await context.Request.ReadFromJsonAsync<ModelWorkloadRequest>(FactoryGitHubJson.Options, context.RequestAborted) ?? throw new JsonException();
                    reference = completion.Capability;
                }
                authority = FactoryModelsValidation.ReadVerified(manifest, values, reference, dependencies.ModelClock.GetUtcNow());
                var workloads = new FactoryModelWorkloads(manifest, authority, credential, dependencies.ModelHttp ?? ModelHttp, dependencies.ModelClock);
                object result;
                if (native is not null)
                {
                    result = await workloads.ReadAuthorityAsync(native.CheckId, context.RequestAborted);
                }
                else result = route == "embedding" ? await workloads.DeduplicateAsync(deduplication!.Items, context.RequestAborted)
                    : route == "synthesis" ? await workloads.SynthesizeAsync(completion!.Input, context.RequestAborted)
                    : await workloads.JudgeAsync(route, completion!.Input, context.RequestAborted);
                var (latestManifest, _, latestValues) = await ReadAsync(configuration, context.RequestAborted);
                var latest = FactoryModelsValidation.ReadVerified(latestManifest, latestValues, reference, dependencies.ModelClock.GetUtcNow());
                if (FactoryModelsValidation.Hash(latestManifest) != FactoryModelsValidation.Hash(manifest)
                    || FactoryModelsValidation.Hash(latest) != FactoryModelsValidation.Hash(authority)) throw new JsonException();
                context.Response.Headers.CacheControl = "no-store";
                return Results.Json(result, FactoryGitHubJson.Options);
            }
            catch (ModelCapabilityException e)
            { return Results.Json(new { state = "Blocked", route = e.Route, reason = e.Reason.ToString(), detail = e.Message }, statusCode: 409); }
            catch (OperationCanceledException)
            { return Results.Json(new { state = "Canceled", detail = "Model workload canceled; no result released." }, statusCode: 409); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException
                or Azure.RequestFailedException or HttpRequestException or AuthenticationFailedException)
            { return Results.Json(new { state = "Blocked", detail = "Current verified capability reference and exact native workload caller required; no result released." }, statusCode: 403); }
        }

        async Task<(FactoryManifest Manifest, FactoryModels Authority, Dictionary<string, string> Values)> ReadAsync(AzureConfigurationSettingsGateway gateway, CancellationToken token)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            await foreach (var (key, value) in gateway.ListAsync(settings.ProductEndpoint, ProductConfigurationKeys.NoLabel, token))
                if (!values.TryAdd(key, value)) throw new JsonException();
            var manifest = FactoryGovernance.ReadInactiveManifest(values.GetValueOrDefault(ProductConfigurationKeys.FactoryManifest),
                values.GetValueOrDefault(ProductConfigurationKeys.FactoryProfile), settings.Product, settings.ProductEndpoint, settings.ProfilePin);
            return (manifest, FactoryModelsValidation.ReadPreparation(manifest, values), values);
        }
    }
}
