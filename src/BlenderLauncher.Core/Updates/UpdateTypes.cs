namespace BlenderLauncher.Core.Updates;

/// <summary>The step a self-update is on, for the progress window.</summary>
public enum UpdateStage
{
    /// <summary>Velopack is downloading (and checking) the release package.</summary>
    Downloading,

    /// <summary>The package is ready; the launcher closes and Velopack's updater installs it.</summary>
    Installing,
}

/// <summary>Progress of a self-update: the step, and how far through it (null when unknown).</summary>
public readonly record struct UpdateProgress(UpdateStage Stage, double? Fraction);

public enum UpdateOutcome
{
    /// <summary>The release is downloaded and verified; applying it closes the launcher.</summary>
    Ready,

    /// <summary>The update source has nothing newer than the running version.</summary>
    UpToDate,

    /// <summary>The check or the download failed; nothing was changed.</summary>
    Failed,

    Cancelled,
}

/// <summary>The outcome of preparing a self-update.</summary>
public sealed record UpdateResult(UpdateOutcome Outcome, string? Error = null)
{
    public bool Succeeded => Outcome == UpdateOutcome.Ready;
}
