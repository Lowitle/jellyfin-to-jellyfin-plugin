namespace JellyfinToJellyfin.Configuration;

/// <summary>
/// Maps a remote library to a local library (existing or newly created).
/// </summary>
public class LibraryMapping
{
    /// <summary>
    /// Gets or sets the remote library identifier.
    /// </summary>
    public string RemoteLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the remote library name.
    /// </summary>
    public string RemoteLibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the remote collection type (movies, tvshows, music, ...).
    /// </summary>
    public string RemoteCollectionType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this mapping is included in the sync.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the folder name created under the instance base path. Defaults to the remote library name.
    /// </summary>
    public string FolderName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target local library name. Used when creating a new library or when attaching to an existing one by name.
    /// </summary>
    public string TargetLibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target collection type used when creating a new library.
    /// </summary>
    public string TargetCollectionType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether a new local library should be created for this mapping.
    /// </summary>
    public bool CreateNewLibrary { get; set; } = true;

    /// <summary>
    /// Gets or sets the id of an existing local library to attach the generated folder to (optional).
    /// </summary>
    public string TargetLibraryId { get; set; } = string.Empty;
}
