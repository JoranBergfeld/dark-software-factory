using System.Text.Json;
using Azure.Identity;
using Dsf.Core.Charters;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

public static partial class RuntimeVerbs
{
    /// <summary>
    /// Source-workload implementation, using its own proven code identity.
    /// This is not council admission, an operator endpoint or permission to execute a preview.
    /// </summary>
    public static async Task<SourceCharterContextResult> ReadSourceCharterContextAsync(
        FactoryHostSettings settings, RuntimeDependencies dependencies, CancellationToken token)
    {
        try
        {
            using var deadline = new CancellationTokenSource(FactoryIntentLimits.RequestBudget, dependencies.SourceClock);
            using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            var credential = dependencies.SourceCredential
                ?? new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ManagedIdentityClientId));
            var http = dependencies.SourceHttp ?? SourceHttp;
            var source = new FactorySourceAccess(settings, credential, http, dependencies.SourceClock);
            await source.CheckAsync(pending.Token);
            var records = new AzureCharterEvidenceReader(settings.ProductEndpoint, source.Credential, http);
            return await RuntimeWorkflow.ReadSourceCharterContextAsync(source, records, http, dependencies.SourceClock, pending.Token);
        }
        catch (CharterAdoptionEvidenceException)
        { return Failure(FactoryCharterState.Unknown, "Protected adoption history or current pointer unconfirmed."); }
        catch (IntentException e)
        { return Failure(Enum.Parse<FactoryCharterState>(e.State.ToString()), e.Message); }
        catch (SourceAcquisitionException e)
        { return Failure(Enum.Parse<FactoryCharterState>(FactoryIntentWorkflow.Map(e.State).ToString()), e.Message); }
        catch (OperationCanceledException)
        { return Failure(token.IsCancellationRequested ? FactoryCharterState.Canceled : FactoryCharterState.Partial,
            "Source context canceled or time budget exhausted; no current context released."); }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        { return Failure(FactoryCharterState.Partial, "Source screening time limit; no current context released."); }
        catch (Exception e) when (e is HttpRequestException or IOException or Azure.RequestFailedException or AuthenticationFailedException)
        { return Failure(FactoryCharterState.Unknown, "Required source context transport or identity unavailable; provider details withheld."); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException
            or FormatException or ModelCapabilityException or CharterParseException)
        { return Failure(FactoryCharterState.Blocked, "Current adopted intent, complete source analysis and native model authority required."); }

        static SourceCharterContextResult Failure(FactoryCharterState state, string detail) => new(state, detail, null, null);
    }
}
