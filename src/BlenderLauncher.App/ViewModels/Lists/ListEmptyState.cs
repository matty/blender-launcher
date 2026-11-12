namespace BlenderLauncher.App.ViewModels.Lists;

/// <summary>What the main area shows instead of rows; <see cref="None"/> when there are rows.</summary>
public enum ListEmptyState
{
    None,

    /// <summary>No library folder is set.</summary>
    NoLibrary,

    /// <summary>Nothing installed in this view (with "Browse downloads").</summary>
    NoBuilds,

    /// <summary>The Favorites view has no favourites.</summary>
    NoFavourites,

    /// <summary>The filter text matches nothing.</summary>
    NoMatch,

    /// <summary>A check is running (or the first one has not finished) and this channel has no rows yet.</summary>
    Checking,

    /// <summary>This channel's source could not connect.</summary>
    Offline,

    /// <summary>This channel's source failed for another reason.</summary>
    SourceError,

    /// <summary>The last check found no builds for this channel.</summary>
    NoneAvailable,
}
