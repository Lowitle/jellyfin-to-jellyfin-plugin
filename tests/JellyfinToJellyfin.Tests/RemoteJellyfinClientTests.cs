using System.Net;
using System.Text;
using JellyfinToJellyfin.Services.Remote;

namespace JellyfinToJellyfin.Tests;

/// <summary>
/// Covers the user-login authentication of <see cref="RemoteJellyfinClient"/>: session token caching,
/// refresh after a 401, library discovery through /UserViews and failure reporting. No real network is used.
/// </summary>
public sealed class RemoteJellyfinClientTests
{
    [Fact]
    public async Task LogsInOnceAndReusesTheSessionToken()
    {
        using var handler = new FakeHandler()
            .EnqueueLogin("token-1")
            .EnqueueJson("""{"ServerName":"Lowyfin","Version":"10.11.6"}""")
            .EnqueueJson("""{"ServerName":"Lowyfin","Version":"10.11.6"}""");

        using var client = new RemoteJellyfinClient("http://login-once.test", "mirror", "s3cret", handler);
        await client.GetServerInfoAsync(CancellationToken.None);
        await client.GetServerInfoAsync(CancellationToken.None);

        Assert.True(client.HasCredentials);
        Assert.Equal(3, handler.Requests.Count);

        var login = handler.Requests[0];
        Assert.Equal("POST", login.Method);
        Assert.Equal("/Users/AuthenticateByName", login.Path);
        Assert.Null(login.Token);
        Assert.Contains("\"Username\":\"mirror\"", login.Body, StringComparison.Ordinal);
        Assert.Contains("\"Pw\":\"s3cret\"", login.Body, StringComparison.Ordinal);

        Assert.Equal("token-1", handler.Requests[1].Token);
        Assert.Equal("token-1", handler.Requests[2].Token);
        Assert.Equal("token-1", client.AccessToken);
    }

    [Fact]
    public async Task CredentialsNeverTravelInRequestUrls()
    {
        using var handler = new FakeHandler()
            .EnqueueLogin("token-1")
            .EnqueueJson("""{"ServerName":"Lowyfin"}""");

        using var client = new RemoteJellyfinClient("http://no-secrets-in-url.test", "mirror", "s3cret", handler);
        await client.GetServerInfoAsync(CancellationToken.None);

        foreach (var request in handler.Requests)
        {
            Assert.DoesNotContain("s3cret", request.Path, StringComparison.Ordinal);
            Assert.DoesNotContain("mirror", request.Path, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task LogsInAgainAndRetriesAfterUnauthorized()
    {
        using var handler = new FakeHandler()
            .EnqueueLogin("stale-token")
            .EnqueueStatus(HttpStatusCode.Unauthorized)
            .EnqueueLogin("fresh-token")
            .EnqueueJson("""{"ServerName":"Lowyfin"}""");

        using var client = new RemoteJellyfinClient("http://relogin-after-401.test", "mirror", "s3cret", handler);
        var info = await client.GetServerInfoAsync(CancellationToken.None);

        Assert.Equal("Lowyfin", info?.ServerName);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal("stale-token", handler.Requests[1].Token);
        Assert.Equal("fresh-token", handler.Requests[3].Token);
        Assert.Equal("fresh-token", client.AccessToken);
    }

    [Fact]
    public async Task ListsLibrariesThroughUserViewsAndSkipsSpecialViews()
    {
        using var handler = new FakeHandler()
            .EnqueueLogin("token-uv")
            .EnqueueJson(
                """
                {"Items":[
                  {"Id":"movies1","Name":"Pellicules","Type":"CollectionFolder","CollectionType":"movies"},
                  {"Id":"playlists","Name":"Playlists","Type":"UserView"},
                  {"Id":"music1","Name":"Musica","Type":"CollectionFolder","CollectionType":"music"}
                ],"TotalRecordCount":3}
                """);

        using var client = new RemoteJellyfinClient("http://user-views.test", "mirror", "s3cret", handler);
        var libraries = await client.GetLibrariesAsync(CancellationToken.None);

        Assert.Equal(2, libraries.Count);
        Assert.Equal("Pellicules", libraries[0].Name);
        Assert.Equal("movies1", libraries[0].ItemId);
        Assert.Equal("movies", libraries[0].CollectionType);
        Assert.Equal("Musica", libraries[1].Name);
        Assert.StartsWith("/UserViews", handler.Requests[1].Path, StringComparison.Ordinal);
        Assert.DoesNotContain(handler.Requests, r => r.Path.StartsWith("/Library/VirtualFolders", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoginFailureReportsTheStatusAndTheUser()
    {
        using var handler = new FakeHandler()
            .EnqueueStatus(HttpStatusCode.Unauthorized);

        using var client = new RemoteJellyfinClient("http://login-fails.test", "mirror", "wrong", handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetServerInfoAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Contains("mirror", error.Message, StringComparison.Ordinal);
        Assert.Contains("HTTP 401", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthorizationHeaderIdentifiesThePluginWithoutSecrets()
    {
        var header = RemoteJellyfinClient.BuildAuthorizationHeader();

        Assert.StartsWith("MediaBrowser ", header, StringComparison.Ordinal);
        Assert.Contains("Client=\"Jellyfin to Jellyfin\"", header, StringComparison.Ordinal);
        Assert.Contains("DeviceId=\"j2j-", header, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientWithoutCredentialsRefusesToAuthenticate()
    {
        using var handler = new FakeHandler();
        using var client = new RemoteJellyfinClient("http://no-credentials.test", string.Empty, string.Empty, handler);

        Assert.False(client.HasCredentials);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAccessTokenAsync(CancellationToken.None));
        Assert.Contains("credentials", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    private sealed record RequestRecord(string Method, string Path, string? Token, string? Body);

    /// <summary>Message handler that replays scripted responses and records what the client sent.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
        private readonly List<RequestRecord> _requests = new();

        public IReadOnlyList<RequestRecord> Requests => _requests;

        public FakeHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _responses.Enqueue(response);
            return this;
        }

        public FakeHandler EnqueueJson(string json)
            => Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });

        public FakeHandler EnqueueStatus(HttpStatusCode status)
            => Enqueue(_ => new HttpResponseMessage(status));

        public FakeHandler EnqueueLogin(string accessToken)
            => EnqueueJson($"{{\"AccessToken\":\"{accessToken}\"}}");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = request.Headers.TryGetValues("X-Emby-Token", out var values) ? values.FirstOrDefault() : null;
            var body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            _requests.Add(new RequestRecord(request.Method.Method, request.RequestUri!.PathAndQuery, token, body));

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
            }

            return Task.FromResult(_responses.Dequeue()(request));
        }
    }
}
