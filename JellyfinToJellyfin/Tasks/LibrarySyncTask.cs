using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JellyfinToJellyfin.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JellyfinToJellyfin.Tasks;

/// <summary>
/// Scheduled task that regenerates the mirror for every enabled remote instance.
/// </summary>
public sealed class LibrarySyncTask : IScheduledTask
{
    private readonly SyncService _syncService;
    private readonly ILogger<LibrarySyncTask> _logger;

    public LibrarySyncTask(SyncService syncService, ILogger<LibrarySyncTask> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Jellyfin to Jellyfin mirror sync";

    /// <inheritdoc />
    public string Key => "JellyfinToJellyfinMirrorSync";

    /// <inheritdoc />
    public string Description => "Rebuilds the local .strm/.nfo mirror tree from all enabled remote Jellyfin instances.";

    /// <inheritdoc />
    public string Category => "Jellyfin to Jellyfin";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var triggers = new List<TaskTriggerInfo>();

        // Mirrors the 'sync on startup' option so the mirror refreshes even when the plugin hosted service
        // is not started by the host.
        if (Plugin.Instance?.Configuration.SyncOnStartup == true)
        {
            triggers.Add(new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger });
        }

        var hours = Plugin.Instance?.Configuration.ScheduleIntervalHours ?? 0;
        if (hours > 0)
        {
            triggers.Add(new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(hours).Ticks
            });
        }

        return triggers;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            var report = await _syncService.SyncAllAsync(progress, cancellationToken).ConfigureAwait(false);
            if (!report.Success)
            {
                _logger.LogWarning("Mirror sync finished with {Count} error(s)", report.Errors.Count);
            }
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogInformation("Mirror sync skipped: {Message}", ex.Message);
        }
    }
}
