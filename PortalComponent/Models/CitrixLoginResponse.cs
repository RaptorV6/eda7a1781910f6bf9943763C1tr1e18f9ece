using System.Text.Json;

namespace PortalComponent.Models;

public sealed class CitrixLoginResponse
{
    public bool Ok { get; init; }

    public string RequestId { get; init; } = string.Empty;

    public bool LoginSucceeded { get; init; }

    public string Message { get; init; } = string.Empty;

    public string AuthResult { get; init; } = string.Empty;

    public int? BootstrapStatusCode { get; init; }

    public int? BootstrapLandingStatusCode { get; init; }

    public int? LoginFormStatusCode { get; init; }

    public int? LoginSubmitStatusCode { get; init; }

    public int? ResourcesStatusCode { get; init; }

    public int? AuthMethodsStatusCode { get; init; }

    public string BootstrapFinalUrl { get; init; } = string.Empty;

    public string BootstrapRedirectUrl { get; init; } = string.Empty;

    public string LoginFormUrl { get; init; } = string.Empty;

    public string LoginPostUrl { get; init; } = string.Empty;

    public string ResourcesUrl { get; init; } = string.Empty;

    public string AuthMethodsUrl { get; init; } = string.Empty;

    public string[] CookieNames { get; init; } = [];

    public bool CsrfTokenFound { get; init; }

    public Dictionary<string, string> BootstrapHeaders { get; init; } = [];

    public string BootstrapBodyPreview { get; init; } = string.Empty;

    public string BootstrapLandingPreview { get; init; } = string.Empty;

    public string LoginFormPreview { get; init; } = string.Empty;

    public string LoginSubmitPreview { get; init; } = string.Empty;

    public string ResourcesPreview { get; init; } = string.Empty;

    public string AuthMethodsPreview { get; init; } = string.Empty;

    public JsonElement? ResourcesPayload { get; init; }

    public string ErrorType { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;

    public string InnerErrorMessage { get; init; } = string.Empty;
}
