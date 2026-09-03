using System.Text;
using Dsf.Core.Runtime;

namespace Dsf.ControlCenter;

/// <summary>
/// The Control Center web process: a product-first governance surface over the
/// owner App Configuration authority.
///
/// Browser writes are protected by two independent factors -- a server-issued,
/// HttpOnly session cookie and a CSRF token minted with that session which every
/// form must echo back. A bearer token is deliberately *not* accepted on the
/// browser write routes, so a leaked automation credential cannot be turned into
/// a one-request cross-site write; automated clients use the separate
/// <c>/api</c> surface instead.
/// </summary>
internal static class ControlCenterApp
{
    private const string HtmlContentType = "text/html; charset=utf-8";

    public static WebApplication Build(
        ControlCenterSettings settings,
        IProductPolicyAuthority authority,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(authority);

        var sessions = new OperatorSessionStore(clock ?? TimeProvider.System, TimeSpan.FromHours(8));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://{settings.Host}:{settings.Port}");
        var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Dsf.ControlCenter");

        app.MapGet("/healthz", () => Results.Ok(new
        {
            status = "ok",
            process = "control-center",
            authority = settings.OwnerAppConfigEndpoint,
        }));

        app.MapGet("/", () => SeeOther("/products"));

        app.MapPost("/session", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            var form = await ReadFormAsync(context, cancellationToken);
            if (!OperatorSessionStore.TokensMatch(form.GetValueOrDefault("operator_token"), settings.OperatorToken))
            {
                logger.LogWarning(
                    "rejected control center sign-in from {Remote}",
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
                return Html(
                    ControlCenterPages.SignIn("That operator token is not valid."),
                    StatusCodes.Status401Unauthorized);
            }

            var (sessionId, csrfToken) = sessions.Create();
            var options = CookieOptions(settings);
            context.Response.Cookies.Append(OperatorSessionStore.SessionCookie, sessionId, options);
            context.Response.Cookies.Append(OperatorSessionStore.CsrfCookie, csrfToken, options);
            logger.LogInformation(
                "control center sign-in from {Remote}",
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            return SeeOther("/products");
        });

        app.MapPost("/sign-out", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            var form = await ReadFormAsync(context, cancellationToken);
            if (TryRejectWrite(context, sessions, form, out var rejection))
            {
                return rejection;
            }

            sessions.Remove(context.Request.Cookies[OperatorSessionStore.SessionCookie]);
            var options = CookieOptions(settings);
            context.Response.Cookies.Delete(OperatorSessionStore.SessionCookie, options);
            context.Response.Cookies.Delete(OperatorSessionStore.CsrfCookie, options);
            return SeeOther("/");
        });

        app.MapGet("/products", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!TryAuthorizeSession(context, sessions, out var csrfToken))
            {
                return SignInRequired();
            }

            try
            {
                var products = await authority.ListProductsAsync(cancellationToken);
                return Html(ControlCenterPages.ProductList(products, csrfToken));
            }
            catch (ConfigurationAuthorityUnavailableException exception)
            {
                // The authority is the Control Center's only source of truth; when it
                // cannot be read, say so instead of rendering an empty, reassuring list.
                logger.LogError(exception, "failed to list products from the configuration authority");
                return Html(
                    ControlCenterPages.Message("Configuration authority unavailable", exception.Message),
                    StatusCodes.Status502BadGateway);
            }
        });

        app.MapGet("/products/{product}", async (
            string product,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorizeSession(context, sessions, out var csrfToken))
            {
                return SignInRequired();
            }

            return await RenderPolicyAsync(authority, product, csrfToken, error: null, StatusCodes.Status200OK, cancellationToken);
        });

        app.MapPost("/products/{product}/threshold", async (
            string product,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var form = await ReadFormAsync(context, cancellationToken);
            if (TryRejectWrite(context, sessions, form, out var rejection))
            {
                return rejection;
            }

            var csrfToken = form.GetValueOrDefault(OperatorSessionStore.CsrfField) ?? string.Empty;
            if (!PolicyValidation.TryValidateConfidenceThreshold(form.GetValueOrDefault("value"), out var threshold, out var error))
            {
                return await RenderPolicyAsync(
                    authority, product, csrfToken, error, StatusCodes.Status400BadRequest, cancellationToken);
            }

            try
            {
                await authority.SetConfidenceThresholdAsync(product, threshold, cancellationToken);
            }
            catch (ProductNotFoundException exception)
            {
                return Html(
                    ControlCenterPages.Message("Product unavailable", exception.Message),
                    StatusCodes.Status404NotFound);
            }
            catch (ConfigurationAuthorityUnavailableException exception)
            {
                logger.LogError(exception, "failed to write confidence threshold through configuration authority");
                return AuthorityUnavailable(exception);
            }

            logger.LogInformation(
                "set confidence threshold product={Product} value={Threshold} remote={Remote}",
                product,
                threshold,
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            return SeeOther(ProductPath(product));
        });

        app.MapPost("/products/{product}/agents", async (
            string product,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var form = await ReadFormAsync(context, cancellationToken);
            if (TryRejectWrite(context, sessions, form, out var rejection))
            {
                return rejection;
            }

            var csrfToken = form.GetValueOrDefault(OperatorSessionStore.CsrfField) ?? string.Empty;
            var kind = (form.GetValueOrDefault("kind") ?? string.Empty).Trim().ToLowerInvariant();
            if (!SourceAgentKinds.IsKnown(kind))
            {
                return await RenderPolicyAsync(
                    authority,
                    product,
                    csrfToken,
                    $"'{kind}' is not a known source agent; expected one of {string.Join(", ", SourceAgentKinds.Known)}.",
                    StatusCodes.Status400BadRequest,
                    cancellationToken);
            }

            var enabled = IsTruthy(form.GetValueOrDefault("enabled"));
            try
            {
                await authority.SetAgentEnabledAsync(product, kind, enabled, cancellationToken);
            }
            catch (ProductNotFoundException exception)
            {
                return Html(
                    ControlCenterPages.Message("Product unavailable", exception.Message),
                    StatusCodes.Status404NotFound);
            }
            catch (ConfigurationAuthorityUnavailableException exception)
            {
                logger.LogError(exception, "failed to write agent enablement through configuration authority");
                return AuthorityUnavailable(exception);
            }

            logger.LogInformation(
                "set agent enablement product={Product} kind={Kind} enabled={Enabled} remote={Remote}",
                product,
                kind,
                enabled,
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            return SeeOther(ProductPath(product));
        });

        app.MapGet("/api/products", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!IsAuthorizedApiClient(context, settings))
            {
                return Unauthorized();
            }

            try
            {
                return Results.Ok(await authority.ListProductsAsync(cancellationToken));
            }
            catch (ConfigurationAuthorityUnavailableException exception)
            {
                logger.LogError(exception, "failed to list products from the configuration authority");
                return AuthorityUnavailableJson(exception);
            }
        });

        app.MapGet("/api/products/{product}", async (
            string product,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!IsAuthorizedApiClient(context, settings))
            {
                return Unauthorized();
            }

            try
            {
                return Results.Ok(await authority.ReadPolicyAsync(product, cancellationToken));
            }
            catch (ProductNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
            catch (ConfigurationAuthorityUnavailableException exception)
            {
                logger.LogError(exception, "failed to read product policy from the configuration authority");
                return AuthorityUnavailableJson(exception);
            }
        });

        app.MapPost("/api/products/{product}/threshold", async (
            string product,
            ThresholdRequest request,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!IsAuthorizedApiClient(context, settings))
            {
                return Unauthorized();
            }

            if (!PolicyValidation.TryValidateConfidenceThreshold(request.Value, out var error))
            {
                return Results.BadRequest(new { error });
            }

            try
            {
                await authority.SetConfidenceThresholdAsync(product, request.Value, cancellationToken);
            }
            catch (ProductNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
            catch (ConfigurationAuthorityUnavailableException exception)
            {
                logger.LogError(exception, "failed to write confidence threshold through configuration authority");
                return AuthorityUnavailableJson(exception);
            }

            logger.LogInformation(
                "api set confidence threshold product={Product} value={Threshold}",
                product,
                request.Value);
            return Results.NoContent();
        });

        return app;
    }

    internal sealed record ThresholdRequest(double Value);

    private static string ProductPath(string product) => $"/products/{Uri.EscapeDataString(product)}";

    private static async Task<IResult> RenderPolicyAsync(
        IProductPolicyAuthority authority,
        string product,
        string csrfToken,
        string? error,
        int statusCode,
        CancellationToken cancellationToken)
    {
        try
        {
            var policy = await authority.ReadPolicyAsync(product, cancellationToken);
            return Html(ControlCenterPages.ProductPolicyPage(policy, csrfToken, error), statusCode);
        }
        catch (ProductNotFoundException exception)
        {
            return Html(
                ControlCenterPages.Message("Product unavailable", exception.Message),
                StatusCodes.Status404NotFound);
        }
        catch (ConfigurationAuthorityUnavailableException exception)
        {
            return AuthorityUnavailable(exception);
        }
    }

    /// <summary>
    /// Rejects a browser write that lacks either factor: an unknown or expired
    /// session cookie is unauthenticated (401), a present session whose CSRF
    /// cookie and form field do not both match the server-side token is a forged
    /// cross-site request (403).
    /// </summary>
    private static bool TryRejectWrite(
        HttpContext context,
        OperatorSessionStore sessions,
        IReadOnlyDictionary<string, string> form,
        out IResult rejection)
    {
        if (!sessions.TryGetCsrfToken(context.Request.Cookies[OperatorSessionStore.SessionCookie], out var expected))
        {
            rejection = SignInRequired();
            return true;
        }

        var cookieToken = context.Request.Cookies[OperatorSessionStore.CsrfCookie];
        var formToken = form.GetValueOrDefault(OperatorSessionStore.CsrfField);
        if (!OperatorSessionStore.TokensMatch(cookieToken, expected)
            || !OperatorSessionStore.TokensMatch(formToken, expected))
        {
            rejection = Html(
                ControlCenterPages.Message(
                    "Request blocked",
                    "This request did not carry a valid CSRF token. Reload the page and try again."),
                StatusCodes.Status403Forbidden);
            return true;
        }

        rejection = Results.Empty;
        return false;
    }

    private static bool TryAuthorizeSession(HttpContext context, OperatorSessionStore sessions, out string csrfToken) =>
        sessions.TryGetCsrfToken(context.Request.Cookies[OperatorSessionStore.SessionCookie], out csrfToken);

    private static bool IsAuthorizedApiClient(HttpContext context, ControlCenterSettings settings)
    {
        var header = context.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        return header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            && OperatorSessionStore.TokensMatch(header[scheme.Length..].Trim(), settings.OperatorToken);
    }

    private static CookieOptions CookieOptions(ControlCenterSettings settings) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = settings.RequireSecureCookies,
        Path = "/",
        IsEssential = true,
    };

    private static async Task<IReadOnlyDictionary<string, string>> ReadFormAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        return form.ToDictionary(entry => entry.Key, entry => entry.Value.ToString(), StringComparer.Ordinal);
    }

    private static bool IsTruthy(string? value) =>
        value?.Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";

    private static IResult SignInRequired() =>
        Html(ControlCenterPages.SignIn("Sign in to govern factory products."), StatusCodes.Status401Unauthorized);

    private static IResult Unauthorized() =>
        Results.Json(new { error = "a bearer token is required" }, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult AuthorityUnavailable(ConfigurationAuthorityUnavailableException exception) =>
        Html(
            ControlCenterPages.Message("Configuration authority unavailable", exception.Message),
            StatusCodes.Status502BadGateway);

    private static IResult AuthorityUnavailableJson(ConfigurationAuthorityUnavailableException exception) =>
        Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status502BadGateway);

    private static IResult Html(string html, int statusCode = StatusCodes.Status200OK) =>
        Results.Content(html, HtmlContentType, Encoding.UTF8, statusCode);

    private static IResult SeeOther(string location) => new SeeOtherResult(location);

    private sealed class SeeOtherResult(string location) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
            httpContext.Response.Headers.Location = location;
            return Task.CompletedTask;
        }
    }
}
