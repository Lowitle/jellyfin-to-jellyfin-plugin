using System;
using System.Collections.Generic;

namespace JellyfinToJellyfin.Services;

/// <summary>
/// Outcome of a single instance or full sync run.
/// </summary>
public sealed class SyncReport
{
    public string InstanceName { get; set; } = string.Empty;

    public bool Success { get; set; }

    public double DurationSeconds { get; set; }

    public int LibrariesSynced { get; set; }

    public int ItemsSeen { get; set; }

    public int StrmWritten { get; set; }

    public int NfoWritten { get; set; }

    public int ImagesWritten { get; set; }

    public int DuplicatesSkipped { get; set; }

    public int StaleRemoved { get; set; }

    public List<string> Errors { get; } = new();

    public List<string> Warnings { get; } = new();

    public List<string> Notes { get; } = new();

    public void AddError(string message) => Errors.Add(message);

    public void AddWarning(string message) => Warnings.Add(message);

    public void AddNote(string message) => Notes.Add(message);

    public void Merge(SyncReport other)
    {
        LibrariesSynced += other.LibrariesSynced;
        ItemsSeen += other.ItemsSeen;
        StrmWritten += other.StrmWritten;
        NfoWritten += other.NfoWritten;
        ImagesWritten += other.ImagesWritten;
        DuplicatesSkipped += other.DuplicatesSkipped;
        StaleRemoved += other.StaleRemoved;
        Errors.AddRange(other.Errors);
        Warnings.AddRange(other.Warnings);
        Notes.AddRange(other.Notes);
    }

    public string Summary()
        => Success
            ? $"OK - {LibrariesSynced} libraries, {ItemsSeen} items, {StrmWritten} strm, {DuplicatesSkipped} duplicate path(s), {StaleRemoved} stale removed at {DateTime.UtcNow:u}"
            : $"Failed - {Errors.Count} error(s) at {DateTime.UtcNow:u}";

    public Dictionary<string, object?> ToDto() => new()
    {
        ["instanceName"] = InstanceName,
        ["success"] = Success,
        ["durationSeconds"] = Math.Round(DurationSeconds, 1),
        ["librariesSynced"] = LibrariesSynced,
        ["itemsSeen"] = ItemsSeen,
        ["strmWritten"] = StrmWritten,
        ["nfoWritten"] = NfoWritten,
        ["imagesWritten"] = ImagesWritten,
        ["duplicatesSkipped"] = DuplicatesSkipped,
        ["staleRemoved"] = StaleRemoved,
        ["errors"] = Errors,
        ["warnings"] = Warnings,
        ["notes"] = Notes
    };
}
