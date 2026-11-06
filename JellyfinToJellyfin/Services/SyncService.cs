using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using JellyfinToJellyfin.Configuration;
using JellyfinToJellyfin.Services.Remote;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace JellyfinToJellyfin.Services;

/// <summary>
/// Orchestrates mirroring of remote libraries into local .strm trees and local Jellyfin libraries.
/// </summary>
public sealed class SyncService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<SyncService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile SyncReport? _lastReport;

    public SyncService(ILibraryManager libraryManager, IServerApplicationHost appHost, ILogger<SyncService> logger)
    {
        _libraryManager = libraryManager;
        _appHost = appHost;
        _logger = logger;
    }

    public bool IsSyncing => _gate.CurrentCount == 0;

    public SyncReport? LastReport => _lastReport;

    /// <summary>
    /// Starts a sync in the background. Returns false when a sync is already running.
    /// </summary>
    public bool TryStartSync(string? instanceId)
    {
        if (!_gate.Wait(0))
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(instanceId))
                {
                    var instance = Plugin.Instance?.Configuration.Instances
                        .FirstOrDefault(i => string.Equals(i.Id, instanceId, StringComparison.OrdinalIgnoreCase));

                    if (instance is null)
                    {
                        var missing = new SyncReport { InstanceName = instanceId, Success = false };
                        missing.AddError($"Instance '{instanceId}' not found.");
                        _lastReport = missing;
                        return;
                    }

                    _lastReport = await SyncInstanceCoreAsync(instance, null, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                // SyncAllCoreAsync publishes its own aggregate report.
                await SyncAllCoreAsync(null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var report = new SyncReport { Success = false };
                report.AddError(ex.Message);
                _lastReport = report;
                _logger.LogError(ex, "Background sync failed");
            }
            finally
            {
                _gate.Release();
            }
        });

        return true;
    }

    public async Task<SyncReport> SyncAllAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A sync is already running.");
        }

        try
        {
            return await SyncAllCoreAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SyncReport> SyncAllCoreAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var aggregate = new SyncReport { InstanceName = "All instances" };
        var sw = Stopwatch.StartNew();
        try
        {
            var instances = Plugin.Instance!.Configuration.Instances.Where(i => i.Enabled).ToList();
            if (instances.Count == 0)
            {
                aggregate.AddWarning("No enabled instances configured.");
                aggregate.Success = true;
                return aggregate;
            }

            for (var i = 0; i < instances.Count; i++)
            {
                var index = i;
                var sub = new Progress<double>(p => progress?.Report((index + p) / instances.Count));
                var report = await SyncInstanceCoreAsync(instances[index], sub, cancellationToken).ConfigureAwait(false);
                aggregate.Merge(report);
            }

            _libraryManager.QueueLibraryScan();
            aggregate.Success = aggregate.Errors.Count == 0;
            return aggregate;
        }
        finally
        {
            sw.Stop();
            aggregate.DurationSeconds = sw.Elapsed.TotalSeconds;
            _lastReport = aggregate;
        }
    }

    public async Task<SyncReport> SyncInstanceAsync(SourceInstance instance, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A sync is already running.");
        }

        try
        {
            return await SyncInstanceCoreAsync(instance, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SyncReport> SyncInstanceCoreAsync(SourceInstance instance, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var report = new SyncReport { InstanceName = instance.Name };
        var sw = Stopwatch.StartNew();
        var cancelled = false;
        try
        {
            if (string.IsNullOrWhiteSpace(instance.BaseUrl))
            {
                report.AddError("Base URL is not configured.");
                return report;
            }

            if (string.IsNullOrWhiteSpace(instance.Username))
            {
                report.AddError("No remote user configured. Set a user name and password for this instance.");
                return report;
            }

            var mappings = instance.Libraries.Where(m => m.Enabled).ToList();
            if (mappings.Count == 0)
            {
                report.AddWarning("No enabled library mappings for this instance.");
                return report;
            }

            var localBaseUrl = ResolveLocalBaseUrl(instance, report);
            if (string.IsNullOrWhiteSpace(localBaseUrl))
            {
                report.AddError("Could not determine a local server URL. Set 'LocalServerUrl'/'ServerUrlOverride'.");
                return report;
            }

            using var client = new RemoteJellyfinClient(instance.BaseUrl, instance.Username, instance.Password);
            List<RemoteLibrary> remoteLibraries;
            try
            {
                remoteLibraries = await client.GetLibrariesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                report.AddError($"Cannot reach remote '{instance.BaseUrl}': {ex.Message}");
                return report;
            }

            _logger.LogInformation("Sync '{Name}': {Count} remote libraries, local base '{Url}'", instance.Name, remoteLibraries.Count, localBaseUrl);

            for (var i = 0; i < mappings.Count; i++)
            {
                var index = i;
                await SyncMappingAsync(instance, mappings[index], client, remoteLibraries, localBaseUrl, report, p => progress?.Report((index + p) / mappings.Count), cancellationToken).ConfigureAwait(false);
            }

            _libraryManager.QueueLibraryScan();
            report.Success = report.Errors.Count == 0;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            report.AddWarning("Sync was cancelled.");
        }
        catch (Exception ex)
        {
            report.AddError(ex.Message);
            _logger.LogError(ex, "Sync failed for instance {Name}", instance.Name);
        }
        finally
        {
            sw.Stop();
            report.DurationSeconds = sw.Elapsed.TotalSeconds;
            instance.LastSyncUtc = DateTime.UtcNow;
            instance.LastSyncStatus = cancelled ? "Cancelled" : report.Summary();
            SaveConfig();
        }

        return report;
    }

    private async Task SyncMappingAsync(
        SourceInstance instance,
        LibraryMapping mapping,
        RemoteJellyfinClient client,
        IReadOnlyList<RemoteLibrary> remoteLibraries,
        string localBaseUrl,
        SyncReport report,
        Action<double> progress,
        CancellationToken cancellationToken)
    {
        var remote = FindRemoteLibrary(remoteLibraries, mapping);
        if (remote is null || string.IsNullOrEmpty(remote.ItemId))
        {
            report.AddError($"Remote library '{DisplayName(mapping)}' was not found on the source server.");
            return;
        }

        var libraryDir = GetLibraryDirectory(instance, mapping, remote);
        try
        {
            Directory.CreateDirectory(libraryDir);
        }
        catch (Exception ex)
        {
            report.AddError($"Cannot create output folder '{libraryDir}': {ex.Message}");
            return;
        }

        try
        {
            await EnsureLocalLibraryAsync(mapping, remote, libraryDir, report).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            report.AddWarning($"Could not update local library configuration for '{remote.Name}': {ex.Message}");
        }

        progress(0.02);

        RemoteItemsResult result;
        try
        {
            result = await client.GetItemsAsync(remote.ItemId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            report.AddError($"Failed to query items for '{remote.Name}': {ex.Message}");
            return;
        }

        var items = result.Items ?? new List<RemoteItem>();
        progress(0.15);

        var generator = new MirrorGenerator(client, instance.Id, localBaseUrl, instance.DownloadImages, instance.RemoveStaleFiles, _logger);
        await generator.GenerateAsync(items, libraryDir, cancellationToken).ConfigureAwait(false);

        report.Warnings.AddRange(generator.Warnings);
        report.StrmWritten += generator.StrmWritten;
        report.NfoWritten += generator.NfoWritten;
        report.ImagesWritten += generator.ImagesWritten;
        report.DuplicatesSkipped += generator.DuplicatesSkipped;
        report.StaleRemoved += generator.StaleRemoved;
        report.ItemsSeen += items.Count;
        report.LibrariesSynced++;

        _logger.LogInformation(
            "Mirrored {Count} items from '{Remote}' into '{Dir}' ({Strm} strm, {Nfo} nfo, {Images} images, {Duplicates} duplicate paths, {Stale} stale removed)",
            items.Count,
            remote.Name,
            libraryDir,
            generator.StrmWritten,
            generator.NfoWritten,
            generator.ImagesWritten,
            generator.DuplicatesSkipped,
            generator.StaleRemoved);

        progress(1);
    }

    private async Task EnsureLocalLibraryAsync(LibraryMapping mapping, RemoteLibrary remote, string libraryDir, SyncReport report)
    {
        var desiredName = !string.IsNullOrWhiteSpace(mapping.TargetLibraryName) ? mapping.TargetLibraryName : remote.Name;
        var libraryName = string.IsNullOrWhiteSpace(desiredName) ? "Library" : desiredName!;
        var typeString = string.IsNullOrWhiteSpace(mapping.TargetCollectionType) ? remote.GetEffectiveCollectionType() : mapping.TargetCollectionType;
        var collectionType = MapCollectionType(typeString);

        var virtualFolders = _libraryManager.GetVirtualFolders();

        // An explicit target id wins, then an existing library with the same name.
        var existing = !string.IsNullOrWhiteSpace(mapping.TargetLibraryId)
            ? virtualFolders.FirstOrDefault(v => string.Equals(v.ItemId, mapping.TargetLibraryId, StringComparison.OrdinalIgnoreCase))
            : null;

        existing ??= virtualFolders.FirstOrDefault(v => string.Equals(v.Name, libraryName, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            if (!mapping.CreateNewLibrary)
            {
                report.AddWarning($"Local library '{libraryName}' does not exist and automatic creation is disabled for this mapping.");
                return;
            }

            var options = new LibraryOptions { PathInfos = new[] { new MediaPathInfo(libraryDir) } };
            await _libraryManager.AddVirtualFolder(libraryName, collectionType, options, false).ConfigureAwait(false);
            report.AddNote($"Created local library '{libraryName}' ({collectionType?.ToString() ?? "mixed"}).");
            return;
        }

        var locations = existing.Locations ?? Array.Empty<string>();
        if (!locations.Any(l => PathSanitizer.SamePath(l, libraryDir)))
        {
            _libraryManager.AddMediaPath(existing.Name, new MediaPathInfo(libraryDir));
            report.AddNote($"Attached '{libraryDir}' to existing library '{existing.Name}'.");
        }
    }

    private static RemoteLibrary? FindRemoteLibrary(IReadOnlyList<RemoteLibrary> libraries, LibraryMapping mapping)
    {
        if (!string.IsNullOrWhiteSpace(mapping.RemoteLibraryId))
        {
            var byId = libraries.FirstOrDefault(l => string.Equals(l.ItemId, mapping.RemoteLibraryId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId;
            }
        }

        if (!string.IsNullOrWhiteSpace(mapping.RemoteLibraryName))
        {
            var byName = libraries.FirstOrDefault(l => string.Equals(l.Name, mapping.RemoteLibraryName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName;
            }
        }

        return null;
    }

    private static string DisplayName(LibraryMapping mapping)
        => !string.IsNullOrWhiteSpace(mapping.RemoteLibraryName)
            ? mapping.RemoteLibraryName
            : (string.IsNullOrWhiteSpace(mapping.RemoteLibraryId) ? "(unnamed)" : mapping.RemoteLibraryId);

    private static string GetLibraryDirectory(SourceInstance instance, LibraryMapping mapping, RemoteLibrary remote)
    {
        var config = Plugin.Instance!.Configuration;
        var root = !string.IsNullOrWhiteSpace(instance.BasePath)
            ? instance.BasePath!
            : Path.Combine(config.DefaultBasePath, "JellyfinToJellyfin", instance.Id);

        var folder = !string.IsNullOrWhiteSpace(mapping.FolderName)
            ? mapping.FolderName
            : (!string.IsNullOrWhiteSpace(remote.Name) ? remote.Name : "Library");

        return Path.Combine(root, PathSanitizer.Sanitize(folder, "Library"));
    }

    private string ResolveLocalBaseUrl(SourceInstance instance, SyncReport report)
    {
        var config = Plugin.Instance!.Configuration;
        var configured = !string.IsNullOrWhiteSpace(instance.LocalServerUrl)
            ? instance.LocalServerUrl
            : config.ServerUrlOverride;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return RemoteJellyfinClient.NormalizeBaseUrl(configured);
        }

        try
        {
            var smart = _appHost.GetSmartApiUrl(Environment.MachineName);
            if (!string.IsNullOrWhiteSpace(smart)
                && !smart.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                && !smart.Contains("127.0.0.1", StringComparison.Ordinal))
            {
                report.AddNote($"Auto-detected local URL '{smart}'. Set 'LocalServerUrl'/'ServerUrlOverride' to pin it.");
                return RemoteJellyfinClient.NormalizeBaseUrl(smart);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetSmartApiUrl failed while resolving the local base URL");
        }

        var ip = GetLanIPv4();
        var scheme = _appHost.ListenWithHttps ? "https" : "http";
        var port = _appHost.ListenWithHttps && _appHost.HttpsPort > 0
            ? _appHost.HttpsPort
            : (_appHost.HttpPort > 0 ? _appHost.HttpPort : 8096);
        var fallback = ip is not null ? $"{scheme}://{ip}:{port}" : $"{scheme}://localhost:{port}";
        report.AddWarning($"Using fallback local URL '{fallback}'. Configure 'LocalServerUrl'/'ServerUrlOverride' for reliable playback.");
        return fallback;
    }

    private static string? GetLanIPv4()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    var address = unicast.Address.ToString();
                    if (address.StartsWith("127.", StringComparison.Ordinal) || address.StartsWith("169.254.", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    return address;
                }
            }
        }
        catch
        {
            // Best effort only.
        }

        return null;
    }

    private static CollectionTypeOptions? MapCollectionType(string? type)
    {
        switch ((type ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "movies":
                return CollectionTypeOptions.movies;
            case "tvshows":
                return CollectionTypeOptions.tvshows;
            case "music":
                return CollectionTypeOptions.music;
            case "musicvideos":
                return CollectionTypeOptions.musicvideos;
            case "homevideos":
            case "videos":
                return CollectionTypeOptions.homevideos;
            case "boxsets":
                return CollectionTypeOptions.boxsets;
            case "books":
                return CollectionTypeOptions.books;
            default:
                return CollectionTypeOptions.mixed;
        }
    }

    private static void SaveConfig()
    {
        try
        {
            Plugin.Instance?.SaveConfiguration();
        }
        catch
        {
            // Never let a config write failure abort the sync.
        }
    }
}



