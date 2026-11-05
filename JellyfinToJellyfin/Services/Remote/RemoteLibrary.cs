namespace JellyfinToJellyfin.Services.Remote;

/// <summary>
/// Minimal DTO for a remote library as returned by GET /Library/VirtualFolders.
/// </summary>
public sealed class RemoteLibrary
{
    /// <summary>Gets or sets the library name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the library (CollectionFolder) item id used as the item query parent.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets the configured filesystem locations.</summary>
    public string[]? Locations { get; set; }

    /// <summary>Gets or sets the collection type string (e.g. "movies", "tvshows", "music").</summary>
    public string? CollectionType { get; set; }

    /// <summary>Gets or sets the primary image item id.</summary>
    public string? PrimaryImageItemId { get; set; }

    /// <summary>Gets or sets the collection type of this library as a normalised string (movies, tvshows, music, mixed, ...).</summary>
    public string GetEffectiveCollectionType()
        => string.IsNullOrWhiteSpace(CollectionType) ? "mixed" : CollectionType!;
}
