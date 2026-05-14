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

app.MapPost("/api/citrix-sso/login", async (
    HttpContext httpContext,
    IConfiguration config,
    ILoggerFactory loggerFactory,
    CitrixSessionCache sessionCache,
    CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("CitrixSsoLogin");

    if (httpContext.User.Identity is not WindowsIdentity windowsIdentity || !windowsIdentity.IsAuthenticated)
    {
        return Results.Ok(new CitrixLoginResponse
        {
            Ok = false,
            ErrorType = "WindowsAuthUnavailable",
            ErrorMessage = "Windows identita není dostupná — IIS Windows Authentication není povolena nebo uživatel není autentizován."
        });
    }

    var storeRootUrl = config["CitrixDiagnostics:BaseUrl"] ?? string.Empty;
    if (!Uri.TryCreate(storeRootUrl, UriKind.Absolute, out var storeRootUri))
    {
        return Results.Ok(new CitrixLoginResponse
        {
            Ok = false,
            ErrorType = "InvalidStoreRootUrl",
            ErrorMessage = $"CitrixDiagnostics:BaseUrl není validní URI: {storeRootUrl}"
        });
    }

    var requestId = Guid.NewGuid().ToString("N")[..12];
    var forwardedAcceptLanguage = httpContext.Request.Headers.AcceptLanguage.ToString();
    var forwardedUserAgent = httpContext.Request.Headers.UserAgent.ToString();
    var httpsHeaderValue = string.Equals(storeRootUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "Yes" : "No";

    logger.LogInformation(
        "Citrix SSO login started. RequestId: {RequestId}. WindowsUser: {WindowsUser}. ImpersonationLevel: {ImpersonationLevel}. StoreRootUrl: {StoreRootUrl}",
        requestId, windowsIdentity.Name, windowsIdentity.ImpersonationLevel, storeRootUri);

    var cookies = new CookieContainer();
    var authMethodsUri = new Uri(storeRootUri, "Authentication/GetAuthMethods");
    var dptUri = new Uri(storeRootUri, "DomainPassthroughAuth/Login");
    var resourcesUri = new Uri(storeRootUri, "Resources/List");

    HttpStatusCode? bootstrapStatusCode = null;
    HttpStatusCode? authMethodsStatusCode = null;
    HttpStatusCode? loginSubmitStatusCode = null;
    HttpStatusCode? resourcesStatusCode = null;
    string bootstrapFinalUrl = storeRootUri.ToString();
    string authMethodsPreview = string.Empty;
    string loginSubmitPreview = string.Empty;
    string resourcesPreview = string.Empty;
    string authResult = string.Empty;

    try
    {
        // Step 1+2: Bootstrap + GetAuthMethods — anonymous, just need CsrfToken + ASP.NET session
        using (var anonHandler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = cookies,
            AutomaticDecompression = DecompressionMethods.All
        })
        using (var anonClient = new HttpClient(anonHandler) { Timeout = TimeSpan.FromSeconds(30) })
        {
            using var bootstrapReq = CitrixExplicitAuth.CreateRequest(
                HttpMethod.Get, storeRootUri,
                CitrixExplicitAuth.CreatePageHeaders(storeRootUri, forwardedAcceptLanguage, forwardedUserAgent));
            using var bootstrapResp = await anonClient.SendAsync(bootstrapReq, cancellationToken);
            bootstrapStatusCode = bootstrapResp.StatusCode;
            bootstrapFinalUrl = bootstrapResp.RequestMessage?.RequestUri?.ToString() ?? storeRootUri.ToString();
            _ = await bootstrapResp.Content.ReadAsStringAsync(cancellationToken);

            var amHeaders = CitrixExplicitAuth.CreateBaseHeaders(storeRootUri, authMethodsUri, httpsHeaderValue, acceptLanguage: forwardedAcceptLanguage, userAgent: forwardedUserAgent);
            amHeaders["X-Citrix-AM-CredentialTypes"] = CitrixExplicitAuth.FormCredentialTypes;
            amHeaders["X-Citrix-AM-LabelTypes"] = CitrixExplicitAuth.FormLabelTypes;
            using var amReq = CitrixExplicitAuth.CreateRequest(HttpMethod.Post, authMethodsUri, amHeaders, string.Empty, "application/x-www-form-urlencoded; charset=UTF-8");
            using var amResp = await anonClient.SendAsync(amReq, cancellationToken);
            authMethodsStatusCode = amResp.StatusCode;
            authMethodsPreview = CitrixExplicitAuth.Preview(await amResp.Content.ReadAsStringAsync(cancellationToken));
        }

        var csrf = CitrixExplicitAuth.GetCookieValue(cookies, storeRootUri, "CsrfToken");
        if (string.IsNullOrWhiteSpace(csrf))
        {
            return Results.Ok(new CitrixLoginResponse
            {
                Ok = false,
                RequestId = requestId,
                BootstrapStatusCode = bootstrapStatusCode is null ? null : (int)bootstrapStatusCode.Value,
                AuthMethodsStatusCode = authMethodsStatusCode is null ? null : (int)authMethodsStatusCode.Value,
                BootstrapFinalUrl = bootstrapFinalUrl,
                AuthMethodsPreview = authMethodsPreview,
                CookieNames = CitrixExplicitAuth.GetCookieNames(cookies, storeRootUri),
                CsrfTokenFound = false,
                ErrorType = "CsrfTokenMissing",
                ErrorMessage = "Po bootstrapu nebyl nalezen CsrfToken — StoreFront pravděpodobně nevytvoří session."
            });
        }

        // Step 3: DomainPassthroughAuth/Login.
        // IIS NTLM token (loopback) has ImpersonationLevel=Impersonation — outbound network calls
        // fall back to process identity, not the user. We need S4U2Self to get a Kerberos token.
        // With TrustedToAuthForDelegation set on VXXXX22FISXVI15$ in AD, S4U2Self produces a
        // Delegation-level token that UseDefaultCredentials can forward via RBCD to pnagent.
        var upnFromClaims = windowsIdentity.Claims
            .FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.Upn)?.Value ?? string.Empty;
        string s4uUpn;
        if (!string.IsNullOrWhiteSpace(upnFromClaims))
        {
            s4uUpn = upnFromClaims;
        }
        else
        {
            var domainMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["FIS"] = "fis.acr",
                ["ACR"] = "acr"
            };
            var nameParts = windowsIdentity.Name.Split('\\');
            var netbios = nameParts.Length == 2 ? nameParts[0] : string.Empty;
            var userName = nameParts.Length == 2 ? nameParts[1] : windowsIdentity.Name;
            var fqdn = domainMap.TryGetValue(netbios, out var d) ? d : $"{netbios.ToLower()}.acr";
            s4uUpn = $"{userName}@{fqdn}";
        }

        WindowsIdentity s4uIdentity;
        try
        {
            s4uIdentity = new WindowsIdentity(s4uUpn);
        }
        catch (Exception ex)
        {
            return Results.Ok(new CitrixLoginResponse
            {
                Ok = false,
                RequestId = requestId,
                BootstrapStatusCode = bootstrapStatusCode is null ? null : (int)bootstrapStatusCode.Value,
                AuthMethodsStatusCode = authMethodsStatusCode is null ? null : (int)authMethodsStatusCode.Value,
                BootstrapFinalUrl = bootstrapFinalUrl,
                AuthMethodsPreview = authMethodsPreview,
                CookieNames = CitrixExplicitAuth.GetCookieNames(cookies, storeRootUri),
                CsrfTokenFound = true,
                ErrorType = "S4U2SelfFailed",
                ErrorMessage = $"S4U2Self pro '{s4uUpn}' selhal: {ex.Message}. Zkontrolujte UPN format a TrustedToAuthForDelegation na VXXXX22FISXVI15$.",
                InnerErrorMessage = ex.InnerException?.Message ?? string.Empty
            });
        }

        logger.LogInformation(
            "Citrix SSO S4U2Self. RequestId: {RequestId}. UPN: {UPN}. ImpersonationLevel: {ImpersonationLevel}",
            requestId, s4uUpn, s4uIdentity.ImpersonationLevel);

        await WindowsIdentity.RunImpersonatedAsync(s4uIdentity.AccessToken, async () =>
        {
            using var authHandler = new HttpClientHandler
            {
                UseDefaultCredentials = true,
                UseProxy = false,
                AllowAutoRedirect = false,
                UseCookies = true,
                CookieContainer = cookies,
                AutomaticDecompression = DecompressionMethods.All
            };
            using var authClient = new HttpClient(authHandler) { Timeout = TimeSpan.FromSeconds(30) };

            var dptHeaders = CitrixExplicitAuth.CreateBaseHeaders(storeRootUri, dptUri, httpsHeaderValue, csrf, forwardedAcceptLanguage, forwardedUserAgent);
            using var dptReq = CitrixExplicitAuth.CreateRequest(HttpMethod.Post, dptUri, dptHeaders, string.Empty, "application/x-www-form-urlencoded; charset=UTF-8");
            using var dptResp = await authClient.SendAsync(dptReq, cancellationToken);
            loginSubmitStatusCode = dptResp.StatusCode;
            var dptBody = await dptResp.Content.ReadAsStringAsync(cancellationToken);
            loginSubmitPreview = CitrixExplicitAuth.Preview(dptBody);
            authResult = CitrixExplicitAuth.FindElementValue(dptBody, "Result") ?? string.Empty;

            logger.LogInformation(
                "Citrix SSO DomainPassthroughAuth result. RequestId: {RequestId}. StatusCode: {StatusCode}. AuthResult: {AuthResult}. S4uImpersonationLevel: {ImpersonationLevel}",
                requestId, (int)dptResp.StatusCode, authResult, WindowsIdentity.GetCurrent().ImpersonationLevel);
        });

        var updatedCsrf = CitrixExplicitAuth.GetCookieValue(cookies, storeRootUri, "CsrfToken") ?? csrf;

        if (!string.Equals(authResult, "success", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new CitrixLoginResponse
            {
                Ok = true,
                RequestId = requestId,
                LoginSucceeded = false,
                Message = "DomainPassthroughAuth nevrátil success. Zkontrolujte Kerberos delegaci (RBCD) a Windows Authentication na IIS.",
                AuthResult = authResult,
                BootstrapStatusCode = bootstrapStatusCode is null ? null : (int)bootstrapStatusCode.Value,
                AuthMethodsStatusCode = authMethodsStatusCode is null ? null : (int)authMethodsStatusCode.Value,
                LoginSubmitStatusCode = loginSubmitStatusCode is null ? null : (int)loginSubmitStatusCode.Value,
                BootstrapFinalUrl = bootstrapFinalUrl,
                LoginPostUrl = dptUri.ToString(),
                CookieNames = CitrixExplicitAuth.GetCookieNames(cookies, storeRootUri),
                CsrfTokenFound = !string.IsNullOrWhiteSpace(updatedCsrf),
                AuthMethodsPreview = authMethodsPreview,
                LoginSubmitPreview = loginSubmitPreview
            });
        }

        // Step 4: Resources/List — session cookies are sufficient, no Kerberos needed
        using (var resHandler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = cookies,
            AutomaticDecompression = DecompressionMethods.All
        })
        using (var resClient = new HttpClient(resHandler) { Timeout = TimeSpan.FromSeconds(30) })
        {
            var resHeaders = CitrixExplicitAuth.CreateBaseHeaders(storeRootUri, resourcesUri, httpsHeaderValue, updatedCsrf, forwardedAcceptLanguage, forwardedUserAgent);
            using var resReq = CitrixExplicitAuth.CreateRequest(HttpMethod.Post, resourcesUri, resHeaders, "format=json&resourceDetails=Default", "application/x-www-form-urlencoded; charset=UTF-8");
            using var resResp = await resClient.SendAsync(resReq, cancellationToken);
            resourcesStatusCode = resResp.StatusCode;
            var resBody = await resResp.Content.ReadAsStringAsync(cancellationToken);
            resourcesPreview = CitrixExplicitAuth.Preview(resBody);
            var resourcesPayload = CitrixExplicitAuth.TryParseJson(resBody);

            var sessionToken = sessionCache.Store(new CitrixSessionEntry
            {
                Cookies = cookies,
                StoreRootUri = storeRootUri,
                CreatedAt = DateTimeOffset.UtcNow
            });

            logger.LogInformation(
                "Citrix SSO login succeeded. RequestId: {RequestId}. SessionToken: {SessionToken}. WindowsUser: {WindowsUser}",
                requestId, sessionToken, windowsIdentity.Name);

            return Results.Ok(new CitrixLoginResponse
            {
                Ok = true,
                RequestId = requestId,
                LoginSucceeded = true,
                Message = "SSO přihlášení přes DomainPassthroughAuth proběhlo úspěšně.",
                SessionToken = sessionToken,
                AuthResult = authResult,
                BootstrapStatusCode = bootstrapStatusCode is null ? null : (int)bootstrapStatusCode.Value,
                AuthMethodsStatusCode = authMethodsStatusCode is null ? null : (int)authMethodsStatusCode.Value,
                LoginSubmitStatusCode = loginSubmitStatusCode is null ? null : (int)loginSubmitStatusCode.Value,
                ResourcesStatusCode = resourcesStatusCode is null ? null : (int)resourcesStatusCode.Value,
                BootstrapFinalUrl = bootstrapFinalUrl,
                LoginPostUrl = dptUri.ToString(),
                ResourcesUrl = resourcesUri.ToString(),
                CookieNames = CitrixExplicitAuth.GetCookieNames(cookies, storeRootUri),
                CsrfTokenFound = !string.IsNullOrWhiteSpace(CitrixExplicitAuth.GetCookieValue(cookies, storeRootUri, "CsrfToken")),
                AuthMethodsPreview = authMethodsPreview,
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
            "Citrix SSO login failed. RequestId: {RequestId}. StoreRootUrl: {StoreRootUrl}. WindowsUser: {WindowsUser}",
            requestId, storeRootUri, windowsIdentity.Name);

        return Results.Ok(new CitrixLoginResponse
        {
            Ok = false,
            RequestId = requestId,
            AuthResult = authResult,
            BootstrapStatusCode = bootstrapStatusCode is null ? null : (int)bootstrapStatusCode.Value,
            AuthMethodsStatusCode = authMethodsStatusCode is null ? null : (int)authMethodsStatusCode.Value,
            LoginSubmitStatusCode = loginSubmitStatusCode is null ? null : (int)loginSubmitStatusCode.Value,
            ResourcesStatusCode = resourcesStatusCode is null ? null : (int)resourcesStatusCode.Value,
            BootstrapFinalUrl = bootstrapFinalUrl,
            LoginPostUrl = dptUri.ToString(),
            ResourcesUrl = resourcesUri.ToString(),
            AuthMethodsPreview = authMethodsPreview,
            LoginSubmitPreview = loginSubmitPreview,
            ResourcesPreview = resourcesPreview,
            CookieNames = CitrixExplicitAuth.GetCookieNames(cookies, storeRootUri),
            CsrfTokenFound = !string.IsNullOrWhiteSpace(CitrixExplicitAuth.GetCookieValue(cookies, storeRootUri, "CsrfToken")),
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
    authType = ctx.User.Identity?.AuthenticationType ?? "none",
    isKerberos = ctx.User.Identity?.AuthenticationType == "Kerberos",
    impersonationLevel = ctx.User.Identity is WindowsIdentity wi ? wi.ImpersonationLevel.ToString() : "n/a"
}));

app.MapGet("/api/citrix-sso/test", async (HttpContext ctx, IConfiguration config, ILoggerFactory loggerFactory) =>
{
    var logger = loggerFactory.CreateLogger("CitrixSsoTest");
    var storeRootUrl = config["CitrixDiagnostics:BaseUrl"] ?? "";

    if (ctx.User.Identity is not WindowsIdentity windowsIdentity || !windowsIdentity.IsAuthenticated)
        return Results.Ok(new { step = "windows-auth", error = "Windows identita není dostupná." });

    var userName = windowsIdentity.Name;
    var processIdentity = WindowsIdentity.GetCurrent().Name;
    var upnFromClaims = windowsIdentity.Claims
        .FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.Upn)?.Value ?? "";
    var allClaims = windowsIdentity.Claims
        .Select(c => $"{c.Type.Split('/').Last()}={c.Value}")
        .ToList();

    // Krok 1 — DNS
    string dnsResult;
    if (!Uri.TryCreate(storeRootUrl, UriKind.Absolute, out var storeUri))
        return Results.Ok(new { step = "config", error = "Chybí nebo neplatná CitrixDiagnostics:BaseUrl.", baseUrl = storeRootUrl });
    try
    {
        var addrs = await System.Net.Dns.GetHostAddressesAsync(storeUri.Host);
        dnsResult = string.Join(", ", addrs.Select(a => a.ToString()));
    }
    catch (Exception ex)
    {
        return Results.Ok(new { step = "dns", error = ex.Message, host = storeUri.Host });
    }

    // Krok 2 — S4U2Self
    WindowsIdentity s4uIdentity;
    string s4uUpn;
    try
    {
        if (!string.IsNullOrWhiteSpace(upnFromClaims))
        {
            s4uUpn = upnFromClaims;
        }
        else
        {
            var domainMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["FIS"] = "fis.acr",
                ["ACR"] = "acr"
            };
            var parts = userName.Split('\\');
            var netbios = parts.Length == 2 ? parts[0] : "";
            var user = parts.Length == 2 ? parts[1] : userName;
            var fqdn = domainMap.TryGetValue(netbios, out var d) ? d : $"{netbios.ToLower()}.acr";
            s4uUpn = $"{user}@{fqdn}";
        }
        s4uIdentity = new WindowsIdentity(s4uUpn);
    }
    catch (Exception ex)
    {
        return Results.Ok(new { step = "s4u2self", error = ex.Message, windowsUser = userName, processUser = processIdentity });
    }

    var impLevel = s4uIdentity.ImpersonationLevel.ToString();

    // Krok 3 — Bootstrap (bez credentials)
    var sharedCookies = new CookieContainer();
    var bootstrapLog = new List<string>();
    string bootstrapError = "";
    try
    {
        using var bootstrapHandler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = sharedCookies,
            AutomaticDecompression = DecompressionMethods.All,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var bootstrapClient = new HttpClient(bootstrapHandler) { Timeout = TimeSpan.FromSeconds(15) };

        var currentUrl = storeUri;
        for (int hop = 0; hop < 8; hop++)
        {
            using var req = CitrixExplicitAuth.CreateRequest(HttpMethod.Get, currentUrl,
                CitrixExplicitAuth.CreatePageHeaders(storeUri));
            using var resp = await bootstrapClient.SendAsync(req);
            var hopCookies = string.Join(",", sharedCookies.GetCookies(storeUri).Cast<Cookie>().Select(c => c.Name));
            bootstrapLog.Add($"hop{hop}: {(int)resp.StatusCode} {currentUrl} cookies=[{hopCookies}]");

            if (resp.StatusCode is System.Net.HttpStatusCode.Moved or System.Net.HttpStatusCode.Found or System.Net.HttpStatusCode.SeeOther)
            {
                Uri nextUrl;
                if (resp.Headers.Location?.IsAbsoluteUri == true)
                {
                    var loc = resp.Headers.Location;
                    nextUrl = loc.Host.Equals(storeUri.Host, StringComparison.OrdinalIgnoreCase)
                        ? loc
                        : new UriBuilder(loc) { Host = storeUri.Host, Port = storeUri.Port }.Uri;
                }
                else
                {
                    nextUrl = new Uri(currentUrl, resp.Headers.Location);
                }
                currentUrl = nextUrl;
                continue;
            }

            var body = await resp.Content.ReadAsStringAsync();
            var metaUrl = CitrixExplicitAuth.TryExtractMetaRefreshUrl(body);
            if (!string.IsNullOrWhiteSpace(metaUrl))
            {
                Uri nextUrl;
                if (Uri.TryCreate(metaUrl, UriKind.Absolute, out var absMetaUri))
                    nextUrl = absMetaUri.Host.Equals(storeUri.Host, StringComparison.OrdinalIgnoreCase)
                        ? absMetaUri
                        : new UriBuilder(absMetaUri) { Host = storeUri.Host, Port = storeUri.Port }.Uri;
                else
                    nextUrl = new Uri(currentUrl, metaUrl);
                currentUrl = nextUrl;
                continue;
            }
            break;
        }
    }
    catch (Exception ex)
    {
        bootstrapError = ex.Message;
        return Results.Ok(new { step = "bootstrap", error = bootstrapError, bootstrapLog, dnsResult, host = storeUri.Host, windowsUser = userName, s4uUpn, impLevel });
    }

    var checkUris = new[] { storeUri, new Uri(storeUri, "/Citrix/FISWeb/"), new Uri(storeUri, "/"), new Uri($"{storeUri.Scheme}://{storeUri.Host}/") };
    var allCookies = checkUris.SelectMany(u => sharedCookies.GetCookies(u).Cast<Cookie>()).DistinctBy(c => c.Name).ToList();
    var csrf = allCookies.FirstOrDefault(c => c.Name.Equals("CsrfToken", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
    var cookieNames = string.Join(", ", allCookies.Select(c => c.Name));

    // Krok 4 — GetAuthMethods (bez credentials) — zakládá ASP.NET session + CsrfToken
    string getAuthMethodsStatus = "";
    string getAuthMethodsError = "";
    try
    {
        using var sessionHandler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = sharedCookies,
            AutomaticDecompression = DecompressionMethods.All,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var sessionClient = new HttpClient(sessionHandler) { Timeout = TimeSpan.FromSeconds(15) };
        var authMethodsUri = new Uri(storeUri, "Authentication/GetAuthMethods");
        var amHeaders = CitrixExplicitAuth.CreateBaseHeaders(storeUri, authMethodsUri, storeUri.Scheme == "https" ? "Yes" : "No");
        amHeaders["X-Citrix-AM-CredentialTypes"] = CitrixExplicitAuth.FormCredentialTypes;
        amHeaders["X-Citrix-AM-LabelTypes"] = CitrixExplicitAuth.FormLabelTypes;
        using var amReq = CitrixExplicitAuth.CreateRequest(HttpMethod.Post, authMethodsUri, amHeaders, "", "application/x-www-form-urlencoded; charset=UTF-8");
        using var amResp = await sessionClient.SendAsync(amReq);
        var amBody = await amResp.Content.ReadAsStringAsync();
        getAuthMethodsStatus = $"{(int)amResp.StatusCode}";

        // Obnov csrf po GetAuthMethods
        var updatedCookies = checkUris.SelectMany(u => sharedCookies.GetCookies(u).Cast<Cookie>()).DistinctBy(c => c.Name).ToList();
        csrf = updatedCookies.FirstOrDefault(c => c.Name.Equals("CsrfToken", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
        cookieNames = string.Join(", ", updatedCookies.Select(c => c.Name));
    }
    catch (Exception ex)
    {
        getAuthMethodsError = ex.Message;
    }

    // Krok 5a — DomainPassthrough BEZ impersonace (jako app_zadosti) — test konektivity
    string dptNoImpStatus = "";
    string dptNoImpError = "";
    string dptNoImpWwwAuth = "";
    try
    {
        using var noImpHandler = new HttpClientHandler
        {
            UseDefaultCredentials = true,
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var noImpClient = new HttpClient(noImpHandler) { Timeout = TimeSpan.FromSeconds(15) };
        var dptUri2 = new Uri(storeUri, "DomainPassthroughAuth/Login");
        using var noImpReq = new HttpRequestMessage(HttpMethod.Post, dptUri2);
        noImpReq.Headers.Add("Accept", "application/xml, text/xml, */*");
        noImpReq.Headers.Add("X-Requested-With", "XMLHttpRequest");
        noImpReq.Headers.Add("X-Citrix-IsUsingHTTPS", storeUri.Scheme == "https" ? "Yes" : "No");
        if (!string.IsNullOrEmpty(csrf)) noImpReq.Headers.Add("Csrf-Token", csrf);
        noImpReq.Headers.Add("Referer", storeUri.ToString());
        noImpReq.Content = new StringContent("", Encoding.UTF8, "application/x-www-form-urlencoded");
        using var noImpResp = await noImpClient.SendAsync(noImpReq);
        var noImpBody = await noImpResp.Content.ReadAsStringAsync();
        dptNoImpStatus = $"{(int)noImpResp.StatusCode} | WWW-Auth: {noImpResp.Headers.WwwAuthenticate} | body: {(noImpBody.Length > 300 ? noImpBody[..300] + "..." : noImpBody)}";
        dptNoImpWwwAuth = noImpResp.Headers.WwwAuthenticate.ToString();
    }
    catch (Exception ex)
    {
        dptNoImpError = ex.Message;
    }

    // Krok 5 — DomainPassthrough s impersonací
    string authStep = "";
    string authError = "";
    string dptStatus = "";
    string dptBody2 = "";
    string wwwAuth = "";
    try
    {
        (authStep, authError, dptStatus, dptBody2, wwwAuth) = await WindowsIdentity.RunImpersonatedAsync(s4uIdentity.AccessToken, async () =>
        {
            using var authHandler = new HttpClientHandler
            {
                UseDefaultCredentials = true,
                UseProxy = false,
                AllowAutoRedirect = false,
                UseCookies = true,
                CookieContainer = sharedCookies,
                AutomaticDecompression = DecompressionMethods.All,
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using var authClient = new HttpClient(authHandler) { Timeout = TimeSpan.FromSeconds(15) };

            var dptUri = new Uri(storeUri, "DomainPassthroughAuth/Login");
            using var dptReq = new HttpRequestMessage(HttpMethod.Post, dptUri);
            dptReq.Headers.Add("Accept", "application/xml, text/xml, */*");
            dptReq.Headers.Add("X-Requested-With", "XMLHttpRequest");
            dptReq.Headers.Add("X-Citrix-IsUsingHTTPS", storeUri.Scheme == "https" ? "Yes" : "No");
            if (!string.IsNullOrEmpty(csrf)) dptReq.Headers.Add("Csrf-Token", csrf);
            dptReq.Headers.Add("Referer", storeUri.ToString());
            dptReq.Content = new StringContent("", Encoding.UTF8, "application/x-www-form-urlencoded");

            using var dptResp = await authClient.SendAsync(dptReq);
            var body = await dptResp.Content.ReadAsStringAsync();
            return ("dpt-call", "", $"{(int)dptResp.StatusCode} {dptResp.ReasonPhrase}", body.Length > 600 ? body[..600] + "..." : body, dptResp.Headers.WwwAuthenticate.ToString());
        });
    }
    catch (Exception ex)
    {
        authError = ex.Message;
        authStep = "dpt-call";
    }

    return Results.Ok(new
    {
        step = string.IsNullOrEmpty(authError) ? "ok" : authStep,
        error = authError,
        windowsUser = userName,
        processUser = processIdentity,
        upnFromClaims,
        allClaims,
        s4uUpn,
        impLevel,
        dnsResult,
        host = storeUri.Host,
        bootstrapLog,
        getAuthMethodsStatus,
        getAuthMethodsError,
        csrf = !string.IsNullOrEmpty(csrf),
        cookies = cookieNames,
        dptNoImpStatus,
        dptNoImpError,
        dptStatus,
        dptBody = dptBody2,
        wwwAuth
    });
});

app.MapPost("/api/citrix-diagnostics/citrixauth-probe", async (IConfiguration configuration, CancellationToken cancellationToken) =>
{
    var baseUrl = configuration["CitrixDiagnostics:BaseUrl"] ?? "";
    var storeRootUri = new Uri(baseUrl);
    var loginUri = new Uri(storeRootUri, "CitrixAuth/Login");

    using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true };
    using var client = new HttpClient(handler);

    using var request = new HttpRequestMessage(HttpMethod.Post, loginUri);
    request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
    request.Headers.TryAddWithoutValidation("Accept", "*/*");
    request.Headers.TryAddWithoutValidation("X-Citrix-IsUsingHTTPS", "Yes");
    request.Headers.TryAddWithoutValidation("X-Citrix-Background-Request", "True");
    request.Headers.TryAddWithoutValidation("User-Agent", "CitrixReceiver/26.3.0.95 Windows/10.0 SelfService/26.3.0.96 (Release) X1Class CWACapable");
    request.Content = new StringContent(string.Empty);
    request.Content.Headers.ContentType = null;

    using var response = await client.SendAsync(request, cancellationToken);
    var body = await response.Content.ReadAsStringAsync(cancellationToken);
    var respHeaders = response.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value));

    return Results.Ok(new
    {
        status = (int)response.StatusCode,
        headers = respHeaders,
        body = CitrixExplicitAuth.Preview(body)
    });
});

app.MapPost("/api/citrix-diagnostics/fisauth-token-probe", async (IConfiguration configuration, CancellationToken cancellationToken) =>
{
    var baseUrl = configuration["CitrixDiagnostics:BaseUrl"] ?? "";
    var baseUri = new Uri(baseUrl);
    var fisAuthRoot = new Uri($"{baseUri.Scheme}://{baseUri.Host}/Citrix/FISAuth/");
    var log = new List<object>();

    using var handler = new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = true,
        UseDefaultCredentials = true
    };
    using var client = new HttpClient(handler);

    // Step 1: GET auth/v1/token — přímé sondování token endpointu z WWW-Authenticate challenge
    var tokenUri = new Uri(fisAuthRoot, "auth/v1/token");
    using var tokenReq = new HttpRequestMessage(HttpMethod.Get, tokenUri);
    tokenReq.Headers.TryAddWithoutValidation("Accept", "application/vnd.citrix.requesttokenresponse+xml, text/xml, */*");
    tokenReq.Headers.TryAddWithoutValidation("X-Citrix-IsUsingHTTPS", "Yes");
    using var tokenResp = await client.SendAsync(tokenReq, cancellationToken);
    var tokenBody = await tokenResp.Content.ReadAsStringAsync(cancellationToken);
    var tokenHeaders = tokenResp.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value));
    log.Add(new { step = "GET auth/v1/token", status = (int)tokenResp.StatusCode, headers = tokenHeaders, body = CitrixExplicitAuth.Preview(tokenBody) });

    // Step 2: POST ExplicitForms/Start s prázdným tělem — nechat FISAuth vrátit formulář/výzvu
    var startUri = new Uri(fisAuthRoot, "ExplicitForms/Start");
    using var startEmptyReq = new HttpRequestMessage(HttpMethod.Post, startUri);
    startEmptyReq.Headers.TryAddWithoutValidation("Accept", "application/vnd.citrix.requesttokenresponse+xml, text/xml, */*");
    startEmptyReq.Headers.TryAddWithoutValidation("X-Citrix-IsUsingHTTPS", "Yes");
    startEmptyReq.Content = new StringContent(string.Empty);
    startEmptyReq.Content.Headers.ContentType = null;
    using var startEmptyResp = await client.SendAsync(startEmptyReq, cancellationToken);
    var startEmptyBody = await startEmptyResp.Content.ReadAsStringAsync(cancellationToken);
    log.Add(new { step = "POST ExplicitForms/Start (empty)", status = (int)startEmptyResp.StatusCode, body = CitrixExplicitAuth.Preview(startEmptyBody) });

    // Step 3: POST ExplicitForms/Start s requesttoken XML (realm z CitrixAuth/Login challenge)
    const string realm = "cf07671f-361c-401a-b6ab-83c7ef97736b";
    var tokenUrl = tokenUri.ToString();
    var requestTokenXml = $"""
        <?xml version="1.0" encoding="UTF-8" standalone="no" ?>
        <requesttoken xmlns="http://citrix.com/delivery-services/1-0/auth/requesttoken">
          <for-service>{realm}</for-service>
          <for-service-url>{tokenUrl}</for-service-url>
          <reqtokentemplate></reqtokentemplate>
          <requested-lifetime>0.20:00:00</requested-lifetime>
        </requesttoken>
        """;
    using var startXmlReq = new HttpRequestMessage(HttpMethod.Post, startUri);
    startXmlReq.Headers.TryAddWithoutValidation("Accept", "application/vnd.citrix.requesttokenresponse+xml, text/xml");
    startXmlReq.Headers.TryAddWithoutValidation("X-Citrix-IsUsingHTTPS", "Yes");
    startXmlReq.Content = new StringContent(requestTokenXml, System.Text.Encoding.UTF8,
        "application/vnd.citrix.requesttoken+xml");
    using var startXmlResp = await client.SendAsync(startXmlReq, cancellationToken);
    var startXmlBody = await startXmlResp.Content.ReadAsStringAsync(cancellationToken);
    log.Add(new { step = "POST ExplicitForms/Start (requesttoken XML)", status = (int)startXmlResp.StatusCode, body = CitrixExplicitAuth.Preview(startXmlBody) });

    return Results.Ok(new { fisAuthRoot = fisAuthRoot.ToString(), log });
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

internal sealed class CitrixSessionCache(IMemoryCache cache, IConfiguration config)
{
    private readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(config.GetValue("SessionCacheMinutes", 20));

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
