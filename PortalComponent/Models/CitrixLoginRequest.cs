namespace PortalComponent.Models;

public sealed class CitrixLoginRequest
{
    public string RequestId { get; init; } = string.Empty;

    public string StoreRootUrl { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public string Domain { get; init; } = string.Empty;
}
