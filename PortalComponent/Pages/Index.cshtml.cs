using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace PortalComponent.Pages;

public class IndexModel : PageModel
{
    private const string DefaultPanelTitle = "Citrix aplikace";
    private const int DefaultBodyPreviewLimit = 1200;

    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(IConfiguration configuration, IHostEnvironment hostEnvironment, ILogger<IndexModel> logger)
    {
        _configuration = configuration;
        _hostEnvironment = hostEnvironment;
        _logger = logger;
    }

    public string CitrixBaseUrl { get; private set; } = string.Empty;

    public string PanelTitle { get; private set; } = DefaultPanelTitle;

    public int BodyPreviewLimit { get; private set; } = DefaultBodyPreviewLimit;

    public string ClientLogEndpoint { get; private set; } = "/api/citrix-diagnostics/client-log";

    public string ServerProbeEndpoint { get; private set; } = "/api/citrix-diagnostics/server-probe";

    public string DiagnosticRequestId { get; private set; } = string.Empty;

    public void OnGet()
    {
        var diagnosticsSection = _configuration.GetSection("CitrixDiagnostics");

        CitrixBaseUrl = diagnosticsSection["BaseUrl"]?.Trim() ?? string.Empty;
        PanelTitle = diagnosticsSection["PanelTitle"]?.Trim() ?? DefaultPanelTitle;
        BodyPreviewLimit = diagnosticsSection.GetValue<int?>("BodyPreviewLimit") ?? DefaultBodyPreviewLimit;
        DiagnosticRequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;

        var hasConfiguredUrl = !string.IsNullOrWhiteSpace(CitrixBaseUrl);

        _logger.LogInformation(
            "Citrix PoC page opened. RequestId: {RequestId}. Path: {Path}. Environment: {Environment}. BaseUrl configured: {HasConfiguredUrl}. BaseUrl: {BaseUrl}. BodyPreviewLimit: {BodyPreviewLimit}",
            DiagnosticRequestId,
            HttpContext?.Request?.Path.Value,
            _hostEnvironment.EnvironmentName,
            hasConfiguredUrl,
            CitrixBaseUrl,
            BodyPreviewLimit);
    }
}
