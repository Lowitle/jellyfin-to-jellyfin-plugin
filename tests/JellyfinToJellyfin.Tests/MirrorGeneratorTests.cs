using JellyfinToJellyfin.Services;
using JellyfinToJellyfin.Services.Remote;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellyfinToJellyfin.Tests;

public sealed class MirrorGeneratorTests : IDisposable
{
    private readonly string _root;
    private readonly RemoteJellyfinClient _client;

    public MirrorGeneratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "j2j-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        // No network access happens while images are disabled, so an unroutable host is fine here.
        _client = new RemoteJellyfinClient("http://remote.invalid:8096", "test-key");
    }

    public void Dispose()
    {
        _client.Dispose();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // Temp folder cleanup is best effort.
        }
    }

    [Fact]
    public async Task Movie_WritesStrmAndNfoInYearFolder()
    {
        var movie = new RemoteItem
        {
            Id = "movie1",
            Name = "Blade Runner 2049",
            Type = "Movie",
            ProductionYear = 2017,
            Overview = "A young blade runner unearths a long-buried secret.",
            PremiereDate = "2017-10-06T00:00:00.0000000Z",
            RunTimeTicks = 164L * 600000000
        };

        var generator = await Mirror(movie);

        var movieDir = Path.Combine(_root, "Blade Runner 2049 (2017)");
        Assert.True(Directory.Exists(movieDir));
        Assert.Equal(
            "http://local:8096/JellyfinToJellyfin/stream/inst1/movie1?k=video",
            await File.ReadAllTextAsync(Path.Combine(movieDir, "Blade Runner 2049 (2017).strm")));
        Assert.True(File.Exists(Path.Combine(movieDir, "Blade Runner 2049 (2017).nfo")));
        Assert.Equal(1, generator.StrmWritten);
        Assert.Equal(1, generator.NfoWritten);
        Assert.Empty(generator.Warnings);
    }

    [Fact]
    public async Task Series_WritesShowSeasonAndEpisodeLayout()
    {
        var series = new RemoteItem { Id = "series1", Name = "Stranger Things", Type = "Series", ProductionYear = 2016 };
        var season = new RemoteItem { Id = "season1", ParentId = "series1", Name = "Season 1", Type = "Season", IndexNumber = 1 };
        var episode = new RemoteItem
        {
            Id = "episode1",
            ParentId = "season1",
            Name = "Chapter One",
            Type = "Episode",
            SeriesName = "Stranger Things",
            ParentIndexNumber = 1,
            IndexNumber = 1
        };

        var generator = await Mirror(series, season, episode);

        var seriesDir = Path.Combine(_root, "Stranger Things (2016)");
        var seasonDir = Path.Combine(seriesDir, "Season 01");
        Assert.True(File.Exists(Path.Combine(seriesDir, "tvshow.nfo")));
        Assert.True(File.Exists(Path.Combine(seasonDir, "season.nfo")));
        Assert.True(File.Exists(Path.Combine(seasonDir, "Stranger Things S01E01 - Chapter One.strm")));
        Assert.Equal(1, generator.StrmWritten);
        Assert.Equal(3, generator.NfoWritten);
    }

    [Fact]
    public async Task Music_WritesArtistAlbumYearAndTrackNumbering()
    {
        var artist = new RemoteItem { Id = "artist1", Name = "ABBA", Type = "MusicArtist" };
        var album = new RemoteItem
        {
            Id = "album1",
            ParentId = "artist1",
            Name = "ABBA",
            Type = "MusicAlbum",
            ProductionYear = 1975,
            AlbumArtists = new[] { new RemoteItem.NameIdPair { Id = "artist1", Name = "ABBA" } }
        };
        var track = new RemoteItem
        {
            Id = "track1",
            ParentId = "album1",
            Name = "Mamma Mia",
            Type = "Audio",
            IndexNumber = 1,
            AlbumArtists = new[] { new RemoteItem.NameIdPair { Id = "artist1", Name = "ABBA" } }
        };

        var generator = await Mirror(artist, album, track);

        var albumDir = Path.Combine(_root, "ABBA", "ABBA (1975)");
        Assert.True(Directory.Exists(albumDir));
        var albumNfo = await File.ReadAllTextAsync(Path.Combine(albumDir, "album.nfo"));
        Assert.Contains("<artist>ABBA</artist>", albumNfo, StringComparison.Ordinal);
        Assert.Equal(
            "http://local:8096/JellyfinToJellyfin/stream/inst1/track1?k=audio",
            await File.ReadAllTextAsync(Path.Combine(albumDir, "01 - Mamma Mia.strm")));
        Assert.Equal(1, generator.StrmWritten);
        Assert.Equal(1, generator.NfoWritten);
    }

    [Fact]
    public async Task DuplicateFolderNames_GetIdSuffixInsteadOfBeingOverwritten()
    {
        var first = new RemoteItem { Id = "aaaaaaaa1111", Name = "Documental", Type = "Movie", ProductionYear = 2019 };
        var second = new RemoteItem { Id = "bbbbbbbb2222", Name = "Documental", Type = "Movie", ProductionYear = 2019 };

        var generator = await Mirror(first, second);

        Assert.True(Directory.Exists(Path.Combine(_root, "Documental (2019)")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Documental (2019) [bbbbbbbb]")));
        Assert.Equal(2, generator.StrmWritten);
        Assert.Equal(2, generator.NfoWritten);
        Assert.Equal(0, generator.DuplicatesSkipped);
        Assert.Contains(generator.Warnings, w => w.Contains("Duplicate folder name 'Documental (2019)'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateFiles_KeepTheFirstItemAndAreCounted()
    {
        var artist = new RemoteItem { Id = "artist1", Name = "ABBA", Type = "MusicArtist" };
        var album = new RemoteItem { Id = "album1", ParentId = "artist1", Name = "ABBA", Type = "MusicAlbum", ProductionYear = 1975 };
        var first = new RemoteItem { Id = "track1", ParentId = "album1", Name = "Mamma Mia", Type = "Audio", IndexNumber = 1 };
        var second = new RemoteItem { Id = "track2", ParentId = "album1", Name = "Mamma Mia", Type = "Audio", IndexNumber = 1 };

        var generator = await Mirror(artist, album, first, second);

        var trackFile = Path.Combine(_root, "ABBA", "ABBA (1975)", "01 - Mamma Mia.strm");
        Assert.Equal(
            "http://local:8096/JellyfinToJellyfin/stream/inst1/track1?k=audio",
            await File.ReadAllTextAsync(trackFile));
        Assert.Equal(1, generator.StrmWritten);
        Assert.Equal(1, generator.DuplicatesSkipped);
        Assert.Contains(generator.Warnings, w => w.Contains("Duplicate mirror path", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecondRun_DoesNotRewriteUnchangedFiles()
    {
        var movie = new RemoteItem { Id = "movie1", Name = "Heat", Type = "Movie", ProductionYear = 1995 };

        var first = await Mirror(movie);
        var second = await Mirror(movie);

        Assert.Equal(1, first.StrmWritten);
        Assert.Equal(1, first.NfoWritten);
        Assert.Equal(0, second.StrmWritten);
        Assert.Equal(0, second.NfoWritten);
    }

    [Fact]
    public async Task UnsupportedItemTypes_WriteNothing()
    {
        var boxSet = new RemoteItem { Id = "box1", Name = "Collection", Type = "BoxSet" };
        var playlist = new RemoteItem { Id = "playlist1", Name = "Road trip", Type = "Playlist" };
        var book = new RemoteItem { Id = "book1", Name = "A book", Type = "Book" };

        var generator = await Mirror(boxSet, playlist, book);

        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
        Assert.Equal(0, generator.StrmWritten);
        Assert.Equal(0, generator.NfoWritten);
        Assert.Empty(generator.Warnings);
    }

    private async Task<MirrorGenerator> Mirror(params RemoteItem[] items)
    {
        var generator = new MirrorGenerator(_client, "inst1", "http://local:8096/", downloadImages: false, NullLogger.Instance);
        await generator.GenerateAsync(items, _root, CancellationToken.None);
        return generator;
    }
}
