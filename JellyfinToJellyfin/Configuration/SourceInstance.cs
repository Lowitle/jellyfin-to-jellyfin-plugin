using System;
using System.Collections.Generic;

namespace JellyfinToJellyfin.Configuration;

/// <summary>
/// A remote Jellyfin instance that this plugin mirrors content from.
/// </summary>
public class SourceInstance
{
    /// <summary>
    /// Gets or sets the instance identifier, used in the output folder and in stream URLs (e.g. "instancia1").
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a human friendly display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the remote base URL (e.g. http://192.168.1.20:8096).
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the remote API key. This value is never written to .strm files.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the output base path for this instance. When empty, "{DefaultBasePath}/{Id}" is used.
    /// </summary>
    public string BasePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this instance participates in scheduled/global syncs.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a per-instance local base URL used inside .strm files. Overrides the global value when set.
    /// </summary>
    public string LocalServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether posters/backdrops/artwork are downloaded from the remote.
    /// </summary>
    public bool DownloadImages { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether mirror files that no longer map to a remote item are deleted
    /// after a sync. Only generated .strm/.nfo files (and artwork when <see cref="DownloadImages"/> is set)
    /// inside the mapped library folder are touched, and the pass is skipped when the sync failed.
    /// </summary>
    public bool RemoveStaleFiles { get; set; }

    /// <summary>
    /// Gets or sets the mapping of remote libraries to local libraries.
    /// </summary>
    public List<LibraryMapping> Libraries { get; set; } = new();

    /// <summary>
    /// Gets or sets the last successful sync date (UTC).
    /// </summary>
    public DateTime? LastSyncUtc { get; set; }

    /// <summary>
    /// Gets or sets the last sync status message.
    /// </summary>
    public string LastSyncStatus { get; set; } = string.Empty;
}
