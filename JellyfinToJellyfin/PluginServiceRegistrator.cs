using JellyfinToJellyfin.Services;
using JellyfinToJellyfin.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace JellyfinToJellyfin;

/// <summary>
/// Registers the plugin services with the Jellyfin service container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<SyncService>();
        serviceCollection.AddSingleton<IScheduledTask, LibrarySyncTask>();
        serviceCollection.AddHostedService<StartupSyncService>();
    }
}
