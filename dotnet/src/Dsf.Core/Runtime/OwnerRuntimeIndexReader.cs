namespace Dsf.Core.Runtime;

/// <summary>
/// Resolves a product's published runtime index from the owner App Configuration
/// authority (the same store <c>dsf new</c> writes to via
/// <c>PublishRuntimeIndexAsync</c>): entries labeled with the product key, keyed by
/// the exact env var names <see cref="RuntimeSettingsComposer"/> reads. This is how
/// a runtime command run with only <c>--product</c> (or <c>DSF_PRODUCT</c>) recovers
/// the rest of its Azure/GitHub configuration without every value being restated as
/// local environment variables.
/// </summary>
public interface IOwnerRuntimeIndexReader
{
    /// <summary>
    /// Reads the runtime index entries labeled <paramref name="product"/> from the
    /// owner App Configuration store at <paramref name="ownerAppConfigEndpoint"/>.
    /// Throws when the endpoint is unreachable or the product has no published index.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ReadAsync(
        string ownerAppConfigEndpoint,
        string product,
        CancellationToken cancellationToken);
}
