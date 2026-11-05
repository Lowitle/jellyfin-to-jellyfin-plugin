using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using JellyfinToJellyfin.Services.Remote;

namespace JellyfinToJellyfin.Services;

/// <summary>
/// Builds Kodi/Jellyfin compatible .nfo documents from remote item metadata.
/// </summary>
public static class NfoBuilder
{
    private static readonly string[] KnownProviders =
    {
        "Imdb", "Tmdb", "Tvdb", "TvRage", "MusicBrainzAlbum", "MusicBrainzArtist", "MusicBrainzTrack"
    };

    public static string Movie(RemoteItem item)
    {
        var e = new XElement("movie");
        AddTitle(e, item);
        AddPlot(e, item);
        AddYear(e, item);
        AddDate(e, "premiered", item.PremiereDate);
        AddText(e, "mpaa", item.OfficialRating);
        AddRuntime(e, item);
        AddUniqueIds(e, item);
        AddRatings(e, item);
        AddGenres(e, item);
        return Serialize(e);
    }

    public static string TvShow(RemoteItem item)
    {
        var e = new XElement("tvshow");
        AddTitle(e, item);
        AddPlot(e, item);
        AddYear(e, item);
        AddDate(e, "premiered", item.PremiereDate);
        AddText(e, "mpaa", item.OfficialRating);
        AddUniqueIds(e, item);
        AddRatings(e, item);
        AddGenres(e, item);
        return Serialize(e);
    }

    public static string Season(int seasonNumber, string? title)
    {
        var e = new XElement("season", new XElement("seasonnumber", seasonNumber.ToString(CultureInfo.InvariantCulture)));
        AddText(e, "title", title);
        return Serialize(e);
    }

    public static string Episode(RemoteItem item, int season, int episode)
    {
        var e = new XElement("episodedetails");
        AddText(e, "title", string.IsNullOrWhiteSpace(item.Name) ? $"Episode {episode}" : item.Name);
        AddText(e, "season", season.ToString(CultureInfo.InvariantCulture));
        AddText(e, "episode", episode.ToString(CultureInfo.InvariantCulture));
        AddPlot(e, item);
        AddText(e, "aired", FormatDate(item.PremiereDate));
        AddRuntime(e, item);
        AddUniqueIds(e, item);
        AddRatings(e, item);
        return Serialize(e);
    }

    public static string MusicVideo(RemoteItem item)
    {
        var e = new XElement("musicvideo");
        AddTitle(e, item);
        AddPlot(e, item);
        AddYear(e, item);
        AddRuntime(e, item);
        AddText(e, "album", item.Album);
        AddText(e, "artist", item.PrimaryArtist);
        AddUniqueIds(e, item);
        return Serialize(e);
    }

    public static string Album(RemoteItem item)
    {
        var e = new XElement("album");
        AddText(e, "title", item.Name ?? item.Album);
        AddText(e, "artist", item.PrimaryArtist);
        AddYear(e, item);
        AddUniqueIds(e, item);
        AddGenres(e, item);
        return Serialize(e);
    }

    private static string Serialize(XElement root)
        => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + root.ToString();

    private static void AddTitle(XElement e, RemoteItem item)
    {
        AddText(e, "title", item.Name);
        AddText(e, "originaltitle", item.OriginalTitle);
        AddText(e, "sorttitle", item.SortName);
    }

    private static void AddPlot(XElement e, RemoteItem item) => AddText(e, "plot", item.Overview);

    private static void AddDate(XElement e, string name, string? value) => AddText(e, name, FormatDate(value));

    /// <summary>
    /// NFO consumers expect a plain yyyy-MM-dd date. Remote values are ISO 8601 UTC timestamps, so the
    /// instant is preserved and rendered in the local time zone (same wall-clock date the source UI shows).
    /// </summary>
    private static string? FormatDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return value.Trim();
    }

    private static void AddYear(XElement e, RemoteItem item)
    {
        if (item.ProductionYear is int y)
        {
            AddText(e, "year", y.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AddRuntime(XElement e, RemoteItem item)
    {
        if (item.RunTimeTicks is long ticks && ticks > 0)
        {
            var minutes = (int)Math.Round(ticks / 600000000.0);
            if (minutes > 0)
            {
                AddText(e, "runtime", minutes.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private static void AddRatings(XElement e, RemoteItem item)
    {
        if (item.CommunityRating is double cr && cr > 0)
        {
            AddText(e, "rating", cr.ToString("0.0#####", CultureInfo.InvariantCulture));
        }

        if (item.CriticRating is double cc && cc > 0)
        {
            AddText(e, "criticrating", cc.ToString("0.0#####", CultureInfo.InvariantCulture));
        }
    }

    private static void AddGenres(XElement e, RemoteItem item)
    {
        if (item.Genres is null)
        {
            return;
        }

        foreach (var g in item.Genres.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            e.Add(new XElement("genre", g.Trim()));
        }
    }

    private static void AddUniqueIds(XElement e, RemoteItem item)
    {
        if (item.ProviderIds is null || item.ProviderIds.Count == 0)
        {
            return;
        }

        var first = true;
        foreach (var key in KnownProviders)
        {
            if (item.ProviderIds.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                var el = new XElement("uniqueid", value.Trim());
                el.SetAttributeValue("type", key);
                if (first)
                {
                    el.SetAttributeValue("default", "true");
                    first = false;
                }

                e.Add(el);
            }
        }

        foreach (var kv in item.ProviderIds)
        {
            if (KnownProviders.Contains(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
            {
                continue;
            }

            var el = new XElement("uniqueid", kv.Value.Trim());
            el.SetAttributeValue("type", kv.Key);
            e.Add(el);
        }
    }

    private static void AddText(XElement e, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            e.Add(new XElement(name, value.Trim()));
        }
    }
}
