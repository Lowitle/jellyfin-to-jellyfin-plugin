using System.Globalization;
using System.Xml.Linq;
using JellyfinToJellyfin.Services;
using JellyfinToJellyfin.Services.Remote;

namespace JellyfinToJellyfin.Tests;

public class NfoBuilderTests
{
    private const long TicksPerMinute = 600000000;

    [Fact]
    public void Movie_WritesCoreMetadata()
    {
        var item = new RemoteItem
        {
            Name = "Blade Runner 2049",
            OriginalTitle = "Blade Runner 2049",
            SortName = "Blade Runner 2049",
            Overview = "A young blade runner unearths a long-buried secret.",
            ProductionYear = 2017,
            OfficialRating = "R",
            RunTimeTicks = 164 * TicksPerMinute,
            CommunityRating = 8.1,
            CriticRating = 61,
            Genres = new[] { "Science Fiction", "Drama" }
        };

        var nfo = Load(NfoBuilder.Movie(item));

        Assert.Equal("movie", nfo.Root!.Name.LocalName);
        Assert.Equal("Blade Runner 2049", Text(nfo, "title"));
        Assert.Equal("2017", Text(nfo, "year"));
        Assert.Equal("R", Text(nfo, "mpaa"));
        Assert.Equal("164", Text(nfo, "runtime"));
        Assert.Equal("8.1", Text(nfo, "rating"));
        Assert.Equal("61.0", Text(nfo, "criticrating"));
        Assert.Equal(new[] { "Science Fiction", "Drama" }, nfo.Root!.Elements("genre").Select(g => g.Value).ToArray());
    }

    [Fact]
    public void Movie_DeduplicatesGenresCaseInsensitively()
    {
        var item = new RemoteItem { Name = "Heat", Genres = new[] { "Drama", "drama", "Crime", " " } };

        var nfo = Load(NfoBuilder.Movie(item));

        Assert.Equal(new[] { "Drama", "Crime" }, nfo.Root!.Elements("genre").Select(g => g.Value).ToArray());
    }

    [Fact]
    public void Movie_NormalizesPremieredToLocalDate()
    {
        const string remoteDate = "2017-10-06T00:00:00.0000000Z";
        var expected = DateTimeOffset.Parse(remoteDate, CultureInfo.InvariantCulture)
            .ToLocalTime()
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var item = new RemoteItem { Name = "Blade Runner 2049", PremiereDate = remoteDate };

        var nfo = Load(NfoBuilder.Movie(item));

        Assert.Equal(expected, Text(nfo, "premiered"));
        Assert.DoesNotContain("T", Text(nfo, "premiered")!);
    }

    [Fact]
    public void Movie_KeepsUnparseableDateVerbatim()
    {
        var item = new RemoteItem { Name = "Unknown Release", PremiereDate = " sometime in 1999 " };

        var nfo = Load(NfoBuilder.Movie(item));

        Assert.Equal("sometime in 1999", Text(nfo, "premiered"));
    }

    [Fact]
    public void Movie_MarksFirstKnownProviderAsDefault()
    {
        var item = new RemoteItem
        {
            Name = "Blade Runner 2049",
            ProviderIds = new Dictionary<string, string>
            {
                ["Tmdb"] = "335984",
                ["Imdb"] = "tt1856101",
                ["Custom"] = "abc"
            }
        };

        var nfo = Load(NfoBuilder.Movie(item));

        var uniqueIds = nfo.Root!.Elements("uniqueid").ToList();
        Assert.Equal(3, uniqueIds.Count);
        Assert.Equal("Imdb", uniqueIds[0].Attribute("type")!.Value);
        Assert.Equal("true", uniqueIds[0].Attribute("default")!.Value);
        Assert.Equal("tt1856101", uniqueIds[0].Value);
        Assert.Equal("Tmdb", uniqueIds[1].Attribute("type")!.Value);
        Assert.Null(uniqueIds[1].Attribute("default"));
        Assert.Equal("Custom", uniqueIds[2].Attribute("type")!.Value);
        Assert.Equal("abc", uniqueIds[2].Value);
    }

    [Fact]
    public void Movie_OmitsEmptyValues()
    {
        var item = new RemoteItem { Name = "Nameless", Overview = "   " };

        var nfo = Load(NfoBuilder.Movie(item));

        Assert.Null(nfo.Root!.Element("plot"));
        Assert.Null(nfo.Root!.Element("year"));
        Assert.Null(nfo.Root!.Element("runtime"));
        Assert.Null(nfo.Root!.Element("premiered"));
    }

    [Fact]
    public void Episode_WritesSeasonAndEpisodeNumbers()
    {
        var item = new RemoteItem
        {
            Name = "Chapter One",
            Overview = "A first chapter.",
            ParentIndexNumber = 1,
            IndexNumber = 2,
            PremiereDate = "2016-07-15T00:00:00Z",
            RunTimeTicks = 47 * TicksPerMinute
        };

        var nfo = Load(NfoBuilder.Episode(item, 1, 2));

        Assert.Equal("episodedetails", nfo.Root!.Name.LocalName);
        Assert.Equal("Chapter One", Text(nfo, "title"));
        Assert.Equal("1", Text(nfo, "season"));
        Assert.Equal("2", Text(nfo, "episode"));
        Assert.Equal("47", Text(nfo, "runtime"));
        Assert.Matches("^\\d{4}-\\d{2}-\\d{2}$", Text(nfo, "aired")!);
    }

    [Fact]
    public void Episode_FallsBackToGeneratedTitle()
    {
        var nfo = Load(NfoBuilder.Episode(new RemoteItem(), 2, 3));

        Assert.Equal("Episode 3", Text(nfo, "title"));
        Assert.Equal("2", Text(nfo, "season"));
        Assert.Equal("3", Text(nfo, "episode"));
    }

    [Fact]
    public void Season_WritesNumberAndTitle()
    {
        var nfo = Load(NfoBuilder.Season(1, "Season 1"));

        Assert.Equal("season", nfo.Root!.Name.LocalName);
        Assert.Equal("1", Text(nfo, "seasonnumber"));
        Assert.Equal("Season 1", Text(nfo, "title"));
    }


    [Fact]
    public void Album_PrefersAlbumArtist()
    {
        var item = new RemoteItem
        {
            Name = "ABBA",
            ProductionYear = 1975,
            AlbumArtist = "ABBA",
            AlbumArtists = new[] { new RemoteItem.NameIdPair { Id = "artist1", Name = "ABBA (album)" } },
            Artists = new[] { "ABBA (track)" }
        };

        var nfo = Load(NfoBuilder.Album(item));

        Assert.Equal("album", nfo.Root!.Name.LocalName);
        Assert.Equal("ABBA", Text(nfo, "artist"));
        Assert.Equal("1975", Text(nfo, "year"));
    }

    [Fact]
    public void Album_FallsBackToFirstAlbumArtistThenFirstArtist()
    {
        var withAlbumArtists = new RemoteItem
        {
            Name = "Arrival",
            AlbumArtists = new[] { new RemoteItem.NameIdPair { Id = "a1", Name = "ABBA" } },
            Artists = new[] { "Anni-Frid" }
        };
        var withArtists = new RemoteItem { Name = "Arrival", Artists = new[] { "Anni-Frid", "Agnetha" } };

        Assert.Equal("ABBA", Text(Load(NfoBuilder.Album(withAlbumArtists)), "artist"));
        Assert.Equal("Anni-Frid", Text(Load(NfoBuilder.Album(withArtists)), "artist"));
    }

    [Fact]
    public void Album_OmitsArtistWhenNoArtistInformationExists()
    {
        var nfo = Load(NfoBuilder.Album(new RemoteItem { Name = "Untitled" }));

        Assert.Null(nfo.Root!.Element("artist"));
    }

    [Fact]
    public void MusicVideo_UsesPrimaryArtist()
    {
        var item = new RemoteItem
        {
            Name = "Mamma Mia",
            Album = "ABBA",
            AlbumArtists = new[] { new RemoteItem.NameIdPair { Id = "a1", Name = "ABBA" } }
        };

        var nfo = Load(NfoBuilder.MusicVideo(item));

        Assert.Equal("musicvideo", nfo.Root!.Name.LocalName);
        Assert.Equal("ABBA", Text(nfo, "artist"));
        Assert.Equal("ABBA", Text(nfo, "album"));
    }

    private static XDocument Load(string nfo) => XDocument.Parse(nfo);

    private static string? Text(XDocument nfo, string elementName) => nfo.Root!.Element(elementName)?.Value;
}

