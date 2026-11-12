using System.Globalization;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Library;
using BlenderLauncher.Core.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlenderLauncher.App.ViewModels;

/// <summary>
/// The custom build dialog (upstream's "Build Entry Creator", <c>CustomBuildDialog</c>): the build's executable
/// (with completion of the <c>.exe</c>, <c>.bat</c> and <c>.cmd</c> files in its folder), "Auto-detect information"
/// (runs the executable with <c>-v</c>), version, hash, commit time, branch, custom name and favourite. Accept is
/// enabled when the executable is a file in the folder, the version parses and the time reads. Used for "Initialise"
/// (an unrecognised folder) and "Edit build…"; <see cref="ToBuildInfo"/> gives the <c>.blinfo</c> fields to write.
/// </summary>
public sealed partial class CustomBuildViewModel : ViewModelBase
{
    /// <summary>The commit time's text format (local time).</summary>
    public const string TimeFormat = "yyyy-MM-dd HH:mm";

    private static readonly string[] ExecutableExtensions = [".exe", ".bat", ".cmd"];

    private readonly Func<string, CancellationToken, Task<VersionOutput>>? _detect;
    private readonly string _originalTimeText;

    /// <param name="folder">The build folder.</param>
    /// <param name="existing">The build's current information (Edit build), or null (Initialise).</param>
    /// <param name="detect">Runs an executable with <c>-v</c> and parses the output; null hides nothing but detection fails.</param>
    public CustomBuildViewModel(string folder, BuildInfo? existing, Func<string, CancellationToken, Task<VersionOutput>>? detect)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        Folder = LibraryLayout.NormaliseRoot(folder);
        Existing = existing;
        _detect = detect;
        ExecutableCompletions = FindExecutables(Folder);

        var detected = Path.GetFileName(BuildInfoReader.DetectExecutable(Folder));
        if (existing is not null)
        {
            Executable = string.IsNullOrEmpty(existing.CustomExecutable) ? detected : existing.CustomExecutable.Replace('/', '\\');
            Version = existing.Subversion;
            Hash = existing.BuildHash ?? "";
            CommitTimeText = existing.CommitTime.ToDateTimeOffset(TimeZoneInfo.Local).ToLocalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);
            Branch = existing.Branch;
            CustomName = existing.CustomName;
            IsFavourite = existing.IsFavorite;
        }
        else
        {
            Executable = File.Exists(Path.Combine(Folder, detected)) ? detected : ExecutableCompletions.FirstOrDefault() ?? "";
            CommitTimeText = SafeLastWrite(Folder).ToString(TimeFormat, CultureInfo.InvariantCulture);
            Branch = DefaultBranch;
        }

        _originalTimeText = CommitTimeText;
    }

    public string Folder { get; }

    public BuildInfo? Existing { get; }

    public bool IsEdit => Existing is not null;

    /// <summary>The window title: "Build Entry Creator" or "Edit build".</summary>
    public string Title => IsEdit ? Loc.Tr("ui.custom_build.edit_title") : Loc.Tr("custom_build.title");

    /// <summary>"Create new build for:" or "Build information for:", without the path.</summary>
    public string Heading => (IsEdit ? Loc.Tr("ui.custom_build.edit_text", ("path", "")) : Loc.Tr("custom_build.text", ("path", ""))).Trim();

    /// <summary>The <c>.exe</c>, <c>.bat</c> and <c>.cmd</c> files directly in the folder, for completion.</summary>
    public IReadOnlyList<string> ExecutableCompletions { get; }

    /// <summary>The branch upstream would derive from the folder: its parent folder, or the folder's name in <c>custom</c>.</summary>
    public string DefaultBranch => BuildInfoReader.BranchFor(Path.GetFileName(Path.GetDirectoryName(Folder)) ?? "", Path.GetFileName(Folder), null);

    // ------------------------------------------------------------------ fields

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExecutableValid), nameof(ExecutableError), nameof(HasExecutableError), nameof(CanAccept))]
    [NotifyCanExecuteChangedFor(nameof(AutoDetectCommand))]
    public partial string Executable { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVersionValid), nameof(VersionError), nameof(HasVersionError), nameof(CanAccept))]
    public partial string Version { get; set; } = "";

    [ObservableProperty]
    public partial string Hash { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTimeValid), nameof(TimeError), nameof(HasTimeError), nameof(CanAccept))]
    public partial string CommitTimeText { get; set; } = "";

    [ObservableProperty]
    public partial string Branch { get; set; } = "";

    [ObservableProperty]
    public partial string CustomName { get; set; } = "";

    [ObservableProperty]
    public partial bool IsFavourite { get; set; }

    /// <summary>The executable is a .exe, .bat or .cmd file inside the build folder.</summary>
    public bool IsExecutableValid => ExecutablePath(Executable) is not null;

    public string? ExecutableError => Executable.Trim().Length > 0 && !IsExecutableValid ? Loc.Tr("ui.custom_build.exe_invalid") : null;

    public bool HasExecutableError => ExecutableError is not null;

    public bool IsVersionValid => BlenderVersionParser.TryParse(Version.Trim(), out _);

    public string? VersionError => Version.Trim().Length > 0 && !IsVersionValid ? Loc.Tr("ui.custom_build.version_invalid") : null;

    public bool HasVersionError => VersionError is not null;

    public bool IsTimeValid => ParseTime(CommitTimeText) is not null;

    public string? TimeError => IsTimeValid ? null : Loc.Tr("ui.custom_build.time_invalid");

    public bool HasTimeError => TimeError is not null;

    /// <summary>Upstream's rule: Accept needs a valid executable and a version that parses (and here, a readable time).</summary>
    public bool CanAccept => IsExecutableValid && IsVersionValid && IsTimeValid && !IsDetecting;

    // ------------------------------------------------------------------ auto-detect

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAccept))]
    [NotifyCanExecuteChangedFor(nameof(AutoDetectCommand))]
    public partial bool IsDetecting { get; private set; }

    /// <summary>"Reading the build…", "Information read from the build." or the failure.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetectStatus))]
    public partial string? DetectStatus { get; private set; }

    [ObservableProperty]
    public partial bool DetectFailed { get; private set; }

    public bool HasDetectStatus => DetectStatus is not null;

    private bool CanAutoDetect => IsExecutableValid && !IsDetecting && _detect is not null;

    /// <summary>"Auto-detect information": runs <c>-v</c> and fills the version, hash and commit time it prints.</summary>
    [RelayCommand(CanExecute = nameof(CanAutoDetect))]
    private async Task AutoDetectAsync(CancellationToken ct)
    {
        if (_detect is null || ExecutablePath(Executable) is not { } exe)
        {
            return;
        }

        IsDetecting = true;
        DetectFailed = false;
        DetectStatus = Loc.Tr("ui.custom_build.detecting");
        try
        {
            var output = await _detect(exe, ct).ConfigureAwait(true);
            Apply(output);
            DetectStatus = Loc.Tr("ui.custom_build.detected");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            DetectFailed = true;
            DetectStatus = Loc.Tr("ui.custom_build.detect_failed", ("error", e.Message));
        }
        finally
        {
            IsDetecting = false;
        }
    }

    /// <summary>Fills the fields <c>-v</c> printed, as upstream's <c>ReadBuildTask</c> result does.</summary>
    public void Apply(VersionOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Subversion is { Length: > 0 } subversion)
        {
            Version = subversion;
        }
        else if (output.FirstLine is { } line && line.LastIndexOf(' ') is var space and > 0)
        {
            // Upstream: the first line split at its last space into a name and a version ("Blender 4.2.0").
            Version = line[(space + 1)..];
            if (CustomName.Length == 0 && line[..space] is var name && !name.Equals("Blender", StringComparison.OrdinalIgnoreCase))
            {
                CustomName = name;
            }
        }

        if (output.BuildHash.Length > 0)
        {
            Hash = output.BuildHash;
        }

        if (output.CommitTime is { } time)
        {
            CommitTimeText = time.ToDateTimeOffset(TimeZoneInfo.Local).ToLocalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);
        }
    }

    // ------------------------------------------------------------------ result

    /// <summary>
    /// The <c>.blinfo</c> fields: the executable is stored relative to the folder (POSIX separators, as upstream), or
    /// left empty when it is the one auto-detection picks anyway. An edit keeps the frozen flag and, when the time was
    /// not touched, the original timestamp with its offset. Null when the fields are not valid.
    /// </summary>
    public BuildInfo? ToBuildInfo()
    {
        if (!CanAccept || ExecutablePath(Executable) is not { } exe || ParseTime(CommitTimeText) is not { } local)
        {
            return null;
        }

        var relative = Path.GetRelativePath(Folder, exe);
        var detected = BuildInfoReader.DetectExecutable(Folder);
        string? customExe = LibraryLayout.PathComparer.Equals(exe, detected) ? null : relative.Replace('\\', '/');

        var time = Existing is { } existing && CommitTimeText == _originalTimeText
            ? existing.CommitTime
            : IsoTimestamp.Aware(new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)));
        var branch = Branch.Trim();
        return new BuildInfo
        {
            Link = Folder,
            Subversion = Version.Trim(),
            BuildHash = Hash.Trim() is { Length: > 0 } hash ? hash : null,
            CommitTime = time,
            Branch = branch.Length > 0 ? branch : DefaultBranch,
            CustomName = CustomName.Trim(),
            IsFavorite = IsFavourite,
            CustomExecutable = customExe,
            IsFrozen = Existing?.IsFrozen ?? false,
        };
    }

    /// <summary>The full path of <paramref name="text"/> when it names an executable file inside the folder, else null.</summary>
    public string? ExecutablePath(string? text)
    {
        var trimmed = text?.Trim().Trim('"');
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(Path.Combine(Folder, trimmed));
            return LibraryLayout.IsSameOrInside(full, Folder) && !LibraryLayout.PathComparer.Equals(full, Folder)
                   && ExecutableExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase)
                   && File.Exists(full)
                ? full
                : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static DateTime? ParseTime(string? text) =>
        DateTime.TryParseExact(text?.Trim(), [TimeFormat, "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var value)
            ? DateTime.SpecifyKind(value, DateTimeKind.Unspecified)
            : null;

    private static IReadOnlyList<string> FindExecutables(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(f => ExecutableExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static DateTime SafeLastWrite(string folder)
    {
        try
        {
            return Directory.GetLastWriteTime(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DateTime.Now;
        }
    }
}
