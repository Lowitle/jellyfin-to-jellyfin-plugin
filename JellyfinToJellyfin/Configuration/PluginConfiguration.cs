using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace JellyfinToJellyfin.Configuration;

/// <summary>
/// Top level configuration for the Jellyfin to Jellyfin plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the default base directory where .strm trees are generated.
    /// Each instance writes into "{DefaultBasePath}/{instanceId}/".
    /// </summary>
    public string DefaultBasePath { get; set; } = "/media/jellyfin-to-jellyfin";

    /// <summary>
    /// Gets or sets an explicit base URL used inside the generated .strm files (e.g. http://192.168.1.10:8096).
    /// Leave empty to auto-detect the local server address.
    /// </summary>
    public string ServerUrlOverride { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether a sync runs once on server startup.
    /// </summary>
    public bool SyncOnStartup { get; set; }

    /// <summary>
    /// Gets or sets the default scheduled sync interval, in hours. 0 disables the scheduled task.
    /// </summary>
    public int ScheduleIntervalHours { get; set; } = 12;

    /// <summary>
    /// Gets or sets the configured remote source instances.
    /// </summary>
    public List<SourceInstance> Instances { get; set; } = new();
}
