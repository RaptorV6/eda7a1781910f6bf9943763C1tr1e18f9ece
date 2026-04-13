namespace PortalComponent.Models;

public sealed class CitrixClientLogEntry
{
    public string RequestId { get; init; } = string.Empty;

    public string Level { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string Step { get; init; } = string.Empty;

    public string BrowserTimestamp { get; init; } = string.Empty;

    public string PagePath { get; init; } = string.Empty;
}
