using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Dsf.Core.Onboarding;
using Dsf.Runtime.GitHubApp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dsf.Runtime;

public static partial class RuntimeVerbs
{
    public static WebApplication BuildPublisherHost(FactoryHostSettings settings, RuntimeDependencies dependencies,
        bool contextHost = false, string host = DefaultHost, int port = DefaultPort)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{host}:{port}");
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 4096);
        var app = builder.Build();
        if (contextHost)
        {
            app.MapPost("/readiness", (Func<HttpContext, Task<IResult>>)(c => Handle(c, true)));
            app.MapPost("/publication-context", (Func<HttpContext, Task<IResult>>)(c => Handle(c, false)));
        }
        else
        {
            app.MapPost("/capability", (Func<HttpContext, Task<IResult>>)(c => Handle(c, true)));
            app.MapPost("/publish", (Func<HttpContext, Task<IResult>>)(c => Handle(c, false)));
        }
        return app;

        async Task<IResult> Handle(HttpContext context, bool readiness)
        {
            context.Response.Headers.CacheControl = "no-store";
            using var deadline = new CancellationTokenSource(FactoryPreviewValidation.RequestBudget, dependencies.PreviewClock);
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);
            var token = pending.Token;
            var http = dependencies.PreviewHttp ?? PreviewHttp;
            var phase = "native-authority";
            try
            {
                if (context.Request.Headers["X-MS-CLIENT-PRINCIPAL-IDP"] != "aad") throw Denied("NativeCallerRequired");
                var credential = dependencies.PreviewCredential
                    ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
                var authority = new FactoryPublisherAuthority(settings, contextHost, credential, http, dependencies.PreviewClock);
                var current = await authority.CheckAsync(token);
                var capability = current.Capability;
                var identity = current.Manifest.Identity;
                var caller = capability.Plan.Deployment.Bindings[contextHost ? "auth" : "council"];
                if (!NativeCaller(context, contextHost && readiness ? identity.AzureActorId : caller.PrincipalId!,
                    identity.TenantId, contextHost && readiness ? null : caller.ClientId))
                    throw Denied("ExactNativePublicationCallerRequired");
                if (readiness)
                {
                    phase = "readiness-request";
                    var request = await context.Request.ReadFromJsonAsync<FactoryLiveRequest>(FactorySourceValidation.RecordJson, token) ?? throw new JsonException();
                    if (request.OperationId != capability.Plan.Selection.OperationId || request.RolloutFingerprint != capability.Plan.Fingerprint)
                        throw Denied("ExactReviewedPublicationRolloutRequired");
                    FactoryPublisherObservation observed;
                    if (contextHost)
                    {
                        phase = "current-council-readiness";
                        var live = FactoryLive.ReadConfirmed(current.Records.Where(p => p.Key.StartsWith(FactoryLive.Prefix, StringComparison.Ordinal))
                            .ToDictionary(p => p.Key, p => p.Value.Value), dependencies.PreviewClock.GetUtcNow());
                        _ = await new FactoryLiveAuthority(settings, dependencies, credential, http).CheckAsync(
                            new(request.OperationId, live.Plan.Fingerprint), true, token);
                        phase = "authenticated-publisher-readiness";
                        observed = await PostAsync<FactoryPublisherObservation>(capability.PublisherEndpoint, "/capability", request, current, token);
                        if (observed.FactoryId != identity.FactoryId || observed.CapabilityFingerprint != capability.Plan.Fingerprint
                            || observed.AuthorityRevision != current.Revision || observed.GitHub.State != GitHubAuthorityState.Verified
                            || observed.GitHub.AuthPrincipalId != capability.Plan.GitHubAuthority.AuthPrincipalId
                            || observed.GitHub.KeyVersionUri != capability.Plan.GitHubAuthority.DestinationSecretUri)
                            throw Denied("OriginalAuthenticatedPublisherReadinessRequired");
                    }
                    else
                    {
                        phase = "github-access";
                        var access = await new FactoryGitHubVerifier(http, current.Credential, dependencies.PreviewClock)
                            .AcquireProposalAccessAsync(capability.Plan.GitHubAuthority, token);
                        phase = "proposal-mutation-review";
                        var review = await access.ReviewAsync(capability.Plan.Automation, token);
                        observed = new(1, identity.FactoryId, capability.Plan.Fingerprint, current.Revision, access.Proof, review);
                    }
                    await authority.RequireSameAsync(current, token);
                    return Results.Json(observed, OutputJson);
                }
                var publication = await context.Request.ReadFromJsonAsync<FactoryPublicationRequest>(FactorySourceValidation.RecordJson, token) ?? throw new JsonException();
                phase = "final-promotion";
                FactoryPromotion.Validate(publication);
                _ = FactoryPromotion.RequireApproved(current.Records.ToDictionary(p => p.Key, p => p.Value.Value),
                    current.Records.Keys.ToDictionary(k => k, current.Identity), capability.Plan, dependencies.PreviewClock.GetUtcNow());
                if (capability.Coordinator.NativeProviderProof is null)
                    throw Denied("OriginalAuthenticatedPublisherProviderProofRequired");
                if (contextHost)
                {
                    phase = "native-decision-qualification";
                    var admission = await RuntimeWorkflow.QualifyPublicationAsync(settings, dependencies, current, publication, credential, http, token);
                    await authority.RequireSameAsync(current, token);
                    return Results.Json(admission, OutputJson);
                }
                phase = "authenticated-decision-admission";
                var approved = await PostAsync<FactoryPublicationAdmission>(capability.ContextEndpoint, "/publication-context", publication, current, token);
                ValidateAdmission(approved);
                phase = "original-native-publication";
                var result = await new FactoryGitHubProposalPublisher(current.Credential, http, dependencies.PreviewClock).CreateAsync(
                    approved.Intent, capability.Plan.GitHubAuthority, token, async cancellation =>
                    {
                        await authority.RequireSameAsync(current, cancellation);
                        var latest = await PostAsync<FactoryPublicationAdmission>(capability.ContextEndpoint, "/publication-context", publication, current, cancellation);
                        ValidateAdmission(latest);
                        if (FactoryModelsValidation.Hash(latest) != FactoryModelsValidation.Hash(approved))
                            throw Denied("OriginalQualifiedPublicationDecisionChanged");
                    });
                return Results.Json(result, OutputJson, statusCode: result.State == FactoryPublicationState.Confirmed ? 200 : 409);

                void ValidateAdmission(FactoryPublicationAdmission admission)
                {
                    FactoryPublication.Validate(admission.Intent);
                    if (admission.AuthorityRevision != current.Revision || admission.Intent.FactoryId != identity.FactoryId
                        || admission.Intent.RepositoryId != identity.RepositoryId || admission.Intent.Product != identity.ProductKey
                        || admission.Intent.InvocationId != publication.InvocationId || admission.Promotion != current.Identity(FactoryPromotion.RootKey)
                        || admission.Publisher != current.Identity(FactoryPublisher.RootKey)
                        || FactoryModelsValidation.Hash(admission.Intent.AutomationReview) != FactoryModelsValidation.Hash(capability.Plan.Automation)
                        || !new[] { admission.Promotion, admission.Publisher, admission.Evaluation, admission.Run }
                            .All(r => admission.Intent.References.Contains(r)))
                        throw Denied("AuthenticatedCurrentNativeDecisionAdmissionRequired");
                }
            }
            catch (PreviewCapabilityException e) { return Failure(e.State, e.Message, 403); }
            catch (OperationCanceledException) { return Failure(FactoryCharterState.Unknown, "OriginalPublicationOrReadinessUnconfirmedNoAutomaticReplay", 409); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException
                or FormatException or HttpRequestException or IOException or Azure.RequestFailedException or AuthenticationFailedException
                or BadHttpRequestException or CosmosCheckpointException or ModelCapabilityException or SourceAcquisitionException
                or IntentException or CharterAdoptionEvidenceException or SupplementalSourceException)
            { return Failure(FactoryCharterState.Unknown, "PublicationCapabilityOrOriginalDecisionUnconfirmedNoFallback:" + phase, 409); }

            async Task<T> PostAsync<T>(string endpoint, string route, object request, PublisherAuthoritySnapshot snapshot, CancellationToken cancellation)
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, FactoryGovernance.NormalizeEndpoint(endpoint) + route)
                { Content = JsonContent.Create(request, options: FactorySourceValidation.RecordJson) };
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await snapshot.Credential.GetTokenAsync(
                    new TokenRequestContext(["api://" + snapshot.Capability.Plan.Selection.AudienceClientId + "/.default"]), cancellation)).Token);
                using var response = await http.SendAsync(message, cancellation);
                if (response.StatusCode != HttpStatusCode.OK) throw Denied("AuthenticatedNativePublicationDependencyUnavailable");
                using var body = await EvidencePayload.ReadAsync(response, cancellation, 256 * 1024);
                return body.RootElement.Deserialize<T>(FactorySourceValidation.RecordJson) ?? throw new JsonException();
            }
        }
        static PreviewCapabilityException Denied(string reason) => new(FactoryCharterState.Blocked, reason);
        static IResult Failure(FactoryCharterState state, string reason, int status) =>
            Results.Json(new { State = state, Reason = reason, IsActivated = false, PublicationPermitted = false }, OutputJson, statusCode: status);
    }
    private static readonly JsonSerializerOptions OutputJson = new(FactoryGitHubJson.Options) { IgnoreReadOnlyProperties = false };
}
