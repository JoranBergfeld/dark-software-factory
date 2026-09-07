namespace Dsf.Cli;

internal sealed record GitHubAppManifest(
    string Name,
    string Url,
    string RedirectUrl,
    bool Public,
    IReadOnlyDictionary<string, string> DefaultPermissions,
    IReadOnlyList<string> DefaultEvents)
{
    public static GitHubAppManifest Create(string name, Uri callbackUri) => new(
        name,
        callbackUri.AbsoluteUri,
        callbackUri.AbsoluteUri,
        false,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["issues"] = "write",
            ["pull_requests"] = "write",
            ["contents"] = "write",
            ["administration"] = "write",
        },
        []);

    public static string ParseCode(string raw)
    {
        var text = raw?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            throw new InvalidOperationException("GitHub App manifest callback code is required.");
        }

        if (!text.Contains("code=", StringComparison.Ordinal))
        {
            return text;
        }

        var query = Uri.TryCreate(text, UriKind.Absolute, out var uri)
            ? uri.Query
            : text.TrimStart('?');
        var code = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .FirstOrDefault(pair => pair.Length == 2 && pair[0] == "code")?[1];
        return string.IsNullOrWhiteSpace(code)
            ? throw new InvalidOperationException("GitHub App manifest callback contained no code.")
            : Uri.UnescapeDataString(code);
    }
}
