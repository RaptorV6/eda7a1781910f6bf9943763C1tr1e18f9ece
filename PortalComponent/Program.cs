using PortalComponent.Models;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();

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

app.MapPost("/api/citrix-diagnostics/explicit-login", async (
    CitrixLoginRequest loginRequest,
    ILoggerFactory loggerFactory,
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

    var explicitLoginUri = new Uri(storeRootUri, "ExplicitAuth/Login");
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
    string loginPostUrl = string.Empty;
    Uri? resolvedLoginFormUri = null;
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
            CitrixExplicitAuth.CreateBaseHeaders(storeRootUri, storeRootUri, httpsHeaderValue)))
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
                        CitrixExplicitAuth.CreateBaseHeaders(storeRootUri, nextBootstrapUri, httpsHeaderValue));
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

        var loginFormHeaders = CitrixExplicitAuth.CreateBaseHeaders(storeRootUri, explicitLoginUri, httpsHeaderValue);
        loginFormHeaders["X-Citrix-AM-CredentialTypes"] = CitrixExplicitAuth.FormCredentialTypes;
        loginFormHeaders["X-Citrix-AM-LabelTypes"] = CitrixExplicitAuth.FormLabelTypes;

        var loginUriCandidates = new List<Uri>();
        var authMethodsHeaders = CitrixExplicitAuth.CreateBaseHeaders(storeRootUri, authMethodsUri, httpsHeaderValue);
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
                    if (!loginUriCandidates.Contains(candidate))
                    {
                        loginUriCandidates.Add(candidate);
                    }
                }

                if (loginUriCandidates.Count > 0)
                {
                    break;
                }
            }
            catch
            {
                // Keep going with fallbacks; the detailed failure is surfaced through the final response.
            }
        }

        foreach (var fallbackUri in new[]
                 {
                     new Uri(storeRootUri, "Login"),
                     explicitLoginUri,
                     new Uri(storeRootUri, "Authentication/Login")
                 })
        {
            if (!loginUriCandidates.Contains(fallbackUri))
            {
                loginUriCandidates.Add(fallbackUri);
            }
        }

        CitrixAuthFormDefinition? authForm = null;

        foreach (var candidateUri in loginUriCandidates)
        {
            var candidateHeaders = CitrixExplicitAuth.CreateBaseHeaders(storeRootUri, candidateUri, httpsHeaderValue);
            candidateHeaders["X-Citrix-AM-CredentialTypes"] = CitrixExplicitAuth.FormCredentialTypes;
            candidateHeaders["X-Citrix-AM-LabelTypes"] = CitrixExplicitAuth.FormLabelTypes;

            foreach (var candidateMethod in new[] { HttpMethod.Post, HttpMethod.Get })
            {
                try
                {
                    using var loginFormRequest = CitrixExplicitAuth.CreateRequest(
                        candidateMethod,
                        candidateUri,
                        candidateHeaders,
                        candidateMethod == HttpMethod.Post ? string.Empty : null,
                        candidateMethod == HttpMethod.Post ? "application/x-www-form-urlencoded; charset=UTF-8" : null);
                    using var loginFormResponse = await client.SendAsync(loginFormRequest, cancellationToken);
                    loginFormStatusCode = loginFormResponse.StatusCode;
                    var loginFormResponseBody = await loginFormResponse.Content.ReadAsStringAsync(cancellationToken);
                    loginFormPreview = CitrixExplicitAuth.Preview(loginFormResponseBody);
                    authForm = CitrixExplicitAuth.TryParseAuthForm(loginFormResponseBody);
                    loginAttemptResults.Add($"{candidateMethod} {candidateUri} => {(int)loginFormResponse.StatusCode} parse={(authForm is not null ? "ok" : "none")}");

                    if (authForm is not null)
                    {
                        resolvedLoginFormUri = candidateUri;
                        break;
                    }
                }
                catch (Exception exception)
                {
                    loginAttemptResults.Add($"{candidateMethod} {candidateUri} => EXCEPTION {exception.GetType().Name}: {exception.Message}");
                }
            }

            if (authForm is not null)
            {
                break;
            }
        }

        if (authForm is null)
        {
            return Results.Ok(new CitrixLoginResponse
            {
                Ok = false,
                RequestId = loginRequest.RequestId,
                BootstrapStatusCode = bootstrapStatusCode is null ? null : (int)bootstrapStatusCode.Value,
                BootstrapLandingStatusCode = bootstrapLandingStatusCode is null ? null : (int)bootstrapLandingStatusCode.Value,
                AuthMethodsStatusCode = authMethodsStatusCode is null ? null : (int)authMethodsStatusCode.Value,
                LoginFormStatusCode = loginFormStatusCode is null ? null : (int)loginFormStatusCode.Value,
                BootstrapFinalUrl = bootstrapFinalUrl,
                BootstrapRedirectUrl = bootstrapRedirectUrl,
                AuthMethodsUrl = authMethodsUri.ToString(),
                AuthMethodCandidates = authMethodCandidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                LoginFormUrl = loginUriCandidates.FirstOrDefault()?.ToString() ?? explicitLoginUri.ToString(),
                LoginAttemptResults = loginAttemptResults.ToArray(),
                CookieNames = CitrixExplicitAuth.GetCookieNames(handler.CookieContainer, storeRootUri),
                CsrfTokenFound = !string.IsNullOrWhiteSpace(CitrixExplicitAuth.GetCookieValue(handler.CookieContainer, storeRootUri, "CsrfToken")),
                BootstrapHeaders = bootstrapHeaders,
                BootstrapBodyPreview = bootstrapBodyPreview,
                BootstrapLandingPreview = bootstrapLandingPreview,
                AuthMethodsPreview = authMethodsPreview,
                LoginFormPreview = loginFormPreview,
                ErrorType = "LoginFormParseFailed",
                ErrorMessage = "Nepodařilo se rozparsovat explicit login formulář z Citrix odpovědi."
            });
        }

        authResult = authForm.Result;

        if (!authForm.HasCredentialInputs)
        {
            return Results.Ok(new CitrixLoginResponse
            {
                Ok = false,
                RequestId = loginRequest.RequestId,
                AuthResult = authResult,
                BootstrapStatusCode = bootstrapStatusCode is null ? null : (int)bootstrapStatusCode.Value,
                BootstrapLandingStatusCode = bootstrapLandingStatusCode is null ? null : (int)bootstrapLandingStatusCode.Value,
                AuthMethodsStatusCode = authMethodsStatusCode is null ? null : (int)authMethodsStatusCode.Value,
                LoginFormStatusCode = loginFormStatusCode is null ? null : (int)loginFormStatusCode.Value,
                BootstrapFinalUrl = bootstrapFinalUrl,
                BootstrapRedirectUrl = bootstrapRedirectUrl,
                AuthMethodsUrl = authMethodsUri.ToString(),
                AuthMethodCandidates = authMethodCandidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                LoginFormUrl = resolvedLoginFormUri?.ToString() ?? loginUriCandidates.FirstOrDefault()?.ToString() ?? explicitLoginUri.ToString(),
                LoginAttemptResults = loginAttemptResults.ToArray(),
                BootstrapHeaders = bootstrapHeaders,
                BootstrapBodyPreview = bootstrapBodyPreview,
                BootstrapLandingPreview = bootstrapLandingPreview,
                AuthMethodsPreview = authMethodsPreview,
                LoginFormPreview = loginFormPreview,
                CookieNames = CitrixExplicitAuth.GetCookieNames(handler.CookieContainer, storeRootUri),
                CsrfTokenFound = !string.IsNullOrWhiteSpace(CitrixExplicitAuth.GetCookieValue(handler.CookieContainer, storeRootUri, "CsrfToken")),
                ErrorType = "LoginFormIncomplete",
                ErrorMessage = "Citrix login formulář neobsahuje očekávaná credential pole nebo submit button."
            });
        }

        var loginPostUri = new Uri(resolvedLoginFormUri ?? explicitLoginUri, authForm.PostBack);
        loginPostUrl = loginPostUri.ToString();

        var currentCsrfToken = CitrixExplicitAuth.GetCookieValue(handler.CookieContainer, storeRootUri, "CsrfToken");
        var loginSubmitHeaders = CitrixExplicitAuth.CreateBaseHeaders(storeRootUri, loginPostUri, httpsHeaderValue, currentCsrfToken);

        var loginFormPayload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [authForm.UsernameId] = loginRequest.Username.Trim(),
            [authForm.PasswordId] = loginRequest.Password,
            [authForm.SubmitButtonId] = authForm.SubmitButtonValue,
            ["StateContext"] = authForm.StateContext
        };

        if (!string.IsNullOrWhiteSpace(authForm.DomainId))
        {
            loginFormPayload[authForm.DomainId] = loginRequest.Domain.Trim();
        }

        var loginFormBody = await new FormUrlEncodedContent(loginFormPayload).ReadAsStringAsync(cancellationToken);

        using (var loginSubmitRequest = CitrixExplicitAuth.CreateRequest(
            HttpMethod.Post,
            loginPostUri,
            loginSubmitHeaders,
            loginFormBody,
            "application/x-www-form-urlencoded; charset=UTF-8"))
        using (var loginSubmitResponse = await client.SendAsync(loginSubmitRequest, cancellationToken))
        {
            loginSubmitStatusCode = loginSubmitResponse.StatusCode;
            var loginSubmitBody = await loginSubmitResponse.Content.ReadAsStringAsync(cancellationToken);
            loginSubmitPreview = CitrixExplicitAuth.Preview(loginSubmitBody);

            var loginSubmitParsed = CitrixExplicitAuth.TryParseAuthForm(loginSubmitBody);
            authResult = loginSubmitParsed?.Result ?? CitrixExplicitAuth.FindElementValue(loginSubmitBody, "Result");
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
                Message = "Citrix login nevrátil success. Zkontrolujte credentials nebo další auth krok.",
                AuthResult = authResult,
                BootstrapStatusCode = bootstrapStatusCode is null ? null : (int)bootstrapStatusCode.Value,
                BootstrapLandingStatusCode = bootstrapLandingStatusCode is null ? null : (int)bootstrapLandingStatusCode.Value,
                AuthMethodsStatusCode = authMethodsStatusCode is null ? null : (int)authMethodsStatusCode.Value,
                LoginFormStatusCode = loginFormStatusCode is null ? null : (int)loginFormStatusCode.Value,
                LoginSubmitStatusCode = loginSubmitStatusCode is null ? null : (int)loginSubmitStatusCode.Value,
                BootstrapFinalUrl = bootstrapFinalUrl,
                BootstrapRedirectUrl = bootstrapRedirectUrl,
                AuthMethodsUrl = authMethodsUri.ToString(),
                AuthMethodCandidates = authMethodCandidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                LoginFormUrl = (resolvedLoginFormUri ?? explicitLoginUri).ToString(),
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

        var resourcesHeaders = CitrixExplicitAuth.CreateBaseHeaders(storeRootUri, resourcesUri, httpsHeaderValue, currentCsrfToken);

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

            return Results.Ok(new CitrixLoginResponse
            {
                Ok = true,
                RequestId = loginRequest.RequestId,
                LoginSucceeded = true,
                Message = "Citrix explicit login proběhl a server vrátil Resources/List.",
                AuthResult = authResult,
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
                LoginFormUrl = (resolvedLoginFormUri ?? explicitLoginUri).ToString(),
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
            LoginFormUrl = resolvedLoginFormUri?.ToString() ?? explicitLoginUri.ToString(),
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

app.MapRazorPages()
   .WithStaticAssets();

app.Run();

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
    public const string FormCredentialTypes = "none, username, domain, password, newpassword, passcode, savecredentials, textcredential, webview";
    public const string FormLabelTypes = "none, plain, heading, information, warning, error, confirmation, image";

    public static Dictionary<string, string> CreateBaseHeaders(Uri storeRootUri, Uri requestUri, string httpsHeaderValue, string? csrfToken = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/xml, text/xml, */*; q=0.01",
            ["Citrix-TransactionId"] = Guid.NewGuid().ToString(),
            ["Origin"] = $"{storeRootUri.Scheme}://{storeRootUri.Authority}",
            ["Referer"] = storeRootUri.ToString(),
            ["X-Citrix-IsUsingHTTPS"] = httpsHeaderValue,
            ["X-Requested-With"] = "XMLHttpRequest"
        };

        if (!string.IsNullOrWhiteSpace(csrfToken))
        {
            headers["Csrf-Token"] = csrfToken;
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
                Result = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Result")?.Value ?? string.Empty,
                PostBack = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "PostBack")?.Value ?? string.Empty,
                StateContext = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "StateContext")?.Value ?? string.Empty,
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
