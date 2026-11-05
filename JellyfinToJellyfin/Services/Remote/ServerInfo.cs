namespace JellyfinToJellyfin.Services.Remote;

/// <summary>
/// Subset of GET /System/Info used for connection tests.
/// </summary>
public sealed class ServerInfo
{
    public string? ServerName { get; set; }

    public string? Version { get; set; }

    public string? Id { get; set; }
}
