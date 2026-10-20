using System.CommandLine;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

public static partial class CliApplication
{
    private sealed record OnboardLifecycleCommandContext(
        string OwnerEndpoint, string Product, string TenantId, string SubscriptionId, Guid OperationId);

    private static OnboardLifecycleCommandContext ParseOnboardLifecycleContext(
        Func<Option<string>, string?> value, ICliTerminal terminal, bool interactive,
        Option<string> endpoint, Option<string> product, Option<string> tenant,
        Option<string> subscription, Option<string> operation, Func<Exception> invalid)
    {
        string Required(Option<string> option)
        {
            var input = value(option)?.Trim();
            if (string.IsNullOrEmpty(input) && interactive)
                input = terminal.Prompt(option.Name + ": ")?.Trim();
            return !string.IsNullOrEmpty(input) ? input : throw invalid();
        }

        Guid Identifier(Option<string> option) =>
            Guid.TryParse(Required(option), out var id) && id != Guid.Empty ? id : throw invalid();

        return new(
            FactoryGovernance.NormalizeEndpoint(Required(endpoint)),
            Required(product),
            Identifier(tenant).ToString(),
            Identifier(subscription).ToString(),
            Identifier(operation));
    }
}
