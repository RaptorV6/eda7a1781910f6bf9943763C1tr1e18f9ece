using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PortalComponent.Pages;

public class IndexModel : PageModel
{
    private const string DefaultPanelTitle = "Citrix aplikace";
    private const int DefaultBodyPreviewLimit = 1200;

    private readonly IConfiguration _configuration;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(IConfiguration configuration, ILogger<IndexModel> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public string CitrixBaseUrl { get; private set; } = string.Empty;

    public string PanelTitle { get; private set; } = DefaultPanelTitle;

    public int BodyPreviewLimit { get; private set; } = DefaultBodyPreviewLimit;

    public void OnGet()
    {
        var diagnosticsSection = _configuration.GetSection("CitrixDiagnostics");

        CitrixBaseUrl = diagnosticsSection["BaseUrl"]?.Trim() ?? string.Empty;
        PanelTitle = diagnosticsSection["PanelTitle"]?.Trim() ?? DefaultPanelTitle;
        BodyPreviewLimit = diagnosticsSection.GetValue<int?>("BodyPreviewLimit") ?? DefaultBodyPreviewLimit;

        var hasConfiguredUrl = !string.IsNullOrWhiteSpace(CitrixBaseUrl);

        _logger.LogInformation(
            "Citrix PoC page opened. Path: {Path}. BaseUrl configured: {HasConfiguredUrl}. BaseUrl: {BaseUrl}. BodyPreviewLimit: {BodyPreviewLimit}",
            HttpContext?.Request?.Path.Value,
            hasConfiguredUrl,
            CitrixBaseUrl,
            BodyPreviewLimit);
    }
}
