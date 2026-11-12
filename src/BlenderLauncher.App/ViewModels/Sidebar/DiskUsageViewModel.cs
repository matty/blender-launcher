using System.Security;
using BlenderLauncher.App.Localisation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlenderLauncher.App.ViewModels.Sidebar;

/// <summary>The size and free space of the volume that holds the library.</summary>
public readonly record struct VolumeInfo(long TotalBytes, long FreeBytes);

/// <summary>
/// The sidebar's disk-usage card: the library folder, the total size of the installed builds, and a bar of that size
/// against the volume's capacity, with the free space in the tooltip.
/// </summary>
public sealed partial class DiskUsageViewModel : ViewModelBase
{
    /// <summary>The bar never shrinks below this while builds take any space, so it reads as "some" on large drives.</summary>
    public const double MinimumVisibleFraction = 0.02;

    private readonly Func<string, VolumeInfo?> _probe;
    private int _probeVersion;

    /// <param name="probe">Reads a path's volume; <see cref="ProbeVolume"/> (<see cref="DriveInfo"/>) by default.</param>
    public DiskUsageViewModel(Func<string, VolumeInfo?>? probe = null)
    {
        _probe = probe ?? ProbeVolume;
    }

    /// <summary>The library folder, or null when none is set.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLibrary), nameof(PathText), nameof(TooltipText))]
    public partial string? LibraryRoot { get; set; }

    /// <summary>The installed builds' total size.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsedText), nameof(Fraction), nameof(TooltipText))]
    public partial long UsedBytes { get; set; }

    /// <summary>The library's volume, once read; null when unknown (no library, offline drive, network path).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Fraction), nameof(TooltipText))]
    public partial VolumeInfo? Volume { get; private set; }

    public bool HasLibrary => !string.IsNullOrEmpty(LibraryRoot);

    /// <summary>The path line: the library folder, or "No library folder".</summary>
    public string PathText => HasLibrary ? LibraryRoot! : Loc.Tr("ui.sidebar.no_library");

    /// <summary>"2.9 GB", as the design.</summary>
    public string UsedText => DisplayFormat.SizeGigabytes(UsedBytes, 1);

    /// <summary>0 to 1: the installed builds against the volume's capacity.</summary>
    public double Fraction
    {
        get
        {
            if (Volume is not { TotalBytes: > 0 } volume || UsedBytes <= 0)
            {
                return 0;
            }

            return Math.Clamp(Math.Max(UsedBytes / (double)volume.TotalBytes, MinimumVisibleFraction), 0, 1);
        }
    }

    /// <summary>
    /// The full folder (the card trims it) over "2.9 GB used by builds · 120.4 GB free of 999.6 GB"; just the folder
    /// when the volume is unknown.
    /// </summary>
    public string TooltipText => Volume is { } volume
        ? PathText + "\n" + Loc.Tr("ui.sidebar.disk_tooltip",
            ("used", DisplayFormat.Size(UsedBytes)),
            ("free", DisplayFormat.Size(volume.FreeBytes)),
            ("total", DisplayFormat.Size(volume.TotalBytes)))
        : PathText;

    /// <summary>Reads the volume of <see cref="LibraryRoot"/> off the UI thread; the latest call wins.</summary>
    public async Task RefreshVolumeAsync()
    {
        var version = ++_probeVersion;
        var root = LibraryRoot;
        VolumeInfo? info = null;
        if (!string.IsNullOrEmpty(root))
        {
            info = await Task.Run(() => _probe(root)).ConfigureAwait(true);
        }

        if (version == _probeVersion)
        {
            Volume = info;
        }
    }

    /// <summary>The volume holding <paramref name="path"/> via <see cref="DriveInfo"/>; null when it cannot be read.</summary>
    public static VolumeInfo? ProbeVolume(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? new VolumeInfo(drive.TotalSize, drive.AvailableFreeSpace) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or SecurityException
                                      or NotSupportedException)
        {
            return null;
        }
    }
}
