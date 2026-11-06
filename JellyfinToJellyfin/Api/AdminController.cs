using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JellyfinToJellyfin.Configuration;
using JellyfinToJellyfin.Services;
using JellyfinToJellyfin.Services.Remote;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JellyfinToJellyfin.Api;

/// <summary>
/// Configuration and maintenance API used by the plugin configuration page.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("JellyfinToJellyfin/api")]
public sealed class AdminController : ControllerBase
{
    /// <summary>
    /// Configuration JSON options kept independent of the host MVC serializer so the shape of the
    /// configuration payload is stable for the embedded configuration page (PascalCase, case-insensitive reads).
    /// </summary>
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = null
    };

    private readonly SyncService _syncService;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminController"/> class.
    /// </summary>
    public AdminController(SyncService syncService, ILibraryManager libraryManager)
    {
        _syncService = syncService;
        _libraryManager = libraryManager;
    }

    /// <summary>Returns the current plugin configuration.</summary>
    /// <returns>The configuration.</returns>
    [HttpGet("config")]
    public ContentResult GetConfig()
        => ConfigContent(Plugin.Instance?.Configuration ?? new PluginConfiguration());

    /// <summary>Replaces and persists the plugin configuration.</summary>
    /// <param name="payload">New configuration payload.</param>
    /// <returns>The stored configuration.</returns>
    [HttpPost("config")]
    public IActionResult SaveConfig([FromBody] JsonElement payload)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return StatusCode(500, "Plugin is not initialised.");
        }

        PluginConfiguration? configuration;
        try
        {
            configuration = payload.Deserialize<PluginConfiguration>(ConfigJsonOptions);
        }
        catch (JsonException ex)
        {
            return BadRequest($"Invalid configuration payload: {ex.Message}");
        }

        if (configuration is null)
        {
            return BadRequest("Missing configuration payload.");
        }

        configuration.Instances ??= new List<SourceInstance>();
        var previousInstances = plugin.Configuration.Instances;
        foreach (var instance in configuration.Instances)
        {
            instance.Id = string.IsNullOrWhiteSpace(instance.Id) ? Guid.NewGuid().ToString("N")[..8] : instance.Id.Trim();
            instance.Libraries ??= new List<LibraryMapping>();

            // Credentials: trim the user name but never the password (leading/trailing spaces are legitimate).
            instance.BaseUrl = (instance.BaseUrl ?? string.Empty).Trim();
            instance.Username = (instance.Username ?? string.Empty).Trim();
            instance.Password ??= string.Empty;

            // Preserve sync bookkeeping: the configuration page never sends fresh values for these fields.
            var previous = previousInstances.FirstOrDefault(p => string.Equals(p.Id, instance.Id, StringComparison.OrdinalIgnoreCase));
            if (previous is not null)
            {
                instance.LastSyncUtc ??= previous.LastSyncUtc;
                if (string.IsNullOrEmpty(instance.LastSyncStatus))
                {
                    instance.LastSyncStatus = previous.LastSyncStatus;
                }
            }
        }

        plugin.UpdateConfiguration(configuration);
        plugin.SaveConfiguration();
        return ConfigContent(plugin.Configuration);
    }

    private static ContentResult ConfigContent(PluginConfiguration configuration)
        => new()
        {
            Content = JsonSerializer.Serialize(configuration, ConfigJsonOptions),
            ContentType = "application/json",
            StatusCode = 200
        };

    /// <summary>
    /// Performs a connectivity check against a remote server. The credentials travel in the JSON body so they
    /// never end up in request URLs, access logs or reverse proxy logs.
    /// </summary>
    /// <param name="request">Remote connection settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Connection result.</returns>
    [HttpPost("test")]
    public async Task<IActionResult> TestConnection([FromBody] RemoteConnectionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            return BadRequest("baseUrl is required.");
        }

        try
        {
            using var client = new RemoteJellyfinClient(request.BaseUrl, request.Username ?? string.Empty, request.Password ?? string.Empty);
            var info = await client.GetServerInfoAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new
            {
                ok = info is not null,
                serverName = info?.ServerName,
                version = info?.Version,
                id = info?.Id,
                user = client.Username
            });
        }
        catch (Exception ex)
        {
            return Ok(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>Lists the libraries available on a remote server. Credentials travel in the JSON body.</summary>
    /// <param name="request">Remote connection settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Remote libraries.</returns>
    [HttpPost("libraries")]
    public async Task<IActionResult> GetRemoteLibraries([FromBody] RemoteConnectionRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            return BadRequest("baseUrl is required.");
        }

        try
        {
            using var client = new RemoteJellyfinClient(request.BaseUrl, request.Username ?? string.Empty, request.Password ?? string.Empty);
            var libraries = await client.GetLibrariesAsync(cancellationToken).ConfigureAwait(false);
            return Ok(libraries.Select(l => new
            {
                name = l.Name,
                itemId = l.ItemId,
                collectionType = l.GetEffectiveCollectionType(),
                locations = l.Locations ?? Array.Empty<string>()
            }));
        }
        catch (Exception ex)
        {
            return Ok(new { error = ex.Message });
        }
    }

    /// <summary>Lists the local (server side) libraries so a mapping can attach to them.</summary>
    /// <returns>Local libraries.</returns>
    [HttpGet("local-libraries")]
    public IActionResult GetLocalLibraries()
        => Ok(_libraryManager.GetVirtualFolders().Select(v => new
        {
            name = v.Name,
            collectionType = v.CollectionType?.ToString(),
            locations = v.Locations ?? Array.Empty<string>()
        }));

    /// <summary>Starts a mirror sync in the background.</summary>
    /// <param name="instanceId">Optional instance id; omit to sync every enabled instance.</param>
    /// <returns>Whether the sync was started.</returns>
    [HttpPost("sync")]
    public IActionResult StartSync([FromQuery] string? instanceId)
    {
        if (!_syncService.TryStartSync(instanceId))
        {
            return Conflict("A sync is already running.");
        }

        return Ok(new { started = true });
    }

    /// <summary>Returns the sync status and per instance state.</summary>
    /// <returns>Sync status.</returns>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var instances = Plugin.Instance?.Configuration.Instances ?? new List<SourceInstance>();
        return Ok(new
        {
            isSyncing = _syncService.IsSyncing,
            lastReport = _syncService.LastReport?.ToDto(),
            instances = instances.Select(i => new
            {
                id = i.Id,
                name = i.Name,
                enabled = i.Enabled,
                lastSyncUtc = i.LastSyncUtc,
                lastSyncStatus = i.LastSyncStatus
            })
        });
    }
}

