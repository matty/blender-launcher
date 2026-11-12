using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlenderLauncher.App.ViewModels.Detail;

/// <summary>
/// One entry of the build actions, shown as a context-menu item and as a row of the detail panel's More
/// section: a command with a header, a group of entries (the Debug submenu), or a separator. Entries that
/// cannot apply are not created, so a hidden entry is simply absent.
/// </summary>
public sealed partial class BuildMenuEntry : ViewModelBase
{
    private BuildMenuEntry(string id, string header, ICommand? command, IReadOnlyList<BuildMenuEntry> children)
    {
        Id = id;
        Header = header;
        Command = command;
        Children = children;
    }

    /// <summary>A stable name for tests and the view (<see cref="BuildMenuIds"/>).</summary>
    public string Id { get; }

    /// <summary>The text; changes for the Shift-dependent entries while a menu is open.</summary>
    [ObservableProperty]
    public partial string Header { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToolTip))]
    public partial string? ToolTip { get; set; }

    public bool HasToolTip => !string.IsNullOrEmpty(ToolTip);

    /// <summary>Run when the entry is chosen; null for groups and separators.</summary>
    public ICommand? Command { get; }

    /// <summary>The submenu's entries (Debug); empty otherwise.</summary>
    public IReadOnlyList<BuildMenuEntry> Children { get; }

    public bool HasChildren => Children.Count > 0;

    public bool IsSeparator => Id == BuildMenuIds.Separator;

    /// <summary>Shown greyed out (for example "Create symlink" when the link already points here).</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    /// <summary>A tick beside the header ("Use as default").</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>Drawn in the danger colour (delete).</summary>
    public bool IsDanger { get; init; }

    /// <summary>For the More section: a child of a group, indented under the group's label.</summary>
    public bool IsNested { get; init; }

    public static BuildMenuEntry Action(string id, string header, ICommand command, bool isDanger = false) =>
        new(id, header, command, []) { IsDanger = isDanger };

    public static BuildMenuEntry Group(string id, string header, IReadOnlyList<BuildMenuEntry> children) => new(id, header, null, children);

    public static BuildMenuEntry Separator() => new(BuildMenuIds.Separator, "", null, []);

    /// <summary>A copy for the panel's flat list, marked as nested under its group.</summary>
    internal BuildMenuEntry AsNested() => new(Id, Header, Command, Children) { IsNested = true, IsDanger = IsDanger, IsEnabled = IsEnabled, ToolTip = ToolTip };
}

/// <summary>The ids of <see cref="BuildMenuEntry"/>.</summary>
public static class BuildMenuIds
{
    public const string Separator = "-";
    public const string Launch = "launch";
    public const string OpenLast = "open-last";
    public const string Debug = "debug";
    public const string DebugLog = "debug-log";
    public const string DebugFactory = "debug-factory";
    public const string DebugGpu = "debug-gpu";
    public const string DebugGpuWorkaround = "debug-gpu-workaround";
    public const string Favourite = "favourite";
    public const string Default = "default";
    public const string Update = "update";
    public const string Register = "register";
    public const string Shortcut = "shortcut";
    public const string Symlink = "symlink";
    public const string Template = "template";
    public const string Portable = "portable";
    public const string CopyHash = "copy-hash";
    public const string Freeze = "freeze";
    public const string ReleaseNotes = "release-notes";
    public const string FetchPrName = "fetch-pr-name";
    public const string BuildFolder = "build-folder";
    public const string ConfigFolder = "config-folder";
    public const string Rename = "rename";
    public const string Edit = "edit";
    public const string Remove = "remove";
    public const string Download = "download";
    public const string Cancel = "cancel";
    public const string ShowInLibrary = "show-in-library";
}
