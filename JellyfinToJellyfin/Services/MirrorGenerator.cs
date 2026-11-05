using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JellyfinToJellyfin.Services.Remote;
using Microsoft.Extensions.Logging;

namespace JellyfinToJellyfin.Services;

/// <summary>
/// Turns a flat list of remote items into a local .strm/.nfo/artwork mirror on disk.
/// </summary>
public sealed class MirrorGenerator
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly RemoteJellyfinClient _client;
    private readonly string _instanceId;
    private readonly string _localBaseUrl;
    private readonly bool _downloadImages;
    private readonly ILogger _logger;

    // Tracks every mirror file this run intends to own, so a second source item mapping onto the same
    // path (duplicate titles, multi-version episodes, same-named folders) is reported instead of silently
    // overwriting an already generated file.
    private readonly HashSet<string> _targetPaths = new(StringComparer.OrdinalIgnoreCase);

    // Tracks every directory this run intends to own. Source libraries often hold two sibling folders
    // with the same name (or two identically titled movies); collapsing them into one directory would
    // silently drop the second subtree, so the later ones get the item id appended instead.
    private readonly HashSet<string> _targetDirs = new(StringComparer.OrdinalIgnoreCase);

    private int _dirRenameWarnings;

    private const int MaxDuplicateWarnings = 20;

    public MirrorGenerator(RemoteJellyfinClient client, string instanceId, string localBaseUrl, bool downloadImages, ILogger logger)
    {
        _client = client;
        _instanceId = instanceId;
        _localBaseUrl = RemoteJellyfinClient.NormalizeBaseUrl(localBaseUrl);
        _downloadImages = downloadImages;
        _logger = logger;
    }

    public int StrmWritten { get; private set; }

    public int NfoWritten { get; private set; }

    public int ImagesWritten { get; private set; }

    /// <summary>Gets the number of source items that mapped onto a mirror file another item already produced.</summary>
    public int DuplicatesSkipped { get; private set; }

    public List<string> Warnings { get; } = new();

    public async Task GenerateAsync(IReadOnlyList<RemoteItem> items, string libraryDir, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(libraryDir);

        var byId = items
            .Where(i => !string.IsNullOrEmpty(i.Id))
            .GroupBy(i => i.Id!)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var childrenByParent = items
            .Where(i => !string.IsNullOrEmpty(i.ParentId))
            .ToLookup(i => i.ParentId!, StringComparer.Ordinal);

        var roots = items
            .Where(i => string.IsNullOrEmpty(i.ParentId) || !byId.ContainsKey(i.ParentId!))
            .ToList();

        foreach (var root in roots)
        {
            await GenerateItemAsync(root, libraryDir, null, childrenByParent, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task GenerateItemAsync(RemoteItem item, string dir, string? seriesName, ILookup<string, RemoteItem> children, CancellationToken ct)
    {
        try
        {
            switch (item.Type)
            {
                case "Movie":
                    await WriteMovieAsync(item, ReserveDirectory(dir, MovieFolderName(item), item), ct).ConfigureAwait(false);
                    break;

                case "Series":
                    {
                        var seriesDir = ReserveDirectory(dir, SeriesFolderName(item), item);
                        await WriteSeriesAsync(item, seriesDir, ct).ConfigureAwait(false);
                        foreach (var child in GetChildren(children, item))
                        {
                            await GenerateItemAsync(child, seriesDir, item.Name, children, ct).ConfigureAwait(false);
                        }

                        break;
                    }

                case "Season":
                    {
                        var seasonDir = ReserveDirectory(dir, SeasonFolderName(item), item);
                        await WriteSeasonAsync(item, seasonDir, ct).ConfigureAwait(false);
                        foreach (var child in GetChildren(children, item))
                        {
                            await GenerateItemAsync(child, seasonDir, seriesName, children, ct).ConfigureAwait(false);
                        }

                        break;
                    }

                case "Episode":
                    await WriteEpisodeAsync(item, dir, seriesName, ct).ConfigureAwait(false);
                    break;

                case "MusicArtist":
                    {
                        var artistDir = ReserveDirectory(dir, PathSanitizer.Sanitize(item.Name, "Unknown Artist"), item);
                        await WriteArtistAsync(item, artistDir, ct).ConfigureAwait(false);
                        foreach (var child in GetChildren(children, item))
                        {
                            await GenerateItemAsync(child, artistDir, null, children, ct).ConfigureAwait(false);
                        }

                        break;
                    }

                case "MusicAlbum":
                    {
                        // The year keeps same-titled albums (greatest hits, reissues) in separate folders.
                        var albumName = PathSanitizer.Sanitize(item.Name, "Unknown Album");
                        var albumFolder = item.ProductionYear is int albumYear ? $"{albumName} ({albumYear})" : albumName;
                        var albumDir = ReserveDirectory(dir, albumFolder, item);
                        await WriteAlbumAsync(item, albumDir, ct).ConfigureAwait(false);
                        foreach (var child in GetChildren(children, item))
                        {
                            await GenerateItemAsync(child, albumDir, null, children, ct).ConfigureAwait(false);
                        }

                        break;
                    }

                case "Audio":
                    await WriteTrackAsync(item, dir, ct).ConfigureAwait(false);
                    break;

                case "Video":
                    await WriteGenericVideoAsync(item, ReserveDirectory(dir, PathSanitizer.Sanitize(item.Name, "Video"), item), ct).ConfigureAwait(false);
                    break;

                case "MusicVideo":
                    await WriteMusicVideoAsync(item, ReserveDirectory(dir, PathSanitizer.Sanitize(item.Name, "Music Video"), item), ct).ConfigureAwait(false);
                    break;

                case "BoxSet":
                case "Playlist":
                case "Photo":
                case "PhotoAlbum":
                case "Book":
                    // Not representable as an original-file stream; skip.
                    break;

                default:
                    await GenerateUnknownAsync(item, dir, seriesName, children, ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Warnings.Add($"Item '{item.Name}' ({item.Type}): {ex.Message}");
            _logger.LogWarning(ex, "Failed to mirror remote item {Name} ({Type})", item.Name, item.Type);
        }
    }

    private async Task GenerateUnknownAsync(RemoteItem item, string dir, string? seriesName, ILookup<string, RemoteItem> children, CancellationToken ct)
    {
        var kids = GetChildren(children, item).ToList();
        if (item.IsFolder == true && kids.Count > 0)
        {
            var fdir = ReserveDirectory(dir, PathSanitizer.Sanitize(item.Name, "Folder"), item);
            foreach (var child in kids)
            {
                await GenerateItemAsync(child, fdir, seriesName, children, ct).ConfigureAwait(false);
            }

            return;
        }

        if (string.Equals(item.MediaType, "Video", StringComparison.OrdinalIgnoreCase))
        {
            await WriteGenericVideoAsync(item, Path.Combine(dir, PathSanitizer.Sanitize(item.Name, "Video")), ct).ConfigureAwait(false);
        }
        else if (string.Equals(item.MediaType, "Audio", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTrackAsync(item, dir, ct).ConfigureAwait(false);
        }
    }

    private async Task WriteMovieAsync(RemoteItem item, string movieDir, CancellationToken ct)
    {
        Directory.CreateDirectory(movieDir);
        var baseName = Path.GetFileName(movieDir);
        await WriteStrmAsync(Path.Combine(movieDir, baseName + ".strm"), BuildUrl(item, "video"), ct).ConfigureAwait(false);
        await WriteNfoAsync(Path.Combine(movieDir, baseName + ".nfo"), NfoBuilder.Movie(item), ct).ConfigureAwait(false);
        await TryWriteImageAsync(item, "Primary", Path.Combine(movieDir, "poster.jpg"), 600, ct).ConfigureAwait(false);
        await TryWriteImageAsync(item, "Backdrop", Path.Combine(movieDir, "fanart.jpg"), 1920, ct).ConfigureAwait(false);
        await TryWriteImageAsync(item, "Logo", Path.Combine(movieDir, "logo.png"), 800, ct).ConfigureAwait(false);
        await TryWriteImageAsync(item, "Banner", Path.Combine(movieDir, "banner.jpg"), 1000, ct).ConfigureAwait(false);
    }

    private async Task WriteSeriesAsync(RemoteItem item, string seriesDir, CancellationToken ct)
    {
        Directory.CreateDirectory(seriesDir);
        await WriteNfoAsync(Path.Combine(seriesDir, "tvshow.nfo"), NfoBuilder.TvShow(item), ct).ConfigureAwait(false);
        await TryWriteImageAsync(item, "Primary", Path.Combine(seriesDir, "poster.jpg"), 600, ct).ConfigureAwait(false);
        await TryWriteImageAsync(item, "Backdrop", Path.Combine(seriesDir, "fanart.jpg"), 1920, ct).ConfigureAwait(false);
        await TryWriteImageAsync(item, "Logo", Path.Combine(seriesDir, "logo.png"), 800, ct).ConfigureAwait(false);
        await TryWriteImageAsync(item, "Banner", Path.Combine(seriesDir, "banner.jpg"), 1000, ct).ConfigureAwait(false);
    }

    private async Task WriteSeasonAsync(RemoteItem item, string seasonDir, CancellationToken ct)
    {
        Directory.CreateDirectory(seasonDir);
        var number = item.IndexNumber ?? 0;
        await WriteNfoAsync(Path.Combine(seasonDir, "season.nfo"), NfoBuilder.Season(number, item.Name), ct).ConfigureAwait(false);
        await TryWriteImageAsync(item, "Primary", Path.Combine(seasonDir, "folder.jpg"), 600, ct).ConfigureAwait(false);
        await TryWriteImageAsync(item, "Banner", Path.Combine(seasonDir, "banner.jpg"), 1000, ct).ConfigureAwait(false);
    }

    private async Task WriteEpisodeAsync(RemoteItem item, string dir, string? seriesName, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var baseName = EpisodeBaseName(item, seriesName);
        await WriteStrmAsync(Path.Combine(dir, baseName + ".strm"), BuildUrl(item, "video"), ct).ConfigureAwait(false);
        var season = item.ParentIndexNumber ?? 0;
        var episode = item.IndexNumber ?? 0;
        await WriteNfoAsync(Path.Combine(dir, baseName + ".nfo"), NfoBuilder.Episode(item, season, episode), ct).ConfigureAwait(false);
        await TryWriteImageAsync(item, "Primary", Path.Combine(dir, baseName + "-thumb.jpg"), 500, ct).ConfigureAwait(false);
    }

    private async Task WriteArtistAsync(RemoteItem item, string artistDir, CancellationToken ct)
    {
        Directory.CreateDirectory(artistDir);
        await TryWriteImageAsync(item, "Primary", Path.Combine(artistDir, "folder.jpg"), 600, ct).ConfigureAwait(false);
        await TryWriteImageAsync(item, "Backdrop", Path.Combine(artistDir, "backdrop.jpg"), 1920, ct).ConfigureAwait(false);
    }

    private async Task WriteAlbumAsync(RemoteItem item, string albumDir, CancellationToken ct)
    {
        Directory.CreateDirectory(albumDir);
        await WriteNfoAsync(Path.Combine(albumDir, "album.nfo"), NfoBuilder.Album(item), ct).ConfigureAwait(false);
        await TryWriteImageAsync(item, "Primary", Path.Combine(albumDir, "folder.jpg"), 600, ct).ConfigureAwait(false);
        await TryWriteImageAsync(item, "Backdrop", Path.Combine(albumDir, "backdrop.jpg"), 1920, ct).ConfigureAwait(false);
    }

    private async Task WriteTrackAsync(RemoteItem item, string dir, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var title = PathSanitizer.Sanitize(item.Name, "Track");
        var baseName = item.IndexNumber is int n ? $"{n:00} - {title}" : title;
        await WriteStrmAsync(Path.Combine(dir, baseName + ".strm"), BuildUrl(item, "audio"), ct).ConfigureAwait(false);
    }

    private async Task WriteGenericVideoAsync(RemoteItem item, string videoDir, CancellationToken ct)
    {
        Directory.CreateDirectory(videoDir);
        var baseName = Path.GetFileName(videoDir);
        await WriteStrmAsync(Path.Combine(videoDir, baseName + ".strm"), BuildUrl(item, "video"), ct).ConfigureAwait(false);
        await WriteNfoAsync(Path.Combine(videoDir, baseName + ".nfo"), NfoBuilder.Movie(item), ct).ConfigureAwait(false);
        await TryWriteImageAsync(item, "Primary", Path.Combine(videoDir, "poster.jpg"), 600, ct).ConfigureAwait(false);
        await TryWriteImageAsync(item, "Backdrop", Path.Combine(videoDir, "fanart.jpg"), 1920, ct).ConfigureAwait(false);
    }

    private async Task WriteMusicVideoAsync(RemoteItem item, string videoDir, CancellationToken ct)
    {
        Directory.CreateDirectory(videoDir);
        var baseName = Path.GetFileName(videoDir);
        await WriteStrmAsync(Path.Combine(videoDir, baseName + ".strm"), BuildUrl(item, "video"), ct).ConfigureAwait(false);
        await WriteNfoAsync(Path.Combine(videoDir, baseName + ".nfo"), NfoBuilder.MusicVideo(item), ct).ConfigureAwait(false);
        await TryWriteImageAsync(item, "Primary", Path.Combine(videoDir, "poster.jpg"), 600, ct).ConfigureAwait(false);
        await TryWriteImageAsync(item, "Backdrop", Path.Combine(videoDir, "fanart.jpg"), 1920, ct).ConfigureAwait(false);
    }

    private async Task WriteStrmAsync(string path, string url, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (!ClaimPath(path))
        {
            return;
        }

        if (await ContentMatchesAsync(path, url, ct).ConfigureAwait(false))
        {
            return;
        }

        await File.WriteAllTextAsync(path, url, Utf8NoBom, ct).ConfigureAwait(false);
        StrmWritten++;
    }

    private async Task WriteNfoAsync(string path, string content, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (!ClaimPath(path))
        {
            return;
        }

        if (await ContentMatchesAsync(path, content, ct).ConfigureAwait(false))
        {
            return;
        }

        await File.WriteAllTextAsync(path, content, Utf8NoBom, ct).ConfigureAwait(false);
        NfoWritten++;
    }

    /// <summary>
    /// Reserves a mirror directory for an item. When another item in this run already claimed the same
    /// path, the item id is appended so both subtrees are mirrored instead of one overwriting the other.
    /// </summary>
    /// <param name="parentDir">Directory that contains the new folder.</param>
    /// <param name="folderName">Sanitized folder name.</param>
    /// <param name="item">Source item owning the folder.</param>
    /// <returns>A directory path that is unique within this run.</returns>
    private string ReserveDirectory(string parentDir, string folderName, RemoteItem item)
    {
        var path = Path.Combine(parentDir, folderName);
        if (_targetDirs.Add(path))
        {
            return path;
        }

        var suffix = item.Id is { Length: >= 8 } id ? id[..8] : (DuplicatesSkipped + 1).ToString(CultureInfo.InvariantCulture);
        var candidate = path + " [" + suffix + "]";
        var attempt = 2;
        while (!_targetDirs.Add(candidate))
        {
            candidate = path + " [" + suffix + "-" + attempt.ToString(CultureInfo.InvariantCulture) + "]";
            attempt++;
        }

        _dirRenameWarnings++;
        if (_dirRenameWarnings <= MaxDuplicateWarnings)
        {
            Warnings.Add($"Duplicate folder name '{folderName}': '{item.Name}' ({item.Type}) was mirrored to '{Path.GetFileName(candidate)}' instead.");
        }
        else if (_dirRenameWarnings == MaxDuplicateWarnings + 1)
        {
            Warnings.Add("More duplicate folder names detected; further renames are applied but not listed.");
        }

        _logger.LogDebug("Renamed duplicate mirror folder {Path} to {Candidate}", path, candidate);
        return candidate;
    }

    /// <summary>
    /// Reserves a mirror path for the current item. Returns false when an earlier item already claimed it,
    /// which happens with duplicate titles, multi-version episodes and same-named source folders.
    /// </summary>
    private bool ClaimPath(string path)
    {
        if (_targetPaths.Add(path))
        {
            return true;
        }

        DuplicatesSkipped++;
        if (DuplicatesSkipped <= MaxDuplicateWarnings)
        {
            Warnings.Add($"Duplicate mirror path '{path}': keeping the first item, this one was skipped.");
        }
        else if (DuplicatesSkipped == MaxDuplicateWarnings + 1)
        {
            Warnings.Add("More duplicate mirror paths detected; further duplicates are counted but not listed.");
        }

        _logger.LogDebug("Skipped duplicate mirror path {Path}", path);
        return false;
    }

    private static async Task<bool> ContentMatchesAsync(string path, string content, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var existing = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return string.Equals(existing, content, StringComparison.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async Task TryWriteImageAsync(RemoteItem item, string imageType, string targetPath, int maxWidth, CancellationToken ct)
    {
        if (!_downloadImages || string.IsNullOrEmpty(item.Id) || !HasImage(item, imageType))
        {
            return;
        }

        if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
        {
            return;
        }

        try
        {
            var data = await _client.GetImageAsync(item.Id!, imageType, maxWidth, ct).ConfigureAwait(false);
            if (data is { Length: > 0 })
            {
                var parent = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                await File.WriteAllBytesAsync(targetPath, data, ct).ConfigureAwait(false);
                ImagesWritten++;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Warnings.Add($"Image '{imageType}' for '{item.Name}': {ex.Message}");
            _logger.LogDebug(ex, "Failed to download {Type} image for {Name}", imageType, item.Name);
        }
    }

    private static bool HasImage(RemoteItem item, string imageType)
        => imageType == "Backdrop" ? item.HasBackdrop : item.HasImage(imageType);

    private string BuildUrl(RemoteItem item, string kind)
        => $"{_localBaseUrl}/JellyfinToJellyfin/stream/{Uri.EscapeDataString(_instanceId)}/{Uri.EscapeDataString(item.Id!)}?k={kind}";

    private static IEnumerable<RemoteItem> GetChildren(ILookup<string, RemoteItem> children, RemoteItem item)
        => string.IsNullOrEmpty(item.Id) ? Array.Empty<RemoteItem>() : children[item.Id!];

    private static string MovieFolderName(RemoteItem item)
    {
        var name = PathSanitizer.Sanitize(item.Name, "Movie");
        return item.ProductionYear is int y ? $"{name} ({y})" : name;
    }

    private static string SeriesFolderName(RemoteItem item)
    {
        var name = PathSanitizer.Sanitize(item.Name, "Series");
        return item.ProductionYear is int y ? $"{name} ({y})" : name;
    }

    private static string SeasonFolderName(RemoteItem item)
    {
        var n = item.IndexNumber ?? 0;
        return n == 0 ? "Specials" : $"Season {n:00}";
    }

    private static string EpisodeBaseName(RemoteItem ep, string? seriesName)
    {
        var series = !string.IsNullOrWhiteSpace(ep.SeriesName) ? ep.SeriesName! : (seriesName ?? ep.Name ?? "Episode");
        var season = ep.ParentIndexNumber ?? 0;
        var e1 = ep.IndexNumber ?? 0;
        var name = $"{PathSanitizer.Sanitize(series)} S{season:00}E{e1:00}";
        if (ep.IndexNumberEnd is int end && end > e1)
        {
            name += $"-E{end:00}";
        }

        if (!string.IsNullOrWhiteSpace(ep.Name))
        {
            name += " - " + PathSanitizer.Sanitize(ep.Name);
        }

        return name;
    }
}



