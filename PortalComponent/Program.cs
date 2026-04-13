using PortalComponent.Models;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddHttpClient("CitrixDiagnosticsProbe")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All
    });

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
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("CitrixServerProbe");
    var client = httpClientFactory.CreateClient("CitrixDiagnosticsProbe");
    client.Timeout = TimeSpan.FromSeconds(20);

    var requestMessage = new HttpRequestMessage(new HttpMethod(probeRequest.Method), probeRequest.Url);

    foreach (var header in probeRequest.Headers)
    {
        if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value))
        {
            requestMessage.Content ??= new StringContent(probeRequest.Body ?? string.Empty, Encoding.UTF8, probeRequest.ContentType ?? "text/plain");
            requestMessage.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    if (!string.IsNullOrEmpty(probeRequest.Body) && requestMessage.Content is null)
    {
        requestMessage.Content = new StringContent(
            probeRequest.Body,
            Encoding.UTF8,
            probeRequest.ContentType ?? "application/x-www-form-urlencoded; charset=UTF-8");
    }

    logger.LogInformation(
        "Citrix server probe started. RequestId: {RequestId}. Step: {Step}. Method: {Method}. Url: {Url}. HeaderCount: {HeaderCount}",
        probeRequest.RequestId,
        probeRequest.Step,
        probeRequest.Method,
        probeRequest.Url,
        probeRequest.Headers.Count);

    try
    {
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
            ErrorType = exception.GetType().FullName ?? exception.GetType().Name,
            ErrorMessage = exception.Message,
            InnerErrorMessage = exception.InnerException?.Message ?? string.Empty
        });
    }
});

app.MapRazorPages()
   .WithStaticAssets();

app.Run();
