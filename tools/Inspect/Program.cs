// Dev helper: dumps Jellyfin 10.11.6 enum/property shapes so the DTOs used by
// JellyfinToJellyfin\Services\Remote can be re-checked against the server model after a version bump.
using System;
using System.Linq;

var assemblies = new[]
{
    typeof(MediaBrowser.Controller.Library.ILibraryManager).Assembly,
    typeof(MediaBrowser.Model.Dto.BaseItemDto).Assembly,
    typeof(Jellyfin.Data.Enums.BaseItemKind).Assembly
};

foreach (var asm in assemblies.Distinct())
{
    foreach (var type in asm.GetTypes().Where(t => t.IsEnum && t.Name.Equals("ItemFields", StringComparison.Ordinal)))
    {
        Console.WriteLine($"{asm.GetName().Name}: {type.FullName}");
        var names = Enum.GetNames(type);
        Console.WriteLine(string.Join(", ", names));
        Console.WriteLine($"count={names.Length}");
        Console.WriteLine();
    }
}

// Property types we map onto our own DTOs; a mismatch here breaks JSON deserialization at runtime.
var dto = typeof(MediaBrowser.Model.Dto.BaseItemDto);
string[] wanted =
{
    "Id", "Name", "Type", "ParentId", "IndexNumber", "ParentIndexNumber", "IndexNumberEnd",
    "SeriesId", "SeriesName", "SeasonId", "SeasonName", "ProductionYear", "Overview", "Genres",
    "ProviderIds", "ImageTags", "BackdropImageTags", "RunTimeTicks", "MediaType", "PremiereDate",
    "OfficialRating", "CommunityRating", "CriticRating", "OriginalTitle", "SortName", "Album",
    "AlbumArtist", "AlbumArtists", "Artists", "ArtistItems", "Container", "IsFolder", "ChildCount"
};

Console.WriteLine($"--- {dto.FullName} property types ---");
foreach (var name in wanted)
{
    var property = dto.GetProperty(name);
    Console.WriteLine($"{name,-20} {property?.PropertyType.ToString() ?? "<missing>"}");
}