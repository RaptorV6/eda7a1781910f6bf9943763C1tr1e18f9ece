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

    HttpStatusCode? bootstrapStatusCode = null;
    HttpStatusCode? loginFormStatusCode = null;
    HttpStatusCode? loginSubmitStatusCode = null;
    HttpStatusCode? resourcesStatusCode = null;

    string loginFormPreview = string.Empty;
    string loginSubmitPreview = string.Empty;
    string resourcesPreview = string.Empty;
    string authResult = string.Empty;
    string loginPostUrl = string.Empty;

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
            _ = await bootstrapResponse.Content.ReadAsStringAsync(cancellationToken);
        }

        var loginFormHeaders = CitrixExplicitAuth.CreateBaseHeaders(storeRootUri, explicitLoginUri, httpsHeaderValue);
        loginFormHeaders["X-Citrix-AM-CredentialTypes"] = CitrixExplicitAuth.FormCredentialTypes;
        loginFormHeaders["X-Citrix-AM-LabelTypes"] = CitrixExplicitAuth.FormLabelTypes;

        CitrixAuthFormDefinition? authForm = null;

        using (var loginFormRequest = CitrixExplicitAuth.CreateRequest(
            HttpMethod.Post,
            explicitLoginUri,
            loginFormHeaders,
            string.Empty,
            "application/x-www-form-urlencoded; charset=UTF-8"))
        using (var loginFormResponse = await client.SendAsync(loginFormRequest, cancellationToken))
        {
            loginFormStatusCode = loginFormResponse.StatusCode;
            var loginFormResponseBody = await loginFormResponse.Content.ReadAsStringAsync(cancellationToken);
            loginFormPreview = CitrixExplicitAuth.Preview(loginFormResponseBody);
            authForm = CitrixExplicitAuth.TryParseAuthForm(loginFormResponseBody);
        }

        if (authForm is null)
        {
            using var loginFormGetRequest = CitrixExplicitAuth.CreateRequest(
                HttpMethod.Get,
                explicitLoginUri,
                loginFormHeaders);
            using var loginFormGetResponse = await client.SendAsync(loginFormGetRequest, cancellationToken);
            loginFormStatusCode = loginFormGetResponse.StatusCode;
            var loginFormGetBody = await loginFormGetResponse.Content.ReadAsStringAsync(cancellationToken);
            loginFormPreview = CitrixExplicitAuth.Preview(loginFormGetBody);
            authForm = CitrixExplicitAuth.TryParseAuthForm(loginFormGetBody);
        }

        if (authForm is null)
        {
            return Results.Ok(new CitrixLoginResponse
            {
                Ok = false,
                RequestId = loginRequest.RequestId,
                BootstrapStatusCode = bootstrapStatusCode is null ? null : (int)bootstrapStatusCode.Value,
                LoginFormStatusCode = loginFormStatusCode is null ? null : (int)loginFormStatusCode.Value,
                LoginFormUrl = explicitLoginUri.ToString(),
                CookieNames = CitrixExplicitAuth.GetCookieNames(handler.CookieContainer, storeRootUri),
                CsrfTokenFound = !string.IsNullOrWhiteSpace(CitrixExplicitAuth.GetCookieValue(handler.CookieContainer, storeRootUri, "CsrfToken")),
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
                LoginFormStatusCode = loginFormStatusCode is null ? null : (int)loginFormStatusCode.Value,
                LoginFormUrl = explicitLoginUri.ToString(),
                LoginFormPreview = loginFormPreview,
                CookieNames = CitrixExplicitAuth.GetCookieNames(handler.CookieContainer, storeRootUri),
                CsrfTokenFound = !string.IsNullOrWhiteSpace(CitrixExplicitAuth.GetCookieValue(handler.CookieContainer, storeRootUri, "CsrfToken")),
                ErrorType = "LoginFormIncomplete",
                ErrorMessage = "Citrix login formulář neobsahuje očekávaná credential pole nebo submit button."
            });
        }

        var loginPostUri = new Uri(explicitLoginUri, authForm.PostBack);
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
                LoginFormStatusCode = loginFormStatusCode is null ? null : (int)loginFormStatusCode.Value,
                LoginSubmitStatusCode = loginSubmitStatusCode is null ? null : (int)loginSubmitStatusCode.Value,
                LoginFormUrl = explicitLoginUri.ToString(),
                LoginPostUrl = loginPostUrl,
                CookieNames = cookieNames,
                CsrfTokenFound = csrfTokenFound,
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
                LoginFormStatusCode = loginFormStatusCode is null ? null : (int)loginFormStatusCode.Value,
                LoginSubmitStatusCode = loginSubmitStatusCode is null ? null : (int)loginSubmitStatusCode.Value,
                ResourcesStatusCode = resourcesStatusCode is null ? null : (int)resourcesStatusCode.Value,
                LoginFormUrl = explicitLoginUri.ToString(),
                LoginPostUrl = loginPostUrl,
                ResourcesUrl = resourcesUri.ToString(),
                CookieNames = CitrixExplicitAuth.GetCookieNames(handler.CookieContainer, storeRootUri),
                CsrfTokenFound = !string.IsNullOrWhiteSpace(CitrixExplicitAuth.GetCookieValue(handler.CookieContainer, storeRootUri, "CsrfToken")),
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
            LoginFormStatusCode = loginFormStatusCode is null ? null : (int)loginFormStatusCode.Value,
            LoginSubmitStatusCode = loginSubmitStatusCode is null ? null : (int)loginSubmitStatusCode.Value,
            ResourcesStatusCode = resourcesStatusCode is null ? null : (int)resourcesStatusCode.Value,
            LoginFormUrl = explicitLoginUri.ToString(),
            LoginPostUrl = loginPostUrl,
            ResourcesUrl = resourcesUri.ToString(),
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
}
