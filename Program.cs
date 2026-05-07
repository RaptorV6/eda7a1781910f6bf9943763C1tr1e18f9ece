using Microsoft.AspNetCore.Server.IISIntegration;
using Microsoft.Extensions.Caching.Memory;
using CitrixComponent.Models;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<CitrixSessionCache>();
builder.Services.AddAuthentication(IISDefaults.AuthenticationScheme);
builder.Services.AddAuthorization();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

app.MapPost("/api/citrix-diagnostics/client-log", (
    CitrixClientLogEntry entry,
    HttpContext httpContext,
    ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("CitrixClientDiagnostics");

    logger.LogInformation(
        "Citrix client log received. RequestId: {RequestId}. Level: {Level}. Message: {Message}. Step: {Step}. BrowserTimestamp: {BrowserTimestamp}. PagePath: {PagePath}. UserAgent: {UserAgent}",
        entry.RequestId,
        entry.Level,
        entry.Message,
        entry.Step,
        entry.BrowserTimestamp,
        entry.PagePath,
        httpContext.Request.Headers.UserAgent.ToString());

    return Results.Ok(new
    {
        received = true,
        requestId = entry.RequestId,
        serverTimestamp = DateTimeOffset.UtcNow
    });
});

app.MapPost("/api/citrix-diagnostics/server-probe", async (
    CitrixProbeRequest probeRequest,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("CitrixServerProbe");
    if (!Uri.TryCreate(probeRequest.Url, UriKind.Absolute, out var requestUri))
    {
        return Results.Ok(new CitrixProbeResponse
        {
            Ok = false,
            RequestId = probeRequest.RequestId,
            Step = probeRequest.Step,
            ErrorType = "InvalidUrl",
            ErrorMessage = $"Probe URL není validní absolutní URI: {probeRequest.Url}"
        });
    }

    var storeRootCandidate = string.IsNullOrWhiteSpace(probeRequest.StoreRootUrl)
        ? new Uri(requestUri, ".").ToString()
        : probeRequest.StoreRootUrl;

    if (!Uri.TryCreate(storeRootCandidate, UriKind.Absolute, out var storeRootUri))
    {
        return Results.Ok(new CitrixProbeResponse
        {
            Ok = false,
            RequestId = probeRequest.RequestId,
            Step = probeRequest.Step,
            ErrorType = "InvalidStoreRootUrl",
            ErrorMessage = $"Store root URL není validní absolutní URI: {storeRootCandidate}"
        });
    }

    using var handler = new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = true,
        CookieContainer = new CookieContainer(),
        AutomaticDecompression = DecompressionMethods.All
    };

    using var client = new HttpClient(handler)
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    var bootstrapCookieNames = Array.Empty<string>();
    string? bootstrapCsrfToken = null;
    HttpStatusCode? bootstrapStatusCode = null;
    string bootstrapReasonPhrase = string.Empty;
    string bootstrapFinalUrl = string.Empty;
    Dictionary<string, string> bootstrapHeaders = [];
    string bootstrapBodyPreview = string.Empty;

    logger.LogInformation(
        "Citrix server probe started. RequestId: {RequestId}. Step: {Step}. Method: {Method}. Url: {Url}. StoreRootUrl: {StoreRootUrl}. HeaderCount: {HeaderCount}",
        probeRequest.RequestId,
        probeRequest.Step,
        probeRequest.Method,
        probeRequest.Url,
        storeRootUri,
        probeRequest.Headers.Count);

    try
    {
        using (var bootstrapRequest = new HttpRequestMessage(HttpMethod.Get, storeRootUri))
        using (var bootstrapResponse = await client.SendAsync(bootstrapRequest, cancellationToken))
        {
            bootstrapStatusCode = bootstrapResponse.StatusCode;
            bootstrapReasonPhrase = bootstrapResponse.ReasonPhrase ?? string.Empty;
            bootstrapFinalUrl = bootstrapResponse.RequestMessage?.RequestUri?.ToString() ?? storeRootUri.ToString();
            var bootstrapBody = await bootstrapResponse.Content.ReadAsStringAsync(cancellationToken);
            bootstrapHeaders = bootstrapResponse.Headers
                .Concat(bootstrapResponse.Content.Headers)
                .ToDictionary(header => header.Key, header => string.Join("; ", header.Value));
            bootstrapBodyPreview = bootstrapBody.Length > 1200 ? bootstrapBody[..1200] + "... [zkráceno]" : bootstrapBody;

            var bootstrapCookies = handler.CookieContainer.GetCookies(storeRootUri).Cast<Cookie>().ToArray();
            bootstrapCookieNames = bootstrapCookies.Select(cookie => cookie.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            bootstrapCsrfToken = bootstrapCookies
                .FirstOrDefault(cookie => string.Equals(cookie.Name, "CsrfToken", StringComparison.OrdinalIgnoreCase))
                ?.Value;

            logger.LogInformation(
                "Citrix bootstrap completed. RequestId: {RequestId}. Step: {Step}. StatusCode: {StatusCode}. ReasonPhrase: {ReasonPhrase}. FinalUrl: {FinalUrl}. Cookies: {Cookies}. CsrfTokenFound: {CsrfTokenFound}",
                probeRequest.RequestId,
                probeRequest.Step,
                (int)bootstrapResponse.StatusCode,
                bootstrapReasonPhrase,
                bootstrapFinalUrl,
                string.Join(", ", bootstrapCookieNames),
                !string.IsNullOrWhiteSpace(bootstrapCsrfToken));
        }

        using var requestMessage = new HttpRequestMessage(new HttpMethod(probeRequest.Method), requestUri);
        var requestHeaders = new Dictionary<string, string>(probeRequest.Headers, StringComparer.OrdinalIgnoreCase);

        if (!requestHeaders.ContainsKey("X-Requested-With"))
        {
            requestHeaders["X-Requested-With"] = "XMLHttpRequest";
        }

        if (!requestHeaders.ContainsKey("Citrix-TransactionId"))
        {
            requestHeaders["Citrix-TransactionId"] = Guid.NewGuid().ToString();
        }

        if (!requestHeaders.ContainsKey("Origin"))
        {
            requestHeaders["Origin"] = $"{requestUri.Scheme}://{requestUri.Authority}";
        }

        if (!requestHeaders.ContainsKey("Referer"))
        {
            requestHeaders["Referer"] = storeRootUri.ToString();
        }

        if (!requestHeaders.ContainsKey("Csrf-Token") && !string.IsNullOrWhiteSpace(bootstrapCsrfToken))
        {
            requestHeaders["Csrf-Token"] = bootstrapCsrfToken;
        }

        var bodyValue = probeRequest.Body ?? string.Empty;
        var contentType = probeRequest.ContentType;

        if ((probeRequest.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                || probeRequest.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase)
                || probeRequest.Method.Equals("PATCH", StringComparison.OrdinalIgnoreCase))
            && requestMessage.Content is null)
        {
            requestMessage.Content = string.IsNullOrWhiteSpace(contentType)
                ? new StringContent(bodyValue, Encoding.UTF8)
                : new StringContent(bodyValue, Encoding.UTF8, contentType);
        }

        foreach (var header in requestHeaders)
        {
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                requestMessage.Content ??= new StringContent(bodyValue, Encoding.UTF8);
                requestMessage.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        using var response = await client.SendAsync(requestMessage, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var responseHeaders = response.Headers
            .Concat(response.Content.Headers)
            .ToDictionary(header => header.Key, header => string.Join("; ", header.Value));

        logger.LogInformation(
            "Citrix server probe finished. RequestId: {RequestId}. Step: {Step}. StatusCode: {StatusCode}. ReasonPhrase: {ReasonPhrase}. ContentType: {ContentType}. BodyPreview: {BodyPreview}",
            probeRequest.RequestId,
            probeRequest.Step,
            (int)response.StatusCode,
            response.ReasonPhrase,
            response.Content.Headers.ContentType?.ToString(),
            responseBody.Length > 1200 ? responseBody[..1200] + "... [zkráceno]" : responseBody);

        return Results.Ok(new CitrixProbeResponse
        {
            Ok = true,
            RequestId = probeRequest.RequestId,
            Step = probeRequest.Step,
            StatusCode = (int)response.StatusCode,
            ReasonPhrase = response.ReasonPhrase ?? string.Empty,
            FinalUrl = response.RequestMessage?.RequestUri?.ToString() ?? probeRequest.Url,
            ContentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty,
            Headers = responseHeaders,
            BootstrapStatusCode = bootstrapStatusCode is null ? null : (int)bootstrapStatusCode.Value,
            BootstrapReasonPhrase = bootstrapReasonPhrase,
            BootstrapFinalUrl = bootstrapFinalUrl,
            BootstrapCookieNames = bootstrapCookieNames,
            BootstrapCsrfTokenFound = !string.IsNullOrWhiteSpace(bootstrapCsrfToken),
            BootstrapHeaders = bootstrapHeaders,
            BootstrapBodyPreview = bootstrapBodyPreview,
            BodyPreview = responseBody.Length > 1200 ? responseBody[..1200] + "... [zkráceno]" : responseBody
        });
    }
    catch (Exception exception)
    {
        logger.LogError(
            exception,
            "Citrix server probe failed. RequestId: {RequestId}. Step: {Step}. Method: {Method}. Url: {Url}",
            probeRequest.RequestId,
            probeRequest.Step,
            probeRequest.Method,
            probeRequest.Url);

        return Results.Ok(new CitrixProbeResponse
        {
            Ok = false,
            RequestId = probeRequest.RequestId,
            Step = probeRequest.Step,
            BootstrapStatusCode = bootstrapStatusCode is null ? null : (int)bootstrapStatusCode.Value,
            BootstrapReasonPhrase = bootstrapReasonPhrase,
            BootstrapFinalUrl = bootstrapFinalUrl,
            BootstrapCookieNames = bootstrapCookieNames,
            BootstrapCsrfTokenFound = !string.IsNullOrWhiteSpace(bootstrapCsrfToken),
            BootstrapHeaders = bootstrapHeaders,
            BootstrapBodyPreview = bootstrapBodyPreview,
            ErrorType = exception.GetType().FullName ?? exception.GetType().Name,
            ErrorMessage = exception.Message,
            InnerErrorMessage = exception.InnerException?.Message ?? string.Empty
        });
    }
});

// Returns StoreFront launch status JSON (fileFetchUrl, fileFetchTicket, serverProtocolVersion, ttl).
// Browser then uses these to construct a receiver:// URL that hands off to Citrix Workspace App
// (or Citrix Secure Access Client / nglauncher.exe) via OS protocol handler — silent launch, no
// ICA download visible. Format mirrors the official StoreFront SPA flow.
app.MapPost("/api/citrix-launch-status", async (
    string session,
    string resourceId,
    HttpContext httpContext,
    IConfiguration configuration,
    ILoggerFactory loggerFactory,
    CitrixSessionCache sessionCache,
    CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("CitrixLaunchStatus");

    if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(resourceId))
    {
        return Results.BadRequest(new { error = "Missing session or resourceId." });
    }

    var entry = sessionCache.Get(session);
    if (entry is null)
    {
        return Results.StatusCode(StatusCodes.Status401Unauthorized);
    }

    if (!System.Text.RegularExpressions.Regex.IsMatch(resourceId, "^[A-Za-z0-9_-]+$"))
    {
        return Results.BadRequest(new { error = "Invalid resourceId format." });
    }

    var statusUri = new Uri(entry.StoreRootUri, $"Resources/GetLaunchStatus/{resourceId}");

    using var handler = new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = true,
        CookieContainer = entry.Cookies,
        AutomaticDecompression = DecompressionMethods.All
    };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

    var httpsHeaderValue = string.Equals(entry.StoreRootUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "Yes" : "No";
    var csrfToken = CitrixExplicitAuth.GetCookieValue(entry.Cookies, entry.StoreRootUri, "CsrfToken");
    var headers = CitrixExplicitAuth.CreateBaseHeaders(
        entry.StoreRootUri, statusUri, httpsHeaderValue, csrfToken,
        httpContext.Request.Headers.AcceptLanguage.ToString(),
        httpContext.Request.Headers.UserAgent.ToString());
    headers["Accept"] = "application/json, text/javascript, */*; q=0.01";

    using var request = CitrixExplicitAuth.CreateRequest(
        HttpMethod.Post, statusUri, headers,
        "createFileFetchTicket=true",
        "application/x-www-form-urlencoded; charset=UTF-8");
    using var response = await client.SendAsync(request, cancellationToken);

    var bodyText = await response.Content.ReadAsStringAsync(cancellationToken);

    // Rewrite fileFetchUrl from internal StoreFront host (citrixvpx01.fis.acr) to public gateway
    // (pnagent.fis.acr). The browser-side receiver:// URL uses fileFetchUrl as the host that
    // Citrix Workspace App will fetch the ICA from. Workspace App on the client typically can ONLY
    // reach the public gateway; the internal StoreFront host is server-side only.
    var publicGatewayHost = configuration["CitrixDiagnostics:PublicGatewayHost"];
    var publicStorePath = configuration["CitrixDiagnostics:PublicStorePath"];
    if (!string.IsNullOrWhiteSpace(publicGatewayHost) && response.IsSuccessStatusCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            if (doc.RootElement.TryGetProperty("fileFetchUrl", out var fetchUrlEl) && fetchUrlEl.ValueKind == JsonValueKind.String)
            {
                var original = fetchUrlEl.GetString();
                if (Uri.TryCreate(original, UriKind.Absolute, out var originalUri))
                {
                    var rewrittenPath = !string.IsNullOrWhiteSpace(publicStorePath)
                        ? publicStorePath.TrimEnd('/') + "/clientAssistant/getIcaFile"
                        : originalUri.AbsolutePath;
                    var rewritten = $"https://{publicGatewayHost}{rewrittenPath}";

                    var dict = new Dictionary<string, object?>();
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        dict[prop.Name] = prop.Name == "fileFetchUrl"
                            ? rewritten
                            : JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
                    }
                    bodyText = JsonSerializer.Serialize(dict);

                    logger.LogInformation(
                        "Rewrote fileFetchUrl from {Original} to {Rewritten}",
                        original, rewritten);
                }
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse GetLaunchStatus JSON for fileFetchUrl rewrite. Returning original.");
        }
    }

    logger.LogInformation(
        "Citrix launch status. Session: {Session}. ResourceId: {ResourceId}. StatusCode: {StatusCode}",
        session, resourceId, (int)response.StatusCode);

    return Results.Content(bodyText, "application/json", statusCode: (int)response.StatusCode);
});

// Generic authenticated proxy to StoreFront. Used for ICA download (app launch) and icon fetch.
// Browser holds opaque session token; cookies stay server-side. Path is constrained to Resources/* to prevent SSRF.
app.MapGet("/api/citrix-proxy", async (
    string session,
    string path,
    HttpContext httpContext,
    ILoggerFactory loggerFactory,
    CitrixSessionCache sessionCache,
    CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("CitrixProxy");

    if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(path))
    {
        return Results.BadRequest(new { error = "Missing session or path." });
    }

    // Anti-SSRF: only allow relative paths under Resources/ (icons, launch, status).
    // Reject absolute URIs, parent traversal, anything outside the auth scope.
    if (path.StartsWith('/') || path.Contains("..") || path.Contains("://"))
    {
        return Results.BadRequest(new { error = "Invalid path." });
    }
    if (!path.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "Path must start with Resources/." });
    }

    var entry = sessionCache.Get(session);
    if (entry is null)
    {
        return Results.StatusCode(StatusCodes.Status401Unauthorized);
    }

    if (!Uri.TryCreate(entry.StoreRootUri, path, out var targetUri))
    {
        return Results.BadRequest(new { error = "Path resolves to invalid URI." });
    }

    using var handler = new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = true,
        CookieContainer = entry.Cookies,
        AutomaticDecompression = DecompressionMethods.All
    };
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

    var httpsHeaderValue = string.Equals(entry.StoreRootUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "Yes" : "No";
    var csrfToken = CitrixExplicitAuth.GetCookieValue(entry.Cookies, entry.StoreRootUri, "CsrfToken");
    var headers = CitrixExplicitAuth.CreateBaseHeaders(
        entry.StoreRootUri, targetUri, httpsHeaderValue, csrfToken,
        httpContext.Request.Headers.AcceptLanguage.ToString(),
        httpContext.Request.Headers.UserAgent.ToString());

    using var request = CitrixExplicitAuth.CreateRequest(HttpMethod.Get, targetUri, headers);
    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    logger.LogInformation(
        "Citrix proxy. Session: {Session}. Path: {Path}. StatusCode: {StatusCode}. ContentType: {ContentType}",
        session, path, (int)response.StatusCode, response.Content.Headers.ContentType?.ToString());

    var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

    // ICA: send only application/x-ica MIME, NO Content-Disposition. Browser then routes via MIME
    // association → Citrix Workspace App handler launches directly without showing download bar.
    // This mirrors how official StoreFront delivers ICA on machines with Workspace App installed.
    var isIca = path.Contains("LaunchIca", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ica", StringComparison.OrdinalIgnoreCase);
    if (isIca)
    {
        contentType = "application/x-ica";
        // Explicitly remove any Content-Disposition that may have been forwarded from upstream.
        httpContext.Response.Headers.Remove("Content-Disposition");
    }

    httpContext.Response.StatusCode = (int)response.StatusCode;
    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
    return Results.File(bytes, contentType);
});

app.MapPost("/api/citrix-diagnostics/explicit-login", async (
    CitrixLoginRequest loginRequest,
    HttpContext httpContext,
    ILoggerFactory loggerFactory,
    CitrixSessionCache sessionCache,
    CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("CitrixExplicitLogin");

    if (!Uri.TryCreate(loginRequest.StoreRootUrl, UriKind.Absolute, out var storeRootUri))
    {
        return Results.Ok(new CitrixLoginResponse
        {
            Ok = false,
            RequestId = loginRequest.RequestId,
            ErrorType = "InvalidStoreRootUrl",
            ErrorMessage = $"Store root URL není validní absolutní URI: {loginRequest.StoreRootUrl}"
        });
    }

    if (string.IsNullOrWhiteSpace(loginRequest.Username)
        || string.IsNullOrWhiteSpace(loginRequest.Password)
        || string.IsNullOrWhiteSpace(loginRequest.Domain))
    {
        return Results.Ok(new CitrixLoginResponse
        {
            Ok = false,
            RequestId = loginRequest.RequestId,
            ErrorType = "MissingCredentials",
            ErrorMessage = "Username, password i domain musí být vyplněné."
        });
    }

    using var handler = new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = true,
        CookieContainer = new CookieContainer(),
        AutomaticDecompression = DecompressionMethods.All
    };

    using var client = new HttpClient(handler)
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    var httpsHeaderValue = string.Equals(storeRootUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        ? "Yes"
        : "No";
    var forwardedAcceptLanguage = httpContext.Request.Headers.AcceptLanguage.ToString();
    var forwardedUserAgent = httpContext.Request.Headers.UserAgent.ToString();

    var explicitLoginUri = new Uri(storeRootUri, "ExplicitAuth/Login");
    var loginAttemptUri = new Uri(storeRootUri, "ExplicitAuth/LoginAttempt");
    var resourcesUri = new Uri(storeRootUri, "Resources/List");
    var authMethodsUri = new Uri(storeRootUri, "Authentication/GetAuthMethods");

    HttpStatusCode? bootstrapStatusCode = null;
    HttpStatusCode? bootstrapLandingStatusCode = null;
    HttpStatusCode? authMethodsStatusCode = null;
    HttpStatusCode? loginFormStatusCode = null;
    HttpStatusCode? loginSubmitStatusCode = null;
    HttpStatusCode? resourcesStatusCode = null;

    string bootstrapFinalUrl = string.Empty;
    string bootstrapRedirectUrl = string.Empty;
    Dictionary<string, string> bootstrapHeaders = [];
    string bootstrapBodyPreview = string.Empty;
    string bootstrapLandingPreview = string.Empty;
    string authMethodsPreview = string.Empty;
    string loginFormPreview = string.Empty;
    string loginSubmitPreview = string.Empty;
    string resourcesPreview = string.Empty;
    string authResult = string.Empty;
    string loginErrorText = string.Empty;
    string loginFormUrl = string.Empty;
    string loginPostUrl = string.Empty;
    var authMethodCandidates = new List<string>();
    var loginAttemptResults = new List<string>();

    logger.LogInformation(
        "Citrix explicit login started. RequestId: {RequestId}. StoreRootUrl: {StoreRootUrl}. Username: {Username}. Domain: {Domain}",
        loginRequest.RequestId,
        storeRootUri,
        loginRequest.Username,
        loginRequest.Domain);

    try
    {
        using (var bootstrapRequest = CitrixExplicitAuth.CreateRequest(
            HttpMethod.Get,
            storeRootUri,
            CitrixExplicitAuth.CreatePageHeaders(storeRootUri, forwardedAcceptLanguage, forwardedUserAgent)))
        using (var bootstrapResponse = await client.SendAsync(bootstrapRequest, cancellationToken))
        {
            bootstrapStatusCode = bootstrapResponse.StatusCode;
            var bootstrapBody = await bootstrapResponse.Content.ReadAsStringAsync(cancellationToken);
            bootstrapHeaders = bootstrapResponse.Headers
                .Concat(bootstrapResponse.Content.Headers)
                .ToDictionary(header => header.Key, header => string.Join("; ", header.Value));
            bootstrapBodyPreview = CitrixExplicitAuth.Preview(bootstrapBody);

            var currentBootstrapUri = bootstrapResponse.RequestMessage?.RequestUri ?? storeRootUri;
            bootstrapFinalUrl = currentBootstrapUri.ToString();
            bootstrapLandingStatusCode = bootstrapResponse.StatusCode;
            bootstrapLandingPreview = bootstrapBodyPreview;

            var redirectLocation = bootstrapResponse.Headers.Location;
            if (redirectLocation is not null)
            {
                var nextBootstrapUri = redirectLocation.IsAbsoluteUri
                    ? redirectLocation
                    : new Uri(currentBootstrapUri, redirectLocation);
                bootstrapRedirectUrl = nextBootstrapUri.ToString();

                for (var redirectHop = 0; redirectHop < 5; redirectHop++)
                {
                    using var landingRequest = CitrixExplicitAuth.CreateRequest(
                        HttpMethod.Get,
                        nextBootstrapUri,
                        CitrixExplicitAuth.CreatePageHeaders(storeRootUri, forwardedAcceptLanguage, forwardedUserAgent));
                    using var landingResponse = await client.SendAsync(landingRequest, cancellationToken);
                    var landingBody = await landingResponse.Content.ReadAsStringAsync(cancellationToken);

                    bootstrapLandingStatusCode = landingResponse.StatusCode;
                    bootstrapFinalUrl = landingResponse.RequestMessage?.RequestUri?.ToString() ?? nextBootstrapUri.ToString();
                    bootstrapLandingPreview = CitrixExplicitAuth.Preview(landingBody);

                    if ((int)landingResponse.StatusCode is < 300 or >= 400 || landingResponse.Headers.Location is null)
                    {
                        break;
                    }

                    nextBootstrapUri = landingResponse.Headers.Location.IsAbsoluteUri
                        ? landingResponse.Headers.Location
                        : new Uri(nextBootstrapUri, landingResponse.Headers.Location);
                }
            }
        }

        // Follow HTML meta-refresh if present (e.g. /cgi/setclient?wica → /Citrix/FISWeb)
        // NetScaler returns 200 with <META HTTP-EQUIV="REFRESH"> instead of a 3xx HTTP redirect.
        var bootstrapMetaRefreshTarget = CitrixExplicitAuth.TryExtractMetaRefreshUrl(bootstrapLandingPreview);
        if (!string.IsNullOrWhiteSpace(bootstrapMetaRefreshTarget)
            && Uri.TryCreate(bootstrapFinalUrl.Length > 0 ? bootstrapFinalUrl : storeRootUri.ToString(), UriKind.Absolute, out var bootstrapFinalBase)
            && Uri.TryCreate(bootstrapFinalBase, bootstrapMetaRefreshTarget, out var metaRefreshTargetUri))
        {
            logger.LogInformation(
                "Citrix bootstrap: following HTML meta-refresh. RequestId: {RequestId}. MetaRefreshUrl: {MetaRefreshUrl}",
                loginRequest.RequestId, metaRefreshTargetUri);

            // Follow HTTP redirects after meta-refresh (e.g. /Citrix/FISWeb → 301 → /Citrix/FISWeb/)
            var metaCurrentUri = metaRefreshTargetUri;
            for (var metaHop = 0; metaHop < 5; metaHop++)
            {
                using var metaRequest = CitrixExplicitAuth.CreateRequest(
                    HttpMethod.Get, metaCurrentUri,
                    CitrixExplicitAuth.CreatePageHeaders(storeRootUri, forwardedAcceptLanguage, forwardedUserAgent));
                using var metaResponse = await client.SendAsync(metaRequest, cancellationToken);
                var metaBody = await metaResponse.Content.ReadAsStringAsync(cancellationToken);

                bootstrapLandingStatusCode = metaResponse.StatusCode;
                bootstrapFinalUrl = metaResponse.RequestMessage?.RequestUri?.ToString() ?? metaCurrentUri.ToString();
                bootstrapLandingPreview = CitrixExplicitAuth.Preview(metaBody);

                logger.LogInformation(
                    "Citrix bootstrap: meta-refresh hop {Hop}. RequestId: {RequestId}. StatusCode: {StatusCode}. Url: {Url}. Cookies: {Cookies}",
                    metaHop + 1,
                    loginRequest.RequestId,
                    (int)metaResponse.StatusCode,
                    bootstrapFinalUrl,
                    string.Join(", ", CitrixExplicitAuth.GetCookieNames(handler.CookieContainer, storeRootUri)));

                if ((int)metaResponse.StatusCode is < 300 or >= 400 || metaResponse.Headers.Location is null)
                {
                    break;
                }

                metaCurrentUri = metaResponse.Headers.Location.IsAbsoluteUri
                    ? metaResponse.Headers.Location
                    : new Uri(metaCurrentUri, metaResponse.Headers.Location);
            }
        }

        var authMethodsHeaders = CitrixExplicitAuth.CreateBaseHeaders(storeRootUri, authMethodsUri, httpsHeaderValue, acceptLanguage: forwardedAcceptLanguage, userAgent: forwardedUserAgent);
        authMethodsHeaders["X-Citrix-AM-CredentialTypes"] = CitrixExplicitAuth.FormCredentialTypes;
        authMethodsHeaders["X-Citrix-AM-LabelTypes"] = CitrixExplicitAuth.FormLabelTypes;

        foreach (var authMethodsMethod in new[] { HttpMethod.Post, HttpMethod.Get })
        {
            try
            {
                using var authMethodsRequest = CitrixExplicitAuth.CreateRequest(
                    authMethodsMethod,
                    authMethodsUri,
                    authMethodsHeaders,
                    authMethodsMethod == HttpMethod.Post ? string.Empty : null,
                    authMethodsMethod == HttpMethod.Post ? "application/x-www-form-urlencoded; charset=UTF-8" : null);
                using var authMethodsResponse = await client.SendAsync(authMethodsRequest, cancellationToken);
                authMethodsStatusCode = authMethodsResponse.StatusCode;
                var authMethodsBody = await authMethodsResponse.Content.ReadAsStringAsync(cancellationToken);
                authMethodsPreview = CitrixExplicitAuth.Preview(authMethodsBody);

                foreach (var candidate in CitrixExplicitAuth.TryParseAuthMethodUris(authMethodsBody, storeRootUri))
                {
                    authMethodCandidates.Add(candidate.ToString());
                }

                if (authMethodsResponse.IsSuccessStatusCode)
                {
                    break;
                }
            }
            catch
            {
                // Keep going with fallbacks; the detailed failure is surfaced through the final response.
            }
        }

        var currentCsrfToken = CitrixExplicitAuth.GetCookieValue(handler.CookieContainer, storeRootUri, "CsrfToken");
        if (string.IsNullOrWhiteSpace(currentCsrfToken))
        {
            return Results.Ok(new CitrixLoginResponse
            {
                Ok = false,
                RequestId = loginRequest.RequestId,
                BootstrapStatusCode = bootstrapStatusCode is null ? null : (int)bootstrapStatusCode.Value,
                BootstrapLandingStatusCode = bootstrapLandingStatusCode is null ? null : (int)bootstrapLandingStatusCode.Value,
                AuthMethodsStatusCode = authMethodsStatusCode is null ? null : (int)authMethodsStatusCode.Value,
                BootstrapFinalUrl = bootstrapFinalUrl,
                BootstrapRedirectUrl = bootstrapRedirectUrl,
                AuthMethodsUrl = authMethodsUri.ToString(),
                AuthMethodCandidates = authMethodCandidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                LoginFormUrl = explicitLoginUri.ToString(),
                LoginPostUrl = loginAttemptUri.ToString(),
                BootstrapHeaders = bootstrapHeaders,
                BootstrapBodyPreview = bootstrapBodyPreview,
                BootstrapLandingPreview = bootstrapLandingPreview,
                AuthMethodsPreview = authMethodsPreview,
                CookieNames = CitrixExplicitAuth.GetCookieNames(handler.CookieContainer, storeRootUri),
                CsrfTokenFound = false,
                ErrorType = "CsrfTokenMissing",
                ErrorMessage = "Po bootstrapu nebyl nalezen cookie CsrfToken, takže nejde bezpečně odeslat LoginAttempt."
            });
        }

        loginFormUrl = explicitLoginUri.ToString();
        loginPostUrl = loginAttemptUri.ToString();

        // Step 1: GET ExplicitAuth/Login to obtain StateContext.
        // Step 1: GET or POST ExplicitAuth/Login to obtain the auth form definition (StateContext,
        // field IDs, PostBack URL). On this StoreFront deployment IIS rejects GET with 404, so try
        // POST first and fall back to GET for other environments.
        CitrixAuthFormDefinition? parsedLoginForm = null;
        foreach (var loginFormMethod in new[] { HttpMethod.Post, HttpMethod.Get })
        {
            var loginFormHeaders = CitrixExplicitAuth.CreateBaseHeaders(storeRootUri, explicitLoginUri, httpsHeaderValue, currentCsrfToken, forwardedAcceptLanguage, forwardedUserAgent);
            loginFormHeaders["X-Citrix-AM-CredentialTypes"] = CitrixExplicitAuth.FormCredentialTypes;
            loginFormHeaders["X-Citrix-AM-LabelTypes"] = CitrixExplicitAuth.FormLabelTypes;

            using var loginFormRequest = CitrixExplicitAuth.CreateRequest(
                loginFormMethod, explicitLoginUri, loginFormHeaders,
                loginFormMethod == HttpMethod.Post ? string.Empty : null,
                loginFormMethod == HttpMethod.Post ? "application/x-www-form-urlencoded; charset=UTF-8" : null);
            using var loginFormResponse = await client.SendAsync(loginFormRequest, cancellationToken);

            loginFormStatusCode = loginFormResponse.StatusCode;
            var loginFormBodyRaw = await loginFormResponse.Content.ReadAsStringAsync(cancellationToken);
            loginFormPreview = CitrixExplicitAuth.Preview(loginFormBodyRaw);
            loginFormUrl = loginFormResponse.RequestMessage?.RequestUri?.ToString() ?? explicitLoginUri.ToString();
            parsedLoginForm = CitrixExplicitAuth.TryParseAuthForm(loginFormBodyRaw);

            var refreshedCsrfToken = CitrixExplicitAuth.GetCookieValue(handler.CookieContainer, storeRootUri, "CsrfToken");
            if (!string.IsNullOrWhiteSpace(refreshedCsrfToken))
                currentCsrfToken = refreshedCsrfToken;

            logger.LogInformation(
                "Citrix login form fetched. RequestId: {RequestId}. Method: {Method}. StatusCode: {StatusCode}. HasCredentialInputs: {HasCredentialInputs}. StateContextPresent: {StateContextPresent}. PostBack: {PostBack}",
                loginRequest.RequestId, loginFormMethod, (int)loginFormResponse.StatusCode,
                parsedLoginForm?.HasCredentialInputs, !string.IsNullOrWhiteSpace(parsedLoginForm?.StateContext),
                parsedLoginForm?.PostBack ?? "(none)");

            if (loginFormResponse.IsSuccessStatusCode)
                break;
        }

        // Use PostBack URL from form definition if available
        if (parsedLoginForm?.PostBack is { Length: > 0 } postBackRelPath
            && Uri.TryCreate(storeRootUri, postBackRelPath, out var parsedPostBackUri))
        {
            loginAttemptUri = parsedPostBackUri;
            loginPostUrl = loginAttemptUri.ToString();
        }

        // Step 2: POST LoginAttempt with StateContext from form definition
        // Use field IDs from parsed form; fall back to well-known StoreFront defaults
        var usernameField = parsedLoginForm?.UsernameId is { Length: > 0 } uid ? uid : "username";
        var passwordField = parsedLoginForm?.PasswordId is { Length: > 0 } pid ? pid : "password";
        var domainField = parsedLoginForm?.DomainId is { Length: > 0 } did ? did : "domain";
        var stateContextValue = parsedLoginForm?.StateContext ?? string.Empty;

        var loginSubmitHeaders = CitrixExplicitAuth.CreateBaseHeaders(storeRootUri, loginAttemptUri, httpsHeaderValue, currentCsrfToken, forwardedAcceptLanguage, forwardedUserAgent);
        loginSubmitHeaders["X-Citrix-AM-CredentialTypes"] = CitrixExplicitAuth.FormCredentialTypes;
        loginSubmitHeaders["X-Citrix-AM-LabelTypes"] = CitrixExplicitAuth.FormLabelTypes;

        var loginFormPayload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [usernameField] = loginRequest.Username.Trim(),
            [passwordField] = loginRequest.Password,
            ["saveCredentials"] = "false",
            [domainField] = loginRequest.Domain.Trim(),
            ["StateContext"] = stateContextValue
        };

        // HAR: browser sends loginBtn=Přihlásit with empty StateContext and succeeds.
        // Use parsed button if available; fall back to known StoreFront default.
        if (parsedLoginForm?.SubmitButtonId is { Length: > 0 } btnId && parsedLoginForm.SubmitButtonValue is { Length: > 0 } btnVal)
        {
            loginFormPayload[btnId] = btnVal;
        }
        else if (!loginFormPayload.ContainsKey("loginBtn"))
        {
            loginFormPayload["loginBtn"] = "Přihlásit";
        }

        logger.LogInformation(
            "Citrix login attempt. RequestId: {RequestId}. LoginAttemptUri: {LoginAttemptUri}. UsernameField: {UsernameField}. DomainField: {DomainField}. StateContextPresent: {StateContextPresent}",
            loginRequest.RequestId, loginAttemptUri, usernameField, domainField, !string.IsNullOrWhiteSpace(stateContextValue));

        var loginFormBody = await new FormUrlEncodedContent(loginFormPayload).ReadAsStringAsync(cancellationToken);

        using (var loginSubmitRequest = CitrixExplicitAuth.CreateRequest(
            HttpMethod.Post,
            loginAttemptUri,
            loginSubmitHeaders,
            loginFormBody,
            "application/x-www-form-urlencoded; charset=UTF-8"))
        using (var loginSubmitResponse = await client.SendAsync(loginSubmitRequest, cancellationToken))
        {
            loginSubmitStatusCode = loginSubmitResponse.StatusCode;
            var loginSubmitBody = await loginSubmitResponse.Content.ReadAsStringAsync(cancellationToken);
            loginSubmitPreview = CitrixExplicitAuth.Preview(loginSubmitBody);
            loginErrorText = CitrixExplicitAuth.FindAuthMessage(loginSubmitBody);
            authResult = CitrixExplicitAuth.FindElementValue(loginSubmitBody, "Result");
            loginAttemptResults.Add($"POST {loginAttemptUri} => {(int)loginSubmitResponse.StatusCode} result={authResult ?? string.Empty}".TrimEnd());
        }

        var cookieNames = CitrixExplicitAuth.GetCookieNames(handler.CookieContainer, storeRootUri);
        currentCsrfToken = CitrixExplicitAuth.GetCookieValue(handler.CookieContainer, storeRootUri, "CsrfToken");
        var csrfTokenFound = !string.IsNullOrWhiteSpace(currentCsrfToken);

        if (!string.Equals(authResult, "success", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new CitrixLoginResponse
            {
                Ok = true,
                RequestId = loginRequest.RequestId,
                LoginSucceeded = false,
                Message = string.IsNullOrWhiteSpace(loginErrorText)
                    ? "Citrix login nevrátil success. Zkontrolujte credentials nebo další auth krok."
                    : $"Citrix login nevrátil success: {loginErrorText}",
                AuthResult = authResult ?? string.Empty,
                LoginErrorText = loginErrorText ?? string.Empty,
                BootstrapStatusCode = bootstrapStatusCode is null ? null : (int)bootstrapStatusCode.Value,
                BootstrapLandingStatusCode = bootstrapLandingStatusCode is null ? null : (int)bootstrapLandingStatusCode.Value,
                AuthMethodsStatusCode = authMethodsStatusCode is null ? null : (int)authMethodsStatusCode.Value,
                LoginFormStatusCode = loginFormStatusCode is null ? null : (int)loginFormStatusCode.Value,
                LoginSubmitStatusCode = loginSubmitStatusCode is null ? null : (int)loginSubmitStatusCode.Value,
                BootstrapFinalUrl = bootstrapFinalUrl,
                BootstrapRedirectUrl = bootstrapRedirectUrl,
                AuthMethodsUrl = authMethodsUri.ToString(),
                AuthMethodCandidates = authMethodCandidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                LoginFormUrl = loginFormUrl,
                LoginPostUrl = loginPostUrl,
                LoginAttemptResults = loginAttemptResults.ToArray(),
                CookieNames = cookieNames,
                CsrfTokenFound = csrfTokenFound,
                BootstrapHeaders = bootstrapHeaders,
                BootstrapBodyPreview = bootstrapBodyPreview,
                BootstrapLandingPreview = bootstrapLandingPreview,
                AuthMethodsPreview = authMethodsPreview,
                LoginFormPreview = loginFormPreview,
                LoginSubmitPreview = loginSubmitPreview
            });
        }

        var resourcesHeaders = CitrixExplicitAuth.CreateBaseHeaders(storeRootUri, resourcesUri, httpsHeaderValue, currentCsrfToken, forwardedAcceptLanguage, forwardedUserAgent);

        using (var resourcesRequest = CitrixExplicitAuth.CreateRequest(
            HttpMethod.Post,
            resourcesUri,
            resourcesHeaders,
            "format=json&resourceDetails=Default",
            "application/x-www-form-urlencoded; charset=UTF-8"))
        using (var resourcesResponse = await client.SendAsync(resourcesRequest, cancellationToken))
        {
            resourcesStatusCode = resourcesResponse.StatusCode;
            var resourcesBody = await resourcesResponse.Content.ReadAsStringAsync(cancellationToken);
            resourcesPreview = CitrixExplicitAuth.Preview(resourcesBody);
            var resourcesPayload = CitrixExplicitAuth.TryParseJson(resourcesBody);

            // Cache authenticated session so launch + icon endpoints can reuse cookies without re-login.
            // Token is opaque to the browser; cookies stay server-side.
            var sessionToken = sessionCache.Store(new CitrixSessionEntry
            {
                Cookies = handler.CookieContainer,
                StoreRootUri = storeRootUri,
                CreatedAt = DateTimeOffset.UtcNow
            });

            logger.LogInformation(
                "Citrix session cached. RequestId: {RequestId}. SessionToken: {SessionToken}. StoreRootUri: {StoreRootUri}",
                loginRequest.RequestId, sessionToken, storeRootUri);

            return Results.Ok(new CitrixLoginResponse
            {
                Ok = true,
                RequestId = loginRequest.RequestId,
                LoginSucceeded = true,
                Message = "Citrix explicit login proběhl a server vrátil Resources/List.",
                SessionToken = sessionToken,
                AuthResult = authResult ?? string.Empty,
                LoginErrorText = loginErrorText ?? string.Empty,
                BootstrapStatusCode = bootstrapStatusCode is null ? null : (int)bootstrapStatusCode.Value,
                BootstrapLandingStatusCode = bootstrapLandingStatusCode is null ? null : (int)bootstrapLandingStatusCode.Value,
                AuthMethodsStatusCode = authMethodsStatusCode is null ? null : (int)authMethodsStatusCode.Value,
                LoginFormStatusCode = loginFormStatusCode is null ? null : (int)loginFormStatusCode.Value,
                LoginSubmitStatusCode = loginSubmitStatusCode is null ? null : (int)loginSubmitStatusCode.Value,
                ResourcesStatusCode = resourcesStatusCode is null ? null : (int)resourcesStatusCode.Value,
                BootstrapFinalUrl = bootstrapFinalUrl,
                BootstrapRedirectUrl = bootstrapRedirectUrl,
                AuthMethodsUrl = authMethodsUri.ToString(),
                AuthMethodCandidates = authMethodCandidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                LoginFormUrl = loginFormUrl,
                LoginPostUrl = loginPostUrl,
                ResourcesUrl = resourcesUri.ToString(),
                LoginAttemptResults = loginAttemptResults.ToArray(),
                CookieNames = CitrixExplicitAuth.GetCookieNames(handler.CookieContainer, storeRootUri),
                CsrfTokenFound = !string.IsNullOrWhiteSpace(CitrixExplicitAuth.GetCookieValue(handler.CookieContainer, storeRootUri, "CsrfToken")),
                BootstrapHeaders = bootstrapHeaders,
                BootstrapBodyPreview = bootstrapBodyPreview,
                BootstrapLandingPreview = bootstrapLandingPreview,
                AuthMethodsPreview = authMethodsPreview,
                LoginFormPreview = loginFormPreview,
                LoginSubmitPreview = loginSubmitPreview,
                ResourcesPreview = resourcesPreview,
                ResourcesPayload = resourcesPayload
            });
        }
    }
    catch (Exception exception)
    {
        logger.LogError(
            exception,
            "Citrix explicit login failed. RequestId: {RequestId}. StoreRootUrl: {StoreRootUrl}. Username: {Username}. Domain: {Domain}",
            loginRequest.RequestId,
            loginRequest.StoreRootUrl,
            loginRequest.Username,
            loginRequest.Domain);

        return Results.Ok(new CitrixLoginResponse
        {
            Ok = false,
            RequestId = loginRequest.RequestId,
            AuthResult = authResult,
            LoginErrorText = loginErrorText,
            BootstrapStatusCode = bootstrapStatusCode is null ? null : (int)bootstrapStatusCode.Value,
            BootstrapLandingStatusCode = bootstrapLandingStatusCode is null ? null : (int)bootstrapLandingStatusCode.Value,
            AuthMethodsStatusCode = authMethodsStatusCode is null ? null : (int)authMethodsStatusCode.Value,
            LoginFormStatusCode = loginFormStatusCode is null ? null : (int)loginFormStatusCode.Value,
            LoginSubmitStatusCode = loginSubmitStatusCode is null ? null : (int)loginSubmitStatusCode.Value,
            ResourcesStatusCode = resourcesStatusCode is null ? null : (int)resourcesStatusCode.Value,
            BootstrapFinalUrl = bootstrapFinalUrl,
            BootstrapRedirectUrl = bootstrapRedirectUrl,
            AuthMethodsUrl = authMethodsUri.ToString(),
            AuthMethodCandidates = authMethodCandidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            LoginFormUrl = loginFormUrl,
            LoginPostUrl = loginPostUrl,
            ResourcesUrl = resourcesUri.ToString(),
            LoginAttemptResults = loginAttemptResults.ToArray(),
            BootstrapHeaders = bootstrapHeaders,
            BootstrapBodyPreview = bootstrapBodyPreview,
            BootstrapLandingPreview = bootstrapLandingPreview,
            AuthMethodsPreview = authMethodsPreview,
            LoginFormPreview = loginFormPreview,
            LoginSubmitPreview = loginSubmitPreview,
            ResourcesPreview = resourcesPreview,
            CookieNames = CitrixExplicitAuth.GetCookieNames(handler.CookieContainer, storeRootUri),
            CsrfTokenFound = !string.IsNullOrWhiteSpace(CitrixExplicitAuth.GetCookieValue(handler.CookieContainer, storeRootUri, "CsrfToken")),
            ErrorType = exception.GetType().FullName ?? exception.GetType().Name,
            ErrorMessage = exception.Message,
            InnerErrorMessage = exception.InnerException?.Message ?? string.Empty
        });
    }
});

app.MapGet("/api/whoami", (HttpContext ctx) => Results.Ok(new
{
    authenticated = ctx.User.Identity?.IsAuthenticated ?? false,
    name = ctx.User.Identity?.Name ?? "anonymous",
    authType = ctx.User.Identity?.AuthenticationType ?? "none"
}));

app.MapGet("/api/citrix-sso/test", async (HttpContext ctx, IConfiguration config, ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("CitrixSsoTest");
    var storeRootUrl = config["CitrixDiagnostics:BaseUrl"] ?? "";

    if (ctx.User.Identity is not WindowsIdentity windowsIdentity || !windowsIdentity.IsAuthenticated)
        return Results.Ok(new { ssoResult = "FAIL", reason = "Windows identita není dostupná." });

    var userName = windowsIdentity.Name;
    logger.LogInformation("CitrixSsoTest: user={User}", userName);

    string authMethodsResult;
    try
    {
        // Bootstrap bez credentials (aby NetScaler nevyrušil session creation)
        var sharedCookies = new CookieContainer();
        if (!Uri.TryCreate(storeRootUrl, UriKind.Absolute, out var storeUri))
            return Results.Ok(new { ssoResult = "FAIL", reason = "Chybí nebo neplatná CitrixDiagnostics:BaseUrl." });

        using var bootstrapHandler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = sharedCookies,
            AutomaticDecompression = DecompressionMethods.All
        };
        using var bootstrapClient = new HttpClient(bootstrapHandler) { Timeout = TimeSpan.FromSeconds(15) };

        var currentUrl = storeUri;
        for (int hop = 0; hop < 8; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, currentUrl);
            req.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            req.Headers.Add("Upgrade-Insecure-Requests", "1");
            req.Headers.Add("User-Agent", "Mozilla/5.0");
            using var resp = await bootstrapClient.SendAsync(req);

            if (resp.StatusCode is System.Net.HttpStatusCode.Moved or System.Net.HttpStatusCode.Found or System.Net.HttpStatusCode.SeeOther)
            {
                currentUrl = resp.Headers.Location?.IsAbsoluteUri == true
                    ? resp.Headers.Location
                    : new Uri(storeUri, resp.Headers.Location);
                continue;
            }

            var body = await resp.Content.ReadAsStringAsync();
            var metaMatch = System.Text.RegularExpressions.Regex.Match(body, @"content=""\d+;\s*url=([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (metaMatch.Success)
            {
                var metaUrl = metaMatch.Groups[1].Value;
                currentUrl = Uri.TryCreate(metaUrl, UriKind.Absolute, out var absUri) ? absUri : new Uri(storeUri, metaUrl);
                continue;
            }
            break;
        }

        // Hledej cookies na všech možných cestách
        var checkUris = new[] { storeUri, new Uri(storeUri, "/Citrix/FISWeb/"), new Uri(storeUri, "/"), new Uri($"{storeUri.Scheme}://{storeUri.Host}/") };
        var allCookies = checkUris.SelectMany(u => sharedCookies.GetCookies(u).Cast<Cookie>()).DistinctBy(c => c.Name).ToList();
        var csrf = allCookies.FirstOrDefault(c => c.Name.Equals("CsrfToken", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
        var cookieNames = string.Join(", ", allCookies.Select(c => c.Name));

        // DomainPassthrough s credentials (impersonace uživatele)
        authMethodsResult = await WindowsIdentity.RunImpersonatedAsync(windowsIdentity.AccessToken, async () =>
        {
            using var authHandler = new HttpClientHandler
            {
                UseDefaultCredentials = true,
                AllowAutoRedirect = false,
                UseCookies = true,
                CookieContainer = sharedCookies,
                AutomaticDecompression = DecompressionMethods.All
            };
            using var authClient = new HttpClient(authHandler) { Timeout = TimeSpan.FromSeconds(15) };

            using var dptReq = new HttpRequestMessage(HttpMethod.Post, new Uri(storeUri, "DomainPassthroughAuth/Login"));
            dptReq.Headers.Add("Accept", "application/xml, text/xml, */*");
            dptReq.Headers.Add("X-Requested-With", "XMLHttpRequest");
            dptReq.Headers.Add("X-Citrix-IsUsingHTTPS", storeUri.Scheme == "https" ? "Yes" : "No");
            if (!string.IsNullOrEmpty(csrf)) dptReq.Headers.Add("Csrf-Token", csrf);
            dptReq.Headers.Add("Referer", storeUri.ToString());
            dptReq.Content = new StringContent("", Encoding.UTF8, "application/x-www-form-urlencoded");

            using var dptResp = await authClient.SendAsync(dptReq);
            var dptBody = await dptResp.Content.ReadAsStringAsync();
            var dptPreview = dptBody.Length > 800 ? dptBody[..800] + "..." : dptBody;
            var wwwAuth = dptResp.Headers.WwwAuthenticate.ToString();
            return $"DomainPassthroughAuth/Login → {(int)dptResp.StatusCode} | csrf={!string.IsNullOrEmpty(csrf)} | cookies=[{cookieNames}] | WWW-Auth: {wwwAuth} | Body: {dptPreview}";
        });
    }
    catch (Exception ex)
    {
        authMethodsResult = $"Chyba: {ex.Message}";
    }

    return Results.Ok(new
    {
        windowsUser = userName,
        authMethodsResult
    });
});

app.MapRazorPages()
   .WithStaticAssets();

app.Run();

internal sealed class CitrixSessionEntry
{
    public required CookieContainer Cookies { get; init; }

    public required Uri StoreRootUri { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

internal sealed class CitrixSessionCache(IMemoryCache cache)
{
    // StoreFront default session timeout is 20 min idle. SlidingExpiration keeps it alive while user clicks.
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(20);

    public string Store(CitrixSessionEntry entry)
    {
        var token = Guid.NewGuid().ToString("N");
        cache.Set(CacheKey(token), entry, new MemoryCacheEntryOptions
        {
            SlidingExpiration = SessionTtl
        });
        return token;
    }

    public CitrixSessionEntry? Get(string token) =>
        string.IsNullOrWhiteSpace(token)
            ? null
            : cache.TryGetValue(CacheKey(token), out CitrixSessionEntry? entry)
                ? entry
                : null;

    public void Remove(string token)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            cache.Remove(CacheKey(token));
        }
    }

    private static string CacheKey(string token) => $"citrix-session:{token}";
}

internal sealed class CitrixAuthFormDefinition
{
    public string Result { get; init; } = string.Empty;

    public string PostBack { get; init; } = string.Empty;

    public string StateContext { get; init; } = string.Empty;

    public string UsernameId { get; init; } = string.Empty;

    public string PasswordId { get; init; } = string.Empty;

    public string DomainId { get; init; } = string.Empty;

    public string SubmitButtonId { get; init; } = string.Empty;

    public string SubmitButtonValue { get; init; } = string.Empty;

    public bool HasCredentialInputs =>
        !string.IsNullOrWhiteSpace(PostBack)
        && !string.IsNullOrWhiteSpace(StateContext)
        && !string.IsNullOrWhiteSpace(UsernameId)
        && !string.IsNullOrWhiteSpace(PasswordId)
        && !string.IsNullOrWhiteSpace(SubmitButtonId)
        && !string.IsNullOrWhiteSpace(SubmitButtonValue);
}

internal static class CitrixExplicitAuth
{
    public const string FormCredentialTypes = "none, username, domain, password, newpassword, passcode, savecredentials, textcredential, webview, webview";
    public const string FormLabelTypes = "none, plain, heading, information, warning, error, confirmation, image";

    // Use for page navigation (bootstrap, redirect hops, meta-refresh) — NOT for StoreFront API calls.
    // Omits X-Requested-With and X-Citrix-IsUsingHTTPS so StoreFront treats the request as a real browser
    // page load and creates an ASP.NET session (ASP.NET_SessionId) in its response.
    public static Dictionary<string, string> CreatePageHeaders(
        Uri storeRootUri,
        string? acceptLanguage = null,
        string? userAgent = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8",
            ["Origin"] = $"{storeRootUri.Scheme}://{storeRootUri.Authority}",
            ["Referer"] = storeRootUri.ToString(),
            ["Cache-Control"] = "no-cache",
            ["Pragma"] = "no-cache",
            ["Upgrade-Insecure-Requests"] = "1"
        };

        if (!string.IsNullOrWhiteSpace(acceptLanguage))
            headers["Accept-Language"] = acceptLanguage;
        if (!string.IsNullOrWhiteSpace(userAgent))
            headers["User-Agent"] = userAgent;

        return headers;
    }

    public static Dictionary<string, string> CreateBaseHeaders(
        Uri storeRootUri,
        Uri requestUri,
        string httpsHeaderValue,
        string? csrfToken = null,
        string? acceptLanguage = null,
        string? userAgent = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/xml, text/xml, */*; q=0.01",
            ["Citrix-TransactionId"] = Guid.NewGuid().ToString(),
            ["Origin"] = $"{storeRootUri.Scheme}://{storeRootUri.Authority}",
            ["Referer"] = storeRootUri.ToString(),
            ["Cache-Control"] = "no-cache",
            ["Pragma"] = "no-cache",
            ["X-Citrix-IsUsingHTTPS"] = httpsHeaderValue,
            ["X-Requested-With"] = "XMLHttpRequest"
        };

        if (!string.IsNullOrWhiteSpace(csrfToken))
        {
            headers["Csrf-Token"] = csrfToken;
        }

        if (!string.IsNullOrWhiteSpace(acceptLanguage))
        {
            headers["Accept-Language"] = acceptLanguage;
        }

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            headers["User-Agent"] = userAgent;
        }

        return headers;
    }

    public static HttpRequestMessage CreateRequest(
        HttpMethod method,
        Uri requestUri,
        IReadOnlyDictionary<string, string> headers,
        string? body = null,
        string? contentType = null)
    {
        var request = new HttpRequestMessage(method, requestUri);

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);

            if (!string.IsNullOrWhiteSpace(contentType))
            {
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }
        }

        foreach (var header in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                request.Content ??= new StringContent(string.Empty, Encoding.UTF8);
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return request;
    }

    public static string Preview(string text, int limit = 1200) =>
        text.Length > limit ? text[..limit] + "... [zkráceno]" : text;

    public static string[] GetCookieNames(CookieContainer cookieContainer, Uri uri) =>
        cookieContainer.GetCookies(uri).Cast<Cookie>().Select(cookie => cookie.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string GetCookieValue(CookieContainer cookieContainer, Uri uri, string cookieName) =>
        cookieContainer.GetCookies(uri).Cast<Cookie>()
            .FirstOrDefault(cookie => string.Equals(cookie.Name, cookieName, StringComparison.OrdinalIgnoreCase))
            ?.Value
        ?? string.Empty;

    public static CitrixAuthFormDefinition? TryParseAuthForm(string xmlText)
    {
        try
        {
            var document = XDocument.Parse(xmlText);
            var credentials = document.Descendants().Where(element => element.Name.LocalName == "Credential").ToArray();
            var postBack = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "PostBack")?.Value ?? string.Empty;
            var stateContext = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "StateContext")?.Value ?? string.Empty;
            var result = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Result")?.Value ?? string.Empty;

            if (credentials.Length == 0
                && string.IsNullOrWhiteSpace(postBack)
                && string.IsNullOrWhiteSpace(stateContext)
                && string.IsNullOrWhiteSpace(result))
            {
                return null;
            }

            string FindCredentialId(string typeName) =>
                credentials
                    .FirstOrDefault(credential => string.Equals(
                        credential.Elements().FirstOrDefault(child => child.Name.LocalName == "Type")?.Value,
                        typeName,
                        StringComparison.OrdinalIgnoreCase))
                    ?.Elements()
                    .FirstOrDefault(child => child.Name.LocalName == "ID")
                    ?.Value
                ?? string.Empty;

            var submitCredential = credentials
                .Select(credential => new
                {
                    Id = credential.Elements().FirstOrDefault(child => child.Name.LocalName == "ID")?.Value ?? string.Empty,
                    Value = credential.Descendants().FirstOrDefault(child => child.Name.LocalName == "Button")?.Value ?? string.Empty
                })
                .FirstOrDefault(candidate =>
                    !string.IsNullOrWhiteSpace(candidate.Id)
                    && !string.IsNullOrWhiteSpace(candidate.Value));

            return new CitrixAuthFormDefinition
            {
                Result = result,
                PostBack = postBack,
                StateContext = stateContext,
                UsernameId = FindCredentialId("username"),
                PasswordId = FindCredentialId("password"),
                DomainId = FindCredentialId("domain"),
                SubmitButtonId = submitCredential?.Id ?? string.Empty,
                SubmitButtonValue = submitCredential?.Value ?? string.Empty
            };
        }
        catch
        {
            return null;
        }
    }

    public static JsonElement? TryParseJson(string jsonText)
    {
        try
        {
            using var document = JsonDocument.Parse(jsonText);
            return document.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    public static Uri[] TryParseAuthMethodUris(string body, Uri baseUri)
    {
        var rankedUris = new List<(Uri Uri, int Rank)>();

        void AddCandidate(string? rawValue, string? hint)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return;
            }

            var trimmed = rawValue.Trim();
            if (trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!Uri.TryCreate(baseUri, trimmed, out var resolvedUri))
            {
                return;
            }

            var normalizedHint = hint ?? string.Empty;
            var rank = normalizedHint.Contains("explicit", StringComparison.OrdinalIgnoreCase)
                || normalizedHint.Contains("form", StringComparison.OrdinalIgnoreCase)
                ? 0
                : normalizedHint.Contains("generic", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 2;

            rankedUris.Add((resolvedUri, rank));
        }

        try
        {
            using var jsonDocument = JsonDocument.Parse(body);
            CollectUrisFromJson(jsonDocument.RootElement, AddCandidate);
        }
        catch
        {
            // Response was not JSON; try XML next.
        }

        try
        {
            var xmlDocument = XDocument.Parse(body);
            CollectUrisFromXml(xmlDocument, AddCandidate);
        }
        catch
        {
            // Response was not XML.
        }

        return rankedUris
            .OrderBy(candidate => candidate.Rank)
            .ThenBy(candidate => candidate.Uri.ToString(), StringComparer.OrdinalIgnoreCase)
            .GroupBy(candidate => candidate.Uri.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First().Uri)
            .ToArray();
    }

    public static string FindElementValue(string xmlText, string localName)
    {
        try
        {
            var document = XDocument.Parse(xmlText);
            return document.Descendants().FirstOrDefault(element => element.Name.LocalName == localName)?.Value ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string? TryExtractMetaRefreshUrl(string html)
    {
        // Matches: <META HTTP-EQUIV="REFRESH" CONTENT="0; URL=/path">
        // Attribute order and quoting may vary; use a simple case-insensitive regex.
        var match = System.Text.RegularExpressions.Regex.Match(
            html,
            @"<meta[^>]+http-equiv\s*=\s*[""']?refresh[""']?[^>]+content\s*=\s*[""']?\d+\s*;\s*url\s*=\s*([^""'\s>]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        // Also try attribute order reversed: CONTENT first, then HTTP-EQUIV
        match = System.Text.RegularExpressions.Regex.Match(
            html,
            @"content\s*=\s*[""']?\d+\s*;\s*url\s*=\s*([^""'\s>]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    public static string FindAuthMessage(string xmlText)
    {
        try
        {
            var document = XDocument.Parse(xmlText);

            var errorLabel = document
                .Descendants()
                .Where(element => element.Name.LocalName == "Label")
                .Select(label => new
                {
                    Type = label.Elements().FirstOrDefault(child => child.Name.LocalName == "Type")?.Value ?? string.Empty,
                    Text = label.Elements().FirstOrDefault(child => child.Name.LocalName == "Text")?.Value ?? string.Empty
                })
                .FirstOrDefault(label =>
                    string.Equals(label.Type, "error", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(label.Text));

            if (!string.IsNullOrWhiteSpace(errorLabel?.Text))
            {
                return errorLabel.Text;
            }

            return document.Descendants()
                .FirstOrDefault(element =>
                    string.Equals(element.Name.LocalName, "Message", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(element.Name.LocalName, "Text", StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void CollectUrisFromJson(JsonElement element, Action<string?, string?> addCandidate)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                string? hint = null;
                var urlCandidates = new List<string>();

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString();

                        if (IsAuthHintProperty(property.Name))
                        {
                            hint = value;
                        }

                        if (LooksLikeUrlProperty(property.Name))
                        {
                            urlCandidates.Add(value ?? string.Empty);
                        }
                    }
                }

                foreach (var urlCandidate in urlCandidates)
                {
                    addCandidate(urlCandidate, hint);
                }

                foreach (var property in element.EnumerateObject())
                {
                    CollectUrisFromJson(property.Value, addCandidate);
                }

                break;
            }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectUrisFromJson(item, addCandidate);
                }

                break;
        }
    }

    private static void CollectUrisFromXml(XDocument document, Action<string?, string?> addCandidate)
    {
        foreach (var element in document.Descendants())
        {
            var hint = element.Attributes()
                    .FirstOrDefault(attribute => IsAuthHintProperty(attribute.Name.LocalName))
                    ?.Value
                ?? element.Elements()
                    .FirstOrDefault(child => IsAuthHintProperty(child.Name.LocalName))
                    ?.Value;

            var urlValues = element.Attributes()
                .Where(attribute => LooksLikeUrlProperty(attribute.Name.LocalName))
                .Select(attribute => attribute.Value)
                .Concat(element.Elements()
                    .Where(child => LooksLikeUrlProperty(child.Name.LocalName))
                    .Select(child => child.Value))
                .ToArray();

            foreach (var urlValue in urlValues)
            {
                addCandidate(urlValue, hint);
            }
        }
    }

    private static bool LooksLikeUrlProperty(string propertyName) =>
        propertyName.Equals("url", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("href", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("location", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("address", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("postback", StringComparison.OrdinalIgnoreCase)
        || propertyName.EndsWith("url", StringComparison.OrdinalIgnoreCase);

    private static bool IsAuthHintProperty(string propertyName) =>
        propertyName.Equals("name", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("type", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("id", StringComparison.OrdinalIgnoreCase)
        || propertyName.Equals("label", StringComparison.OrdinalIgnoreCase)
        || propertyName.EndsWith("name", StringComparison.OrdinalIgnoreCase)
        || propertyName.EndsWith("type", StringComparison.OrdinalIgnoreCase);
}
