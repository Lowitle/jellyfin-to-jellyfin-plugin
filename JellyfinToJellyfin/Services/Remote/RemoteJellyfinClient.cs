using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JellyfinToJellyfin.Services.Remote;

/// <summary>
/// HTTP client for a remote Jellyfin instance.
/// Authenticates by logging in as a regular user (POST /Users/AuthenticateByName): the session token is
/// cached per (base URL, user), shared by the sync and the stream proxy, and refreshed on 401.
/// Credentials never leave this client: they are not written to .strm files nor exposed by the plugin API.
/// </summary>
public sealed class RemoteJellyfinClient : IDisposable
{
    private const string DeviceName = "Jellyfin to Jellyfin";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    // Session tokens obtained from /Users/AuthenticateByName, keyed by "baseUrl|username" so that the
    // stream proxy and the sync service share a single session per remote account. Memory only.
    private static readonly ConcurrentDictionary<string, string> TokenCache = new(StringComparer.Ordinal);

    // Serializes logins so a burst of requests does not open one remote session per call.
    private static readonly SemaphoreSlim LoginLock = new(1, 1);

    private static readonly string DeviceId = BuildDeviceId();

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
    private readonly string _username;
    private readonly string _password;
    private readonly string _tokenCacheKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteJellyfinClient"/> class that logs in with the given
    /// remote user.
    /// </summary>
    /// <param name="baseUrl">Remote base URL.</param>
    /// <param name="username">Remote user name.</param>
    /// <param name="password">Remote password.</param>
    public RemoteJellyfinClient(string baseUrl, string username, string password)
        : this(baseUrl, username, password, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteJellyfinClient"/> class with an explicit message
    /// handler, so the request/response flow can be exercised without a real server.
    /// </summary>
    /// <param name="baseUrl">Remote base URL.</param>
    /// <param name="username">Remote user name.</param>
    /// <param name="password">Remote password.</param>
    /// <param name="handler">Message handler to use instead of a socket handler.</param>
    public RemoteJellyfinClient(string baseUrl, string username, string password, HttpMessageHandler? handler)
    {
        BaseUrl = NormalizeBaseUrl(baseUrl);
        _username = (username ?? string.Empty).Trim();
        _password = password ?? string.Empty;
        _tokenCacheKey = BaseUrl + "|" + _username;

        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false
            })
            {
                Timeout = TimeSpan.FromSeconds(100)
            }
            : new HttpClient(handler, disposeHandler: false)
            {
                Timeout = TimeSpan.FromSeconds(100)
            };

        if (BaseUrl.Length > 0)
        {
            _http.BaseAddress = new Uri(BaseUrl);
        }

        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DeviceName);
    }

    /// <summary>Gets the remote base URL, without a trailing slash.</summary>
    public string BaseUrl { get; }

    /// <summary>Gets the configured user name.</summary>
    public string Username => _username;

    /// <summary>Gets a value indicating whether the client has a user name to log in with.</summary>
    public bool HasCredentials => _username.Length > 0;

    /// <summary>Gets the session token currently in use, if one has been obtained.</summary>
    public string? AccessToken { get; private set; }

    /// <summary>Trims a configured base URL and removes the trailing slash.</summary>
    /// <param name="url">Configured URL.</param>
    /// <returns>The normalised URL.</returns>
    public static string NormalizeBaseUrl(string? url)
        => string.IsNullOrWhiteSpace(url) ? string.Empty : url!.Trim().TrimEnd('/');

    /// <summary>Returns true when the stream kind refers to audio.</summary>
    /// <param name="kind">Stream kind.</param>
    /// <returns>Whether the kind is audio.</returns>
    public static bool IsAudioKind(string? kind)
        => string.Equals(kind, "audio", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the remote original-stream URL for an item (static = raw file, no remote transcoding).
    /// </summary>
    /// <param name="baseUrl">Remote base URL.</param>
    /// <param name="itemId">Remote item id.</param>
    /// <param name="kind">Stream kind: video (default) or audio.</param>
    /// <returns>The absolute stream URL.</returns>
    public static string BuildStreamUrl(string baseUrl, string itemId, string? kind)
    {
        var controller = IsAudioKind(kind) ? "Audio" : "Videos";
        return $"{NormalizeBaseUrl(baseUrl)}/{controller}/{Uri.EscapeDataString(itemId)}/stream?static=true";
    }

    /// <summary>
    /// Builds the MediaBrowser authorization header Jellyfin expects when a client opens a session.
    /// It identifies the plugin and a stable device id and never contains a credential.
    /// </summary>
    /// <returns>The authorization header value.</returns>
    public static string BuildAuthorizationHeader()
        => $"MediaBrowser Client=\"{DeviceName}\", Device=\"{DeviceName}\", DeviceId=\"{DeviceId}\", Version=\"{PluginVersion}\"";

    private static string PluginVersion
        => typeof(RemoteJellyfinClient).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

    // Stable, short device id so every sync from this server shows up as a single remote session.
    private static string BuildDeviceId()
    {
        uint hash = 2166136261;
        foreach (var character in Environment.MachineName)
        {
            hash = (hash ^ character) * 16777619;
        }

        return "j2j-" + hash.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns the session token to send in the X-Emby-Token header, logging in when there is no cached one.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session token.</returns>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!HasCredentials)
        {
            throw new InvalidOperationException($"No remote credentials configured for {BaseUrl}: set a user name and password.");
        }

        if (TokenCache.TryGetValue(_tokenCacheKey, out var cached))
        {
            AccessToken = cached;
            return cached;
        }

        await LoginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TokenCache.TryGetValue(_tokenCacheKey, out cached))
            {
                AccessToken = cached;
                return cached;
            }

            var token = await LoginAsync(cancellationToken).ConfigureAwait(false);
            TokenCache[_tokenCacheKey] = token;
            AccessToken = token;
            return token;
        }
        finally
        {
            LoginLock.Release();
        }
    }

    /// <summary>Drops the cached session token so the next request logs in again.</summary>
    public void InvalidateAccessToken()
    {
        AccessToken = null;
        TokenCache.TryRemove(_tokenCacheKey, out _);
    }

    /// <summary>
    /// Sends a media stream request through the shared <see cref="ProxyClient"/>, attaching the configured
    /// credential and retrying once when the session expired.
    /// </summary>
    /// <param name="requestFactory">Builds the upstream request; called again when a retry is needed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The upstream response with headers only.</returns>
    public Task<HttpResponseMessage> SendStreamRequestAsync(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
        => SendAsync(ProxyClient, requestFactory, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    // Opens a session with POST /Users/AuthenticateByName and returns its access token.
    private async Task<string> LoginAsync(CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { Username = _username, Pw = _password });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", BuildAuthorizationHeader());

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Remote Jellyfin login failed for user '{_username}' on {BaseUrl}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Check the user name, the password and that remote connections are allowed for that account.",
                null,
                response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<AuthenticationResult>(JsonOptions, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result?.AccessToken))
        {
            throw new HttpRequestException(
                $"Remote Jellyfin login for user '{_username}' on {BaseUrl} returned no access token.",
                null,
                response.StatusCode);
        }

        return result!.AccessToken!;
    }

    // Sends a request with the current session token and retries once after a 401 so a revoked session
    // (logout everywhere, server reset) heals itself.
    private async Task<HttpResponseMessage> SendAsync(HttpClient httpClient, Func<HttpRequestMessage> requestFactory, HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendOnceAsync(httpClient, requestFactory, token, completionOption, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        InvalidateAccessToken();
        token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        return await SendOnceAsync(httpClient, requestFactory, token, completionOption, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> SendOnceAsync(HttpClient httpClient, Func<HttpRequestMessage> requestFactory, string token, HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        var request = requestFactory();
        try
        {
            if (token.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("X-Emby-Token", token);
            }

            return await httpClient.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // GET requests carry no content, so the message itself can be disposed once it is in flight.
            request.Dispose();
        }
    }

    // Minimal shape of the /Users/AuthenticateByName response: only the token is needed.
    private sealed class AuthenticationResult
    {
        public string? AccessToken { get; set; }
    }

    /// <summary>Gets the remote server information (GET /System/Info). Any authenticated user can call it.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Server information, or null.</returns>
    public async Task<ServerInfo?> GetServerInfoAsync(CancellationToken cancellationToken)
        => await GetAsync<ServerInfo>("/System/Info", cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Lists the libraries visible to the logged in user (GET /UserViews). This is the non-elevated way to
    /// enumerate libraries: /Library/VirtualFolders requires administrator rights, which the plugin
    /// deliberately does not use.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The remote libraries available to the configured user.</returns>
    public async Task<List<RemoteLibrary>> GetLibrariesAsync(CancellationToken cancellationToken)
    {
        var result = await GetAsync<RemoteItemsResult>(
            "/UserViews?includeExternalContent=false&includeHidden=false",
            cancellationToken).ConfigureAwait(false);

        return (result?.Items ?? new List<RemoteItem>())
            .Where(i => !string.IsNullOrEmpty(i.Id) && string.Equals(i.Type, "CollectionFolder", StringComparison.OrdinalIgnoreCase))
            .Select(i => new RemoteLibrary
            {
                Name = i.Name,
                ItemId = i.Id,
                CollectionType = i.CollectionType
            })
            .ToList();
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

    /// <summary>Downloads an item image, or returns null when the remote has none.</summary>
    /// <param name="itemId">Remote item id.</param>
    /// <param name="imageType">Image type (Primary, Backdrop, ...).</param>
    /// <param name="maxWidth">Maximum width requested from the remote.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The image bytes, or null.</returns>
    public async Task<byte[]?> GetImageAsync(string itemId, string imageType, int maxWidth, CancellationToken cancellationToken)
    {
        var url = "/Items/" + Uri.EscapeDataString(itemId)
                  + "/Images/" + Uri.EscapeDataString(imageType)
                  + "?maxWidth=" + maxWidth.ToString(CultureInfo.InvariantCulture)
                  + "&quality=90";

        using var response = await SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> GetAsync<T>(string relativeUrl, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(_http, () => new HttpRequestMessage(HttpMethod.Get, relativeUrl), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
