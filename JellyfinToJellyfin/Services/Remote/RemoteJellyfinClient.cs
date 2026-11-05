using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JellyfinToJellyfin.Services.Remote;

/// <summary>
/// HTTP client for a remote Jellyfin instance. Authenticates with an API key sent as the X-Emby-Token header.
/// The API key is never exposed outside of this client.
/// </summary>
public sealed class RemoteJellyfinClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// Shared client used to proxy media streams. Infinite timeout so long playback sessions are not cut off.
    /// </summary>
    public static readonly HttpClient ProxyClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        AllowAutoRedirect = true,
        UseCookies = false
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly HttpClient _http;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteJellyfinClient"/> class.
    /// </summary>
    public RemoteJellyfinClient(string baseUrl, string apiKey)
    {
        BaseUrl = NormalizeBaseUrl(baseUrl);
        ApiKey = apiKey ?? string.Empty;

        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false
        })
        {
            Timeout = TimeSpan.FromSeconds(100)
        };

        if (BaseUrl.Length > 0)
        {
            _http.BaseAddress = new Uri(BaseUrl);
        }

        if (ApiKey.Length > 0)
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Token", ApiKey);
        }

        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "JellyfinToJellyfin");
    }

    public string BaseUrl { get; }

    public string ApiKey { get; }

    public static string NormalizeBaseUrl(string? url)
        => string.IsNullOrWhiteSpace(url) ? string.Empty : url!.Trim().TrimEnd('/');

    public static bool IsAudioKind(string? kind)
        => string.Equals(kind, "audio", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the remote original-stream URL for an item (static = raw file, no remote transcoding).
    /// </summary>
    public static string BuildStreamUrl(string baseUrl, string itemId, string? kind)
    {
        var controller = IsAudioKind(kind) ? "Audio" : "Videos";
        return $"{NormalizeBaseUrl(baseUrl)}/{controller}/{Uri.EscapeDataString(itemId)}/stream?static=true";
    }

    public async Task<ServerInfo?> GetServerInfoAsync(CancellationToken cancellationToken)
        => await GetAsync<ServerInfo>("/System/Info", cancellationToken).ConfigureAwait(false);

    public async Task<List<RemoteLibrary>> GetLibrariesAsync(CancellationToken cancellationToken)
    {
        var libraries = await GetAsync<List<RemoteLibrary>>("/Library/VirtualFolders", cancellationToken).ConfigureAwait(false)
            ?? new List<RemoteLibrary>();

        if (libraries.Any(l => string.IsNullOrEmpty(l.ItemId)))
        {
            // Older servers do not expose ItemId on VirtualFolderInfo: resolve the CollectionFolder ids by name.
            var folders = await GetAsync<RemoteItemsResult>(
                "/Items?Recursive=true&IncludeItemTypes=CollectionFolder",
                cancellationToken).ConfigureAwait(false);

            foreach (var library in libraries.Where(l => string.IsNullOrEmpty(l.ItemId)))
            {
                var match = folders?.Items?.FirstOrDefault(f => string.Equals(f.Name, library.Name, StringComparison.OrdinalIgnoreCase));
                if (match is not null && !string.IsNullOrEmpty(match.Id))
                {
                    library.ItemId = match.Id;
                }
            }
        }

        return libraries;
    }

    /// <summary>
    /// Fetches every descendant item of a library, following server side pagination.
    /// </summary>
    /// <param name="parentId">Remote library (CollectionFolder) item id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All descendant items.</returns>
    public async Task<RemoteItemsResult> GetItemsAsync(string parentId, CancellationToken cancellationToken)
    {
        const int PageSize = 1000;

        // Only members of MediaBrowser.Model.Querying.ItemFields are valid here: anything else is
        // silently dropped by Jellyfin's CommaDelimitedCollectionModelBinder. Basics such as
        // ProductionYear, PremiereDate, RunTimeTicks, ratings, IndexNumber, SeriesId/SeasonId,
        // Album/Artists, MediaType, Container and ImageTags are always returned by /Items.
        const string Fields = "ParentId,Overview,Genres,ProviderIds,SortName,OriginalTitle";

        var merged = new RemoteItemsResult { Items = new List<RemoteItem>() };
        var startIndex = 0;

        while (true)
        {
            var url = "/Items?ParentId=" + Uri.EscapeDataString(parentId)
                      + "&Recursive=true"
                      + "&EnableImages=true"
                      + "&EnableUserData=false"
                      + "&CollapseBoxSetItems=false"
                      + "&StartIndex=" + startIndex.ToString(CultureInfo.InvariantCulture)
                      + "&Limit=" + PageSize.ToString(CultureInfo.InvariantCulture)
                      + "&Fields=" + Uri.EscapeDataString(Fields);

            var page = await GetAsync<RemoteItemsResult>(url, cancellationToken).ConfigureAwait(false);
            var items = page?.Items;
            if (items is { Count: > 0 })
            {
                merged.Items!.AddRange(items);
            }

            var total = page?.TotalRecordCount ?? 0;
            merged.TotalRecordCount = total > 0 ? total : merged.Items!.Count;

            if (items is null || items.Count < PageSize || (total > 0 && merged.Items!.Count >= total))
            {
                break;
            }

            startIndex += items.Count;
            cancellationToken.ThrowIfCancellationRequested();
        }

        return merged;
    }

    public async Task<byte[]?> GetImageAsync(string itemId, string imageType, int maxWidth, CancellationToken cancellationToken)
    {
        var url = "/Items/" + Uri.EscapeDataString(itemId)
                  + "/Images/" + Uri.EscapeDataString(imageType)
                  + "?maxWidth=" + maxWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)
                  + "&quality=90";

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> GetAsync<T>(string relativeUrl, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(relativeUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
