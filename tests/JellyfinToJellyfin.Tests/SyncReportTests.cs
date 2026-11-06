using JellyfinToJellyfin.Services;

namespace JellyfinToJellyfin.Tests;

public sealed class SyncReportTests
{
    [Fact]
    public void Merge_AddsCountersFromBothReports()
    {
        var aggregate = new SyncReport { InstanceName = "All instances", StrmWritten = 1, DuplicatesSkipped = 2, StaleRemoved = 3 };
        var other = new SyncReport { InstanceName = "remote", StrmWritten = 4, DuplicatesSkipped = 5, StaleRemoved = 6, LibrariesSynced = 1 };

        aggregate.Merge(other);

        Assert.Equal(5, aggregate.StrmWritten);
        Assert.Equal(7, aggregate.DuplicatesSkipped);
        Assert.Equal(9, aggregate.StaleRemoved);
        Assert.Equal(1, aggregate.LibrariesSynced);
    }

    [Fact]
    public void ToDto_ExposesStaleRemoved()
    {
        var dto = new SyncReport { StaleRemoved = 7 }.ToDto();

        Assert.Equal(7, dto["staleRemoved"]);
    }

    [Fact]
    public void Summary_MentionsRemovedStaleFiles()
    {
        var summary = new SyncReport { Success = true, LibrariesSynced = 2, ItemsSeen = 10, StrmWritten = 8, DuplicatesSkipped = 1, StaleRemoved = 4 }.Summary();

        Assert.Contains("4 stale removed", summary, StringComparison.Ordinal);
    }
}
