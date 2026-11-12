namespace BlenderLauncher.Core.Settings.Upstream;

/// <summary>Whether the import ran.</summary>
public enum UpstreamImportStatus
{
    /// <summary>The INI was read and applied.</summary>
    Imported,

    /// <summary>An earlier run already imported (<see cref="InternalSettings.UpstreamImportDone"/>); nothing was read.</summary>
    AlreadyDone,

    /// <summary>No upstream INI at the expected path.</summary>
    NotFound,

    /// <summary>The INI exists but could not be read.</summary>
    Unreadable,
}

/// <summary>What happened to one key.</summary>
public enum ImportOutcome
{
    /// <summary>Copied to its counterpart.</summary>
    Imported,

    /// <summary>Converted to a different port setting.</summary>
    Mapped,

    /// <summary>Present but deliberately left out (the message says why).</summary>
    Skipped,

    /// <summary>Not carried over by design.</summary>
    Dropped,

    /// <summary>Present but unusable; the port's value was kept.</summary>
    Failed,
}

/// <summary>One line of the import report. Messages never contain secret values.</summary>
public sealed record ImportEntry(string Key, ImportOutcome Outcome, string Message)
{
    public override string ToString() => $"{Key}: {Outcome} - {Message}";
}

/// <summary>The result of an upstream settings import.</summary>
public sealed record UpstreamImportResult(UpstreamImportStatus Status, string? SourcePath, IReadOnlyList<ImportEntry> Entries)
{
    /// <summary>Keys copied or converted.</summary>
    public IEnumerable<ImportEntry> Imported => Entries.Where(e => e.Outcome is ImportOutcome.Imported or ImportOutcome.Mapped);

    /// <summary>Keys left out on purpose (skipped or dropped).</summary>
    public IEnumerable<ImportEntry> Skipped => Entries.Where(e => e.Outcome is ImportOutcome.Skipped or ImportOutcome.Dropped);

    /// <summary>Keys whose values could not be used.</summary>
    public IEnumerable<ImportEntry> Failed => Entries.Where(e => e.Outcome == ImportOutcome.Failed);

    /// <summary>
    /// Upstream's library folder when it was relative to upstream's exe folder: the UI asks the user to pick the
    /// folder. Null otherwise.
    /// </summary>
    public string? RelativeLibraryFolder { get; init; }

    /// <summary>The entry for <paramref name="key"/>, if any.</summary>
    public ImportEntry? For(string key) => Entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));

    public static UpstreamImportResult NotRun(UpstreamImportStatus status, string? path, string? message = null) =>
        new(status, path, message is null ? [] : [new ImportEntry("(file)", ImportOutcome.Failed, message)]);
}
