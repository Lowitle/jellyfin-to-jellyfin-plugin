namespace JellyfinToJellyfin.Api;

/// <summary>
/// Remote credentials supplied by the configuration page when testing a connection or listing libraries.
/// Kept in the request body on purpose: query strings end up in access logs and reverse proxy logs.
/// </summary>
public sealed class RemoteConnectionRequest
{
    /// <summary>Gets or sets the remote base URL.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Gets or sets the remote user name.</summary>
    public string? Username { get; set; }

    /// <summary>Gets or sets the remote password.</summary>
    public string? Password { get; set; }
}
