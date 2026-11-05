using System.Collections.Generic;
using System.Linq;

namespace JellyfinToJellyfin.Services.Remote;

/// <summary>
/// Minimal DTO describing a single remote media item returned by GET /Items.
/// Only the fields needed to build the mirror are captured.
/// </summary>
public sealed class RemoteItem
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    /// <summary>Gets or sets the item kind string, e.g. Movie, Series, Season, Episode, Audio, MusicAlbum, MusicArtist, Video, Folder.</summary>
    public string? Type { get; set; }

    public string? ParentId { get; set; }

    public int? IndexNumber { get; set; }

    public int? ParentIndexNumber { get; set; }

    public int? IndexNumberEnd { get; set; }

    public string? SeriesId { get; set; }

    public string? SeriesName { get; set; }

    public string? SeasonId { get; set; }

    public string? SeasonName { get; set; }

    public int? ProductionYear { get; set; }

    public string? Overview { get; set; }

    public string[]? Genres { get; set; }

    public Dictionary<string, string>? ProviderIds { get; set; }

    public Dictionary<string, string>? ImageTags { get; set; }

    public string[]? BackdropImageTags { get; set; }

    public long? RunTimeTicks { get; set; }

    public string? MediaType { get; set; }

    public string? PremiereDate { get; set; }

    public string? OfficialRating { get; set; }

    public double? CommunityRating { get; set; }

    public double? CriticRating { get; set; }

    public string? OriginalTitle { get; set; }

    public string? SortName { get; set; }

    public string? Album { get; set; }

    public string? AlbumArtist { get; set; }

    /// <summary>Gets or sets the album artists. Jellyfin returns objects (id + name) here, not plain strings.</summary>
    public NameIdPair[]? AlbumArtists { get; set; }

    public string[]? Artists { get; set; }

    /// <summary>Gets or sets the best available artist name for the item.</summary>
    public string? PrimaryArtist => AlbumArtist ?? AlbumArtists?.FirstOrDefault()?.Name ?? Artists?.FirstOrDefault();

    public string? Container { get; set; }

    public bool? IsFolder { get; set; }

    public int? ChildCount { get; set; }

    /// <summary>Gets or sets a value indicating whether the primary image is available.</summary>
    public bool HasPrimaryImage => ImageTags is not null && ImageTags.ContainsKey("Primary");

    /// <summary>Gets or sets a value indicating whether a backdrop image is available.</summary>
    public bool HasBackdrop => BackdropImageTags is { Length: > 0 };

    /// <summary>Returns true when the item has the given image type.</summary>
    public bool HasImage(string imageType) => ImageTags is not null && ImageTags.ContainsKey(imageType);

    /// <summary>Minimal id/name pair as returned by Jellyfin for AlbumArtists and ArtistItems.</summary>
    public sealed class NameIdPair
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        /// <inheritdoc />
        public override string? ToString() => Name;
    }
}
