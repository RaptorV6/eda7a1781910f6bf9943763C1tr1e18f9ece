using PortalComponent.Models;
using System.Net;
using System.Text;

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
            ErrorType = exception.GetType().FullName ?? exception.GetType().Name,
            ErrorMessage = exception.Message,
            InnerErrorMessage = exception.InnerException?.Message ?? string.Empty
        });
    }
});

app.MapRazorPages()
   .WithStaticAssets();

app.Run();
