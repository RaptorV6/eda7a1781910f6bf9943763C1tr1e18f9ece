using PortalComponent.Models;

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

app.MapRazorPages()
   .WithStaticAssets();

app.Run();
