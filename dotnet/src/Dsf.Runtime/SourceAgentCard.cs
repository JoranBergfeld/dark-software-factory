using Dsf.Core.Runtime;

namespace Dsf.Runtime;

/// <summary>
/// The A2A agent card a served source agent publishes at
/// <c>/.well-known/agent-card.json</c>: who the agent is, which source kind it
/// speaks for, and the skill endpoint a client posts a gather request to. Built
/// from <see cref="SourceAgentKinds"/> so the served card and the kinds the
/// runtime accepts can never drift.
/// </summary>
public sealed record SourceAgentCard(
    string Name,
    string Kind,
    string Description,
    string Product,
    string CardPath,
    string GatherPath)
{
    public const string CardRoute = "/.well-known/agent-card.json";
    public const string GatherRoute = "/gather";

    /// <summary>
    /// Builds the card for <paramref name="kind"/>. Throws
    /// <see cref="RuntimeVerbException"/> naming the valid choices when the kind is
    /// not one the runtime knows.
    /// </summary>
    public static SourceAgentCard For(string kind, string product)
    {
        var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();
        if (!SourceAgentKinds.IsKnown(normalized))
        {
            throw new RuntimeVerbException(
                $"unknown source agent kind '{kind}' (choices: {string.Join(", ", SourceAgentKinds.Known)}).");
        }

        return new SourceAgentCard(
            Name: $"dsf-{normalized}-agent",
            Kind: normalized,
            Description: $"Dark Software Factory source agent for '{normalized}' evidence.",
            Product: product,
            CardPath: CardRoute,
            GatherPath: GatherRoute);
    }
}
