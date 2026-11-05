using System.Collections.Generic;

namespace JellyfinToJellyfin.Services.Remote;

/// <summary>
/// Minimal DTO matching the QueryResult shape returned by GET /Items.
/// </summary>
public sealed class RemoteItemsResult
{
    public List<RemoteItem>? Items { get; set; }

    public int TotalRecordCount { get; set; }

    public int StartIndex { get; set; }
}
