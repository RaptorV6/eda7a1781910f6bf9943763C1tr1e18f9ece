namespace PortalComponent.Models;

public sealed class CitrixProbeRequest
{
    public string RequestId { get; init; } = string.Empty;

    public string Step { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string Method { get; init; } = "GET";

    public string? Body { get; init; }

    public string? ContentType { get; init; }

    public Dictionary<string, string> Headers { get; init; } = [];
}
