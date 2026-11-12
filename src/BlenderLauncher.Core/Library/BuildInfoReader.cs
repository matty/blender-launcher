using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BlenderLauncher.Core.Api;
using BlenderLauncher.Core.Builds;
using BlenderLauncher.Core.Logging;

namespace BlenderLauncher.Core.Library;

/// <summary>What <see cref="BuildInfoReader.ParseVersionOutput"/> found in <c>-v</c> output.</summary>
public sealed record VersionOutput(IsoTimestamp? CommitTime, string BuildHash, string? Subversion, string? FirstLine);

/// <summary>
/// Reads, derives and writes a build folder's <see cref="BuildInfo"/>: a port of upstream's
/// <c>fill_build_info</c>, <c>read_blender_version</c> and <c>fill_blender_info</c> (<c>build_info.py:395-644</c>).
/// Windows layout only: the macOS <c>.app</c> branches are not ported.
/// </summary>
public sealed partial class BuildInfoReader
{
    private readonly IBlenderVersionProbe _probe;
    private readonly ILtsLookup? _lts;
    private readonly Log? _log;
    private readonly TimeProvider _time;

    /// <summary>Creates a reader. <paramref name="lts"/> drives LTS promotion of <c>stable</c> builds when given.</summary>
    public BuildInfoReader(IBlenderVersionProbe probe, ILtsLookup? lts = null, Log? log = null, TimeProvider? time = null)
    {
        _probe = probe;
        _lts = lts;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How long <c>-v</c> may run before it counts as failed. Upstream had no limit.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Upstream's <c>fill_build_info</c>. With a <c>.blinfo</c>: returned as read, unless its
    /// <c>file_version</c> is not <c>"1.5"</c> or the info is invalid, in which case it is re-derived from the
    /// executable (the old info supplying everything <c>-v</c> does not override) and rewritten. Without one: derived
    /// from the executable, with <paramref name="info"/> (scraped data) as fallback, and written when
    /// <paramref name="autoWrite"/> is set. Throws when the folder cannot be read; the caller shows it as damaged.
    /// </summary>
    public async Task<BuildInfo> ReadAsync(
        string buildFolder,
        string? archiveName = null,
        BuildInfo? info = null,
        bool autoWrite = true,
        CancellationToken ct = default)
    {
        var folder = LibraryLayout.NormaliseRoot(buildFolder);
        var blinfo = Path.Combine(folder, BlinfoCodec.FileName);
        if (File.Exists(blinfo))
        {
            var document = await BlinfoCodec.ReadFileAsync(folder, ct).ConfigureAwait(false);
            if (document.Blinfo.Count == 0)
            {
                throw new JsonException($"{blinfo} has no build entry");
            }

            var stored = BlinfoCodec.ToBuildInfo(document.Blinfo[0], folder, _lts, _time);
            if (document.FileVersion != BlinfoCodec.CurrentFileVersion || !stored.IsValid)
            {
                _log?.Warning(document.FileVersion != BlinfoCodec.CurrentFileVersion
                    ? $"Migrating .blinfo of {folder} from file version {document.FileVersion ?? "(none)"}"
                    : $"Invalid .blinfo data for {folder} (subversion={stored.Subversion}, branch={stored.Branch}), regenerating");
                var derived = await DeriveAsync(folder, stored, archiveName, ct).ConfigureAwait(false);
                await BlinfoCodec.WriteFileAsync(folder, derived, ct).ConfigureAwait(false);
                return derived;
            }

            return stored;
        }

        var fresh = await DeriveAsync(folder, info, archiveName, ct).ConfigureAwait(false);
        if (autoWrite)
        {
            await BlinfoCodec.WriteFileAsync(folder, fresh, ct).ConfigureAwait(false);
        }

        return fresh;
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the build's <c>.blinfo</c> and writes it atomically. The file is read
    /// again first (upstream may have rewritten it), and <paramref name="current"/> is used only
    /// when it is missing or unreadable, so a change to one field never reverts another made elsewhere.
    /// </summary>
    public async Task<BuildInfo> UpdateAsync(BuildInfo current, Func<BuildInfo, BuildInfo> change, CancellationToken ct = default)
    {
        var folder = LibraryLayout.NormaliseRoot(current.Link);
        var basis = current;
        try
        {
            var document = await BlinfoCodec.ReadFileAsync(folder, ct).ConfigureAwait(false);
            if (document.Blinfo.Count > 0)
            {
                var onDisk = BlinfoCodec.ToBuildInfo(document.Blinfo[0], folder, _lts, _time);
                if (onDisk.IsValid)
                {
                    basis = onDisk;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _log?.Debug($"Could not re-read .blinfo of {folder} before updating it: {e.Message}");
        }

        var updated = change(basis) with { Link = folder };
        await BlinfoCodec.WriteFileAsync(folder, updated, ct).ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// Writes <paramref name="fields"/> as the <c>.blinfo</c> of <paramref name="buildFolder"/> (the "Initialise"
    /// action for an unrecognised folder, and the custom build dialog). Returns the build with its link set to
    /// the folder and LTS promotion applied.
    /// </summary>
    public async Task<BuildInfo> WriteAsync(string buildFolder, BuildInfo fields, CancellationToken ct = default)
    {
        var folder = LibraryLayout.NormaliseRoot(buildFolder);
        var build = fields with { Link = folder };
        await BlinfoCodec.WriteFileAsync(folder, build, ct).ConfigureAwait(false);
        return _lts is null ? build : build.PromoteLts(_lts);
    }

    /// <summary>
    /// Upstream's <c>read_blender_version</c>: finds the executable, runs <c>-v</c> unless old info can be reused,
    /// takes the branch from the parent folder, and keeps the user's name, favourite and frozen flags from
    /// <paramref name="old"/>. Does not write anything.
    /// </summary>
    public async Task<BuildInfo> DeriveAsync(string buildFolder, BuildInfo? old, string? archiveName = null, CancellationToken ct = default)
    {
        var folder = LibraryLayout.NormaliseRoot(buildFolder);
        var reuseOldInfo = false;
        string? exe = null;

        if (old is not null && !string.IsNullOrEmpty(old.CustomExecutable))
        {
            var custom = Path.Combine(folder, old.CustomExecutable);
            if (File.Exists(custom))
            {
                exe = custom;
            }
            else
            {
                _log?.Warning($"Custom executable not found: {custom}, falling back to auto-detection for {Path.GetFileName(folder)}");
                reuseOldInfo = true;
            }
        }

        exe ??= DetectExecutable(folder);

        IsoTimestamp commitTime;
        string? buildHash;
        string subversion;
        string customName;
        if (reuseOldInfo && old is not null && File.Exists(exe))
        {
            _log?.Info($"Reusing build info, updated executable path to: {Path.GetRelativePath(folder, exe)}");
            (commitTime, buildHash, subversion, customName) = (old.CommitTime, old.BuildHash, old.Subversion, old.CustomName);
        }
        else
        {
            (commitTime, buildHash, subversion, customName) = await FillBlenderInfoAsync(exe, old, ct).ConfigureAwait(false);
        }

        var parent = Path.GetFileName(Path.GetDirectoryName(folder)) ?? "";
        var branch = BranchFor(parent, archiveName ?? Path.GetFileName(folder), old?.Branch);

        var isFavourite = false;
        var isFrozen = false;
        string? customExe = null;
        if (old is not null)
        {
            customName = old.CustomName;
            isFavourite = old.IsFavorite;
            isFrozen = old.IsFrozen;
            customExe = reuseOldInfo ? ToPosixRelative(folder, exe) : old.CustomExecutable;
        }

        var build = new BuildInfo
        {
            Link = folder,
            Subversion = subversion,
            BuildHash = buildHash,
            CommitTime = commitTime,
            Branch = branch,
            CustomName = customName,
            IsFavorite = isFavourite,
            CustomExecutable = customExe,
            IsFrozen = isFrozen,
        };
        return _lts is null ? build : build.PromoteLts(_lts);
    }

    /// <summary>
    /// The executable upstream would run, in its order: <c>bforartists.exe</c> if it is a file, else
    /// <c>blender.exe</c> (returned even when missing, as upstream's fallback is).
    /// </summary>
    public static string DetectExecutable(string buildFolder)
    {
        var bforartists = Path.Combine(buildFolder, BuildExecutables.Bforartists);
        return File.Exists(bforartists) ? bforartists : Path.Combine(buildFolder, BuildExecutables.Blender);
    }

    /// <summary>
    /// The executable to launch or probe: the custom executable when set and present, else
    /// <see cref="DetectExecutable"/>. Null when that does not exist either.
    /// </summary>
    public static string? FindExecutable(string buildFolder, string? customExecutable)
    {
        if (!string.IsNullOrEmpty(customExecutable))
        {
            var custom = Path.Combine(buildFolder, customExecutable);
            if (File.Exists(custom))
            {
                return custom;
            }
        }

        var detected = DetectExecutable(buildFolder);
        return File.Exists(detected) ? detected : null;
    }

    /// <summary>
    /// Upstream's branch rule: the parent folder name; in <c>custom</c>, the archive or folder name; in
    /// <c>experimental</c>, the text between <c>+</c> and the next <c>.</c> of the name (so a patch build's
    /// <c>…+PR12345.hash</c> gives <c>PR12345</c>), else <paramref name="oldBranch"/> when known, else
    /// <c>experimental</c>.
    /// </summary>
    public static string BranchFor(string parentFolder, string name, string? oldBranch)
    {
        if (parentFolder == "custom")
        {
            return name;
        }

        if (parentFolder == "experimental")
        {
            var match = ExperimentalBranch().Match(name);
            return match.Success ? match.Groups[1].Value : oldBranch ?? parentFolder;
        }

        return parentFolder;
    }

    /// <summary>
    /// Parses <c>-v</c> output as <c>fill_blender_info</c> does: <c>build commit date</c> and <c>build commit
    /// time</c> as local time, <c>build hash</c>, and the subversion after <c>Blender </c> or
    /// <c>Bforartists </c>. Values are right-trimmed.
    /// </summary>
    public VersionOutput ParseVersionOutput(string output)
    {
        IsoTimestamp? commitTime = null;
        var time = CommitTimeLine().Match(output);
        var date = CommitDateLine().Match(output);
        if (time.Success && date.Success)
        {
            var text = $"{date.Groups[1].Value.TrimEnd()} {time.Groups[1].Value.TrimEnd()}";
            commitTime = DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var clock)
                ? IsoTimestamp.AssumeLocal(clock, _time.LocalTimeZone)
                : BlinfoCodec.ParseCommitTime(text, _time);
        }

        var hash = HashLine().Match(output) is { Success: true } h ? h.Groups[1].Value.TrimEnd() : "";
        var sub = ProductLine().Match(output) is { Success: true } s ? s.Groups[1].Value.TrimEnd() : null;
        var first = output.Split('\n', 2)[0].Trim();
        return new VersionOutput(commitTime, hash, sub, first.Length == 0 ? null : first);
    }

    // Upstream's fill_blender_info. Returns (commit time, hash, subversion, custom name).
    private async Task<(IsoTimestamp, string?, string, string)> FillBlenderInfoAsync(string exe, BuildInfo? info, CancellationToken ct)
    {
        if (!File.Exists(exe))
        {
            throw new FileNotFoundException($"Executable not found: {exe}", exe);
        }

        string output;
        try
        {
            output = await _probe.RunAsync(exe, ProbeTimeout, ct).ConfigureAwait(false);
        }
        catch (BlenderProbeException e) when (info is not null)
        {
            _log?.Warning($"Failed to run '{exe} -v': {e.Message}. Using supplied info as fallback.");
            return (info.CommitTime, info.BuildHash ?? "", info.Subversion, info.CustomName);
        }

        var parsed = ParseVersionOutput(output);
        var commitTime = info is not null ? info.CommitTime : parsed.CommitTime ?? IsoTimestamp.Aware(_time.GetLocalNow());

        var customName = "";
        string subversion;
        if (info is not null && info.Subversion.Length > 0)
        {
            subversion = info.Subversion;
        }
        else if (parsed.Subversion is not null)
        {
            subversion = parsed.Subversion;
        }
        else
        {
            // Upstream: first line rsplit(" ", 1) into name and subversion; a line without a space raises.
            var line = parsed.FirstLine ?? throw new FormatException($"{exe} -v printed nothing");
            var space = line.LastIndexOf(' ');
            if (space < 0)
            {
                throw new FormatException($"Cannot read a version from '{line}'");
            }

            customName = line[..space];
            subversion = line[(space + 1)..];
        }

        return (commitTime, parsed.BuildHash, subversion, customName);
    }

    private static string ToPosixRelative(string folder, string path) =>
        Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/');

    [GeneratedRegex(@"\+(.+?)\.")]
    private static partial Regex ExperimentalBranch();

    [GeneratedRegex("build commit time: (.*)")]
    private static partial Regex CommitTimeLine();

    [GeneratedRegex("build commit date: (.*)")]
    private static partial Regex CommitDateLine();

    [GeneratedRegex("build hash: (.*)")]
    private static partial Regex HashLine();

    [GeneratedRegex("(?:Blender|Bforartists) (.*)")]
    private static partial Regex ProductLine();
}
