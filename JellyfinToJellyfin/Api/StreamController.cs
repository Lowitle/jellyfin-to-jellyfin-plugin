using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JellyfinToJellyfin.Configuration;
using JellyfinToJellyfin.Services.Remote;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JellyfinToJellyfin.Api;

/// <summary>
/// Streams original files from the configured remote instances so that generated .strm files play through this server.
/// The remote API key never leaves the server: the .strm files only contain this endpoint.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("JellyfinToJellyfin")]
public sealed class StreamController : ControllerBase
{
    private static readonly string[] ForwardedResponseHeaders = { "Accept-Ranges", "Cache-Control", "ETag", "Last-Modified", "Content-Disposition" };

    // Accept-Encoding is intentionally not forwarded: the proxy does not decompress upstream bodies, so
    // asking the remote for gzip/br would hand compressed bytes to the player without a Content-Encoding header.
    private static readonly string[] ForwardedRequestHeaders = { "Range", "If-Range", "If-None-Match", "If-Modified-Since", "User-Agent" };

    private readonly ILogger<StreamController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamController"/> class.
    /// </summary>
    public StreamController(ILogger<StreamController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Proxies the original file of a remote item.
    /// </summary>
    /// <param name="instanceId">Configured source instance id.</param>
    /// <param name="itemId">Remote item id.</param>
    /// <param name="k">Stream kind: <c>video</c> (default) or <c>audio</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The streamed content.</returns>
    [HttpGet("stream/{instanceId}/{itemId}")]
    public async Task<IActionResult> GetStream(string instanceId, string itemId, [FromQuery] string? k, CancellationToken cancellationToken)
    {
        var instance = FindInstance(instanceId);
        if (instance is null || string.IsNullOrWhiteSpace(instance.BaseUrl))
        {
            return NotFound($"Unknown or unconfigured instance '{instanceId}'.");
        }

        var targetUrl = RemoteJellyfinClient.BuildStreamUrl(instance.BaseUrl, itemId, k);
        using var request = new HttpRequestMessage(HttpMethod.Get, targetUrl);
        request.Headers.TryAddWithoutValidation("X-Emby-Token", instance.ApiKey);

        foreach (var header in ForwardedRequestHeaders)
        {
            var values = Request.Headers[header];
            if (values.Count > 0)
            {
                request.Headers.TryAddWithoutValidation(header, values.ToArray());
            }
        }

        HttpResponseMessage upstream;
        try
        {
            upstream = await RemoteJellyfinClient.ProxyClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Upstream stream request failed for item {ItemId} of instance {Instance}", itemId, instanceId);
            return StatusCode(502, "Remote stream unavailable.");
        }

        using (upstream)
        {
            if (!upstream.IsSuccessStatusCode)
            {
                var status = (int)upstream.StatusCode;
                _logger.LogDebug("Upstream returned {Status} for item {ItemId} of instance {Instance}", status, itemId, instanceId);
                return StatusCode(status);
            }

            Response.StatusCode = (int)upstream.StatusCode;
            Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

            if (upstream.Content.Headers.ContentLength is long length)
            {
                Response.ContentLength = length;
            }

            foreach (var header in ForwardedResponseHeaders)
            {
                if (upstream.Headers.TryGetValues(header, out var values) || upstream.Content.Headers.TryGetValues(header, out values))
                {
                    Response.Headers[header] = values.ToArray();
                }
            }

            if (upstream.Content.Headers.TryGetValues("Content-Range", out var contentRange))
            {
                Response.Headers["Content-Range"] = contentRange.ToArray();
            }

            try
            {
                await using var source = await upstream.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await source.CopyToAsync(Response.Body, 128 * 1024, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Client went away mid-stream.
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                _logger.LogDebug(ex, "Stream aborted for item {ItemId} of instance {Instance}", itemId, instanceId);
            }
        }

        return new EmptyResult();
    }

    private static SourceInstance? FindInstance(string instanceId)
        => Plugin.Instance?.Configuration.Instances
            .FirstOrDefault(i => string.Equals(i.Id, instanceId, StringComparison.OrdinalIgnoreCase));
}
