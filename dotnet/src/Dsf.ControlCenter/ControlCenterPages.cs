using System.Net;
using System.Text;

namespace Dsf.ControlCenter;

/// <summary>
/// Renders the operator surface as self-contained HTML: no CDN, no client-side
/// framework, every control's effective value visible, and every write carried by
/// a plain form that must echo the server-issued CSRF token.
/// </summary>
internal static class ControlCenterPages
{
    private const string Style = """
        <style>
        :root { color-scheme: light dark; }
        body { font-family: system-ui, sans-serif; margin: 2rem auto; max-width: 60rem; line-height: 1.5; }
        table { border-collapse: collapse; width: 100%; }
        th, td { border-bottom: 1px solid #8884; padding: 0.4rem 0.6rem; text-align: left; }
        .error { border-left: 4px solid #c0392b; padding: 0.5rem 0.8rem; background: #c0392b22; }
        .unsupported { opacity: 0.65; }
        .why { font-size: 0.9rem; }
        form.inline { display: inline; }
        </style>
        """;

    public static string SignIn(string? error) => Document(
        "Sign in",
        $"""
        <h1>Control Center</h1>
        <p>Sign in with the operator token to govern factory products.</p>
        {ErrorBanner(error)}
        <form method="post" action="/session">
          <label for="operator_token">Operator token</label>
          <input id="operator_token" name="operator_token" type="password" autocomplete="current-password" required>
          <button type="submit">Sign in</button>
        </form>
        """);

    public static string ProductList(IReadOnlyList<ProductSummary> products, string csrfToken)
    {
        var rows = products.Count == 0
            ? "<tr><td colspan=\"3\">No products are published in the owner App Configuration index.</td></tr>"
            : string.Concat(products.Select(product => $"""
                <tr>
                  <td><a href="/products/{Encode(Uri.EscapeDataString(product.Key))}">{Encode(product.Key)}</a></td>
                  <td>{Encode(product.GitHubRepository)}</td>
                  <td>{Encode(product.AppConfigEndpoint)}</td>
                </tr>
                """));

        return Document(
            "Products",
            $"""
            <h1>Products</h1>
            {SignOutForm(csrfToken)}
            <p>Governance is product-first: pick the product whose policy you want to inspect or change.</p>
            <table>
              <thead><tr><th>Product</th><th>Repository</th><th>Configuration store</th></tr></thead>
              <tbody>{rows}</tbody>
            </table>
            """);
    }

    public static string ProductPolicyPage(ProductPolicy policy, string csrfToken, string? error)
    {
        var productPath = Uri.EscapeDataString(policy.Product);
        var agentRows = string.Concat(policy.AgentEnablement
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"""
                <tr>
                  <td>{Encode(entry.Key)}</td>
                  <td>{(entry.Value ? "enabled" : "disabled")}</td>
                  <td>
                    <form class="inline" method="post" action="/products/{Encode(productPath)}/agents">
                      <input type="hidden" name="{OperatorSessionStore.CsrfField}" value="{Encode(csrfToken)}">
                      <input type="hidden" name="kind" value="{Encode(entry.Key)}">
                      <input type="hidden" name="enabled" value="{(entry.Value ? "false" : "true")}">
                      <button type="submit">{(entry.Value ? "Disable" : "Enable")}</button>
                    </form>
                  </td>
                </tr>
                """));

        var unsupported = string.Concat(UnsupportedControls.All.Select(control => $"""
            <li class="unsupported">
              <label>{Encode(control.Name)}
                <input type="text" value="unavailable" disabled aria-disabled="true">
              </label>
              <p class="why">{Encode(control.Reason)} Supported alternative: {Encode(control.SupportedAlternative)}</p>
            </li>
            """));

        return Document(
            $"Product {policy.Product}",
            $"""
            <h1>{Encode(policy.Product)}</h1>
            <p><a href="/products">All products</a> &middot; {Encode(policy.GitHubRepository)}</p>
            {SignOutForm(csrfToken)}
            {ErrorBanner(error)}
            <h2>Effective product policy</h2>
            <form method="post" action="/products/{Encode(productPath)}/threshold">
              <input type="hidden" name="{OperatorSessionStore.CsrfField}" value="{Encode(csrfToken)}">
              <label for="value">Confidence threshold (effective {Encode(PolicyValidation.Format(policy.ConfidenceThreshold))})</label>
              <input id="value" name="value" type="number" inputmode="decimal" step="0.01"
                     min="{PolicyValidation.Format(PolicyValidation.MinimumConfidenceThreshold)}"
                     max="{PolicyValidation.Format(PolicyValidation.MaximumConfidenceThreshold)}"
                     value="{Encode(PolicyValidation.Format(policy.ConfidenceThreshold))}" required>
              <button type="submit">Save threshold</button>
            </form>
            <h2>Source agents</h2>
            <table>
              <thead><tr><th>Agent</th><th>Effective state</th><th>Change</th></tr></thead>
              <tbody>{agentRows}</tbody>
            </table>
            <h2>Unavailable controls</h2>
            <ul>{unsupported}</ul>
            """);
    }

    public static string Message(string heading, string detail) => Document(
        heading,
        $"""
        <h1>{Encode(heading)}</h1>
        <p>{Encode(detail)}</p>
        <p><a href="/products">All products</a></p>
        """);

    private static string SignOutForm(string csrfToken) => $"""
        <form method="post" action="/sign-out">
          <input type="hidden" name="{OperatorSessionStore.CsrfField}" value="{Encode(csrfToken)}">
          <button type="submit">Sign out</button>
        </form>
        """;

    private static string ErrorBanner(string? error) =>
        string.IsNullOrWhiteSpace(error) ? string.Empty : $"<p class=\"error\">{Encode(error)}</p>";

    private static string Document(string title, string body)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<title>").Append(Encode(title)).Append(" &middot; DSF Control Center</title>");
        html.Append(Style).Append("</head><body>").Append(body).Append("</body></html>");
        return html.ToString();
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
