using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyfinToJellyfin.Services;

/// <summary>
/// Kicks off a mirror sync shortly after the server has finished starting up when enabled in the configuration.
/// </summary>
public sealed class StartupSyncService : IHostedService
{
    private readonly SyncService _syncService;
    private readonly ILogger<StartupSyncService> _logger;

    public StartupSyncService(SyncService syncService, ILogger<StartupSyncService> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.SyncOnStartup != true)
        {
            return Task.CompletedTask;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    // Give the host a moment so libraries and network are ready.
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Starting startup mirror sync");
                    if (!_syncService.TryStartSync(null))
                    {
                        _logger.LogInformation("Startup mirror sync skipped: another sync is already running");
                    }
                }
                catch (OperationCanceledException)
                {
                    // Host shutting down.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Startup mirror sync failed");
                }
            },
            CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
