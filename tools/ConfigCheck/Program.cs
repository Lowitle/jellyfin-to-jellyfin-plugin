using System.Text.Json;
using JellyfinToJellyfin.Configuration;
using JellyfinToJellyfin.Services;
using JellyfinToJellyfin.Services.Remote;
using Microsoft.Extensions.Logging.Abstractions;

// Mirrors AdminController.ConfigJsonOptions (PascalCase output, case-insensitive input).
var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = null
};

var config = new PluginConfiguration
{
    DefaultBasePath = "/media/j2j",
    ServerUrlOverride = "http://192.168.87.170:8096",
    SyncOnStartup = true,
    ScheduleIntervalHours = 6,
    Instances = new List<SourceInstance>
    {
        new()
        {
            Id = "inst1",
            Name = "Home",
            BaseUrl = "http://remote:8096",
            ApiKey = "SECRET",
            Enabled = true,
            DownloadImages = false,
            LastSyncUtc = new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc),
            LastSyncStatus = "OK",
            Libraries = new List<LibraryMapping>
            {
                new()
                {
                    RemoteLibraryId = "abc",
                    RemoteLibraryName = "Movies",
                    RemoteCollectionType = "movies",
                    FolderName = "movies",
                    TargetLibraryName = "Movies",
                    TargetCollectionType = "movies"
                }
            }
        }
    }
};

var serverJson = JsonSerializer.Serialize(config, options);
Console.WriteLine("GET /config body:");
Console.WriteLine(serverJson);

using (var doc = JsonDocument.Parse(serverJson))
{
    var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
    Console.WriteLine();
    Console.WriteLine("top-level keys seen by page: " + string.Join(", ", keys));
    Console.WriteLine("instance keys seen by page: " + string.Join(", ", doc.RootElement.GetProperty("Instances")[0].EnumerateObject().Select(p => p.Name)));
}

var reparsed = JsonSerializer.Deserialize<PluginConfiguration>(serverJson, options)!;
Console.WriteLine();
Console.WriteLine($"PascalCase round-trip: instances={reparsed.Instances.Count} id={reparsed.Instances[0].Id} key={reparsed.Instances[0].ApiKey} baseUrl={reparsed.Instances[0].BaseUrl} lastSync={reparsed.Instances[0].LastSyncUtc:o} lib={reparsed.Instances[0].Libraries[0].RemoteLibraryName} images={reparsed.Instances[0].DownloadImages} schedule={reparsed.ScheduleIntervalHours} startup={reparsed.SyncOnStartup} override={reparsed.ServerUrlOverride} basePath={reparsed.DefaultBasePath}");

var camelJson = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web));
var camelReparsed = JsonSerializer.Deserialize<PluginConfiguration>(camelJson, options)!;
Console.WriteLine($"camelCase accepted too: instances={camelReparsed.Instances.Count} id={camelReparsed.Instances[0].Id} lib={camelReparsed.Instances[0].Libraries[0].TargetLibraryName}");

var reserialized = JsonSerializer.Serialize(camelReparsed, options);
Console.WriteLine($"re-serialized stays PascalCase: {reserialized.Contains("\"Instances\"", StringComparison.Ordinal) && reserialized.Contains("\"RemoteLibraryId\"", StringComparison.Ordinal)}");

var defaults = new PluginConfiguration();
Console.WriteLine($"defaults: basePath='{defaults.DefaultBasePath}' schedule={defaults.ScheduleIntervalHours} startup={defaults.SyncOnStartup} instances={defaults.Instances.Count}");

Console.WriteLine();
Console.WriteLine("--- NFO smoke test ---");
var movie = new RemoteItem
{
    Id = "m1",
    Name = "Blade Runner 2049",
    Type = "Movie",
    ProductionYear = 2017,
    Overview = "A young blade runner & <replicant> hunter.",
    Genres = new[] { "Science Fiction", "Drama" },
    PremiereDate = "2017-10-06T00:00:00.0000000Z",
    OfficialRating = "R",
    RunTimeTicks = TimeSpan.FromMinutes(164).Ticks,
    CommunityRating = 8.0,
    CriticRating = 81,
    ProviderIds = new Dictionary<string, string> { ["Imdb"] = "tt1856101", ["Tmdb"] = "335984" },
    ImageTags = new Dictionary<string, string> { ["Primary"] = "tag1" },
    BackdropImageTags = new[] { "bd1" }
};
Console.WriteLine(NfoBuilder.Movie(movie));
Console.WriteLine();

var episode = new RemoteItem
{
    Id = "e1",
    Name = "Chapter One: The Vanishing of Will Byers",
    Type = "Episode",
    SeriesName = "Stranger Things",
    ParentIndexNumber = 1,
    IndexNumber = 1,
    Overview = "Kids play D&D.",
    ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "5754134" }
};
Console.WriteLine(NfoBuilder.Episode(episode, 1, 1));
Console.WriteLine();
Console.WriteLine(NfoBuilder.TvShow(new RemoteItem { Id = "s1", Name = "Stranger Things", Type = "Series", ProductionYear = 2016 }));
Console.WriteLine();
Console.WriteLine(NfoBuilder.Season(1, "Season 1"));

Console.WriteLine();
Console.WriteLine("--- Remote client smoke test ---");
Console.WriteLine($"movie url : {RemoteJellyfinClient.BuildStreamUrl("http://192.168.87.170:8096/", "abc123", "video")}");
Console.WriteLine($"audio url : {RemoteJellyfinClient.BuildStreamUrl("http://192.168.87.170:8096", "abc123", "audio")}");
Console.WriteLine($"default   : {RemoteJellyfinClient.BuildStreamUrl("http://host:8096", "abc123", null)}");
Console.WriteLine($"normalize : [{RemoteJellyfinClient.NormalizeBaseUrl("  http://host:8096///  ")}]");
Console.WriteLine($"isAudioKind(video)={RemoteJellyfinClient.IsAudioKind("video")} isAudioKind(AUDIO)={RemoteJellyfinClient.IsAudioKind("AUDIO")} isAudioKind(null)={RemoteJellyfinClient.IsAudioKind(null)}");

// The remote returns camelCase JSON; the client must map it onto the DTOs used to build the mirror.
const string itemsJson = """
{
  "items": [
    {
      "id": "1f0e5b4a",
      "name": "Blade Runner 2049",
      "type": "Movie",
      "mediaType": "Video",
      "parentId": "lib1",
      "productionYear": 2017,
      "premiereDate": "2017-10-06T00:00:00.0000000Z",
      "runTimeTicks": 9840000000,
      "officialRating": "R",
      "communityRating": 8,
      "criticRating": 81,
      "container": "mkv",
      "overview": "A young blade runner.",
      "genres": [ "Science Fiction" ],
      "providerIds": { "Imdb": "tt1856101", "Tmdb": "335984" },
      "imageTags": { "Primary": "abc", "Logo": "def" },
      "backdropImageTags": [ "bd1" ]
    },
    {
      "id": "ep1",
      "name": "Chapter One",
      "type": "Episode",
      "parentId": "season1",
      "seriesId": "series1",
      "seriesName": "Stranger Things",
      "seasonId": "season1",
      "seasonName": "Season 1",
      "indexNumber": 1,
      "parentIndexNumber": 1,
      "indexNumberEnd": 2
    }
  ],
  "totalRecordCount": 2,
  "startIndex": 0
}
""";

var parsed = JsonSerializer.Deserialize<RemoteItemsResult>(itemsJson, options)!;
Console.WriteLine($"deserialized items : {parsed.Items?.Count} total={parsed.TotalRecordCount}");
var movieDto = parsed.Items![0];
Console.WriteLine($"movie  : {movieDto.Name} | {movieDto.Type} | {movieDto.ProductionYear} | {movieDto.PremiereDate} | ticks={movieDto.RunTimeTicks} | rating={movieDto.CommunityRating}/{movieDto.CriticRating} | genres={string.Join('|', movieDto.Genres ?? Array.Empty<string>())} | imdb={movieDto.ProviderIds?["Imdb"]} | primary={movieDto.HasPrimaryImage} backdrop={movieDto.HasBackdrop} | parent={movieDto.ParentId}");
var episodeDto = parsed.Items[1];
Console.WriteLine($"episode: {episodeDto.Name} | S{episodeDto.ParentIndexNumber:00}E{episodeDto.IndexNumber:00}-E{episodeDto.IndexNumberEnd:00} | series={episodeDto.SeriesName} ({episodeDto.SeriesId}) | season={episodeDto.SeasonId} | parent={episodeDto.ParentId}");

Console.WriteLine();
Console.WriteLine("--- PathSanitizer smoke test ---");

foreach (var name in new[] { "Mission: Impossible - Fallout", "  Spider-Man/No Way Home  ", "Bad\\Name*With?Chars\"<>|", "", "   ", new string('x', 200) })
{
    var safe = PathSanitizer.Sanitize(name);
    Console.WriteLine($"[{name}] -> [{safe}] (len={safe.Length})");
}

if (args.Contains("--live", StringComparer.OrdinalIgnoreCase))
{
    Environment.ExitCode = await RunLiveAsync().ConfigureAwait(false);
}

// Talks to a real Jellyfin over HTTP. Credentials come from the environment so they stay out of the repo.
async Task<int> RunLiveAsync()
{
    var baseUrl = Environment.GetEnvironmentVariable("J2J_REMOTE_URL");
    var apiKey = Environment.GetEnvironmentVariable("J2J_REMOTE_KEY");
    if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
    {
        Console.WriteLine("live: set J2J_REMOTE_URL and J2J_REMOTE_KEY, then run with --live.");
        return 2;
    }

    using var client = new RemoteJellyfinClient(baseUrl!, apiKey!);
    Console.WriteLine();
    Console.WriteLine($"--- Live remote check ({client.BaseUrl}) ---");

    var info = await client.GetServerInfoAsync(CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine($"server   : {info?.ServerName} {info?.Version} id={info?.Id}");

    var libraries = await client.GetLibrariesAsync(CancellationToken.None).ConfigureAwait(false);
    foreach (var lib in libraries)
    {
        Console.WriteLine($"library  : {lib.Name} type={lib.GetEffectiveCollectionType()} itemId={lib.ItemId ?? "<missing>"}");
    }

    // Optional filters: J2J_LIBRARY_INDEX (0-based) or J2J_LIBRARY_INDEX unset to scan every library.
    var candidates = libraries.Where(l => !string.IsNullOrEmpty(l.ItemId)).ToList();
    if (int.TryParse(Environment.GetEnvironmentVariable("J2J_LIBRARY_INDEX"), out var onlyIndex))
    {
        candidates = candidates.Skip(onlyIndex).Take(1).ToList();
    }

    if (candidates.Count == 0)
    {
        Console.WriteLine("live: no library exposed an item id, cannot query items.");
        return 3;
    }

    RemoteItem? streamSample = null;
    var mirrorDir = Environment.GetEnvironmentVariable("J2J_MIRROR_DIR");
    foreach (var library in candidates)
    {
        Console.WriteLine();
        Console.WriteLine($"--- /Items for '{library.Name}' ({library.ItemId}) ---");
        var result = await client.GetItemsAsync(library.ItemId!, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"items    : {result.Items?.Count ?? 0} (TotalRecordCount={result.TotalRecordCount})");

        foreach (var group in (result.Items ?? new List<RemoteItem>()).GroupBy(i => i.Type ?? "<null>").OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {group.Key,-12} {group.Count()}");
        }

        if (!string.IsNullOrWhiteSpace(mirrorDir))
        {
            await MirrorAsync(client, result.Items ?? new List<RemoteItem>(), mirrorDir!).ConfigureAwait(false);
        }

        var dumpDir = Environment.GetEnvironmentVariable("J2J_DUMP_DIR");
        if (!string.IsNullOrWhiteSpace(dumpDir))
        {
            Directory.CreateDirectory(dumpDir!);
            var dumpPath = Path.Combine(dumpDir!, PathSanitizer.Sanitize(library.Name, "library") + ".json");
            await File.WriteAllTextAsync(
                dumpPath,
                JsonSerializer.Serialize(result.Items, new JsonSerializerOptions { WriteIndented = true }),
                CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"dump     : {dumpPath}");
        }

        var sampleMovie = result.Items?.FirstOrDefault(i => i.Type is "Movie" or "Episode" or "Audio");
        if (sampleMovie is not null)
        {
            Console.WriteLine($"sample   : {sampleMovie.Type} '{sampleMovie.Name}' parent={sampleMovie.ParentId ?? "<null>"} "
                + $"overview={(sampleMovie.Overview is null ? "MISSING" : "ok")} genres={sampleMovie.Genres?.Length ?? 0} "
                + $"providers={sampleMovie.ProviderIds?.Count ?? 0} sort={sampleMovie.SortName ?? "<null>"} "
                + $"poster={sampleMovie.HasPrimaryImage} backdrop={sampleMovie.HasBackdrop} ticks={sampleMovie.RunTimeTicks}");
            streamSample ??= sampleMovie;
        }

        var series = result.Items?.FirstOrDefault(i => i.Type == "Series");
        if (series is not null)
        {
            Console.WriteLine($"series   : '{series.Name}' overview={(series.Overview is null ? "MISSING" : "ok")} "
                + $"genres={series.Genres?.Length ?? 0} providers={series.ProviderIds?.Count ?? 0} poster={series.HasPrimaryImage} backdrop={series.HasBackdrop}");
        }
    }

    if (streamSample?.Id is { Length: > 0 } itemId)
    {
        var kind = string.Equals(streamSample.MediaType, "Audio", StringComparison.OrdinalIgnoreCase) ? "audio" : "video";
        var url = RemoteJellyfinClient.BuildStreamUrl(client.BaseUrl, itemId, kind);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-Emby-Token", client.ApiKey);
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-1023");
        using var response = await RemoteJellyfinClient.ProxyClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        var bytes = response.IsSuccessStatusCode
            ? (await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Length
            : 0;
        Console.WriteLine($"stream   : {(int)response.StatusCode} {response.StatusCode} type={response.Content.Headers.ContentType} bytes={bytes} ranges={response.Headers.AcceptRanges}");
    }

    return 0;
}

// Writes a real .strm/.nfo mirror into J2J_MIRROR_DIR so the on-disk layout can be inspected.
async Task MirrorAsync(RemoteJellyfinClient remoteClient, List<RemoteItem> items, string targetDir)
{
    if (Directory.Exists(targetDir))
    {
        Directory.Delete(targetDir, recursive: true);
    }

    var generator = new MirrorGenerator(remoteClient, "live", "http://localhost:8096", downloadImages: false, removeStaleFiles: false, NullLogger.Instance);
    await generator.GenerateAsync(items, targetDir, CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine($"mirror   : {generator.StrmWritten} strm, {generator.NfoWritten} nfo, {generator.ImagesWritten} images, {generator.DuplicatesSkipped} duplicate path(s), {generator.Warnings.Count} warnings -> {targetDir}");
    foreach (var warning in generator.Warnings.Take(5))
    {
        Console.WriteLine($"  warn: {warning}");
    }

    foreach (var entry in Directory.EnumerateFileSystemEntries(targetDir, "*", SearchOption.AllDirectories)
                 .OrderBy(e => e, StringComparer.Ordinal)
                 .Take(30))
    {
        Console.WriteLine("  " + Path.GetRelativePath(targetDir, entry));
    }

    var strm = Directory.EnumerateFiles(targetDir, "*.strm", SearchOption.AllDirectories).FirstOrDefault();
    if (strm is not null)
    {
        Console.WriteLine($"  sample strm: {Path.GetRelativePath(targetDir, strm)} => {await File.ReadAllTextAsync(strm).ConfigureAwait(false)}");
    }
}

