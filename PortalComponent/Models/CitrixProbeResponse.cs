namespace PortalComponent.Models;

public sealed class CitrixProbeResponse
{
    public bool Ok { get; init; }

    public string RequestId { get; init; } = string.Empty;

    public string Step { get; init; } = string.Empty;

    public int? StatusCode { get; init; }

    public string ReasonPhrase { get; init; } = string.Empty;

    public string FinalUrl { get; init; } = string.Empty;

    public string ContentType { get; init; } = string.Empty;

    public Dictionary<string, string> Headers { get; init; } = [];

    public int? BootstrapStatusCode { get; init; }

    public string BootstrapReasonPhrase { get; init; } = string.Empty;

    public string BootstrapFinalUrl { get; init; } = string.Empty;

    public string[] BootstrapCookieNames { get; init; } = [];

    public bool BootstrapCsrfTokenFound { get; init; }

    public string BodyPreview { get; init; } = string.Empty;

    public string ErrorType { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;

    public string InnerErrorMessage { get; init; } = string.Empty;
}
