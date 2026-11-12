using System.Globalization;
using BlenderLauncher.App.Localisation;
using BlenderLauncher.Core.Builds;

namespace BlenderLauncher.App.ViewModels;

/// <summary>
/// How the main window writes dates, sizes and channel names (shared by every region so they agree). Numbers and
/// month names follow the current culture; the patterns follow the design ("Oct 14, 2026", "412 MB", "Today, 09:41").
/// </summary>
public static class DisplayFormat
{
    private const double Megabyte = 1_000_000d;
    private const double Gigabyte = 1_000_000_000d;

    /// <summary>The design's build date: "Oct 14, 2026".</summary>
    public static string Date(DateTimeOffset? value) =>
        value is { } v ? v.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture) : Loc.Tr("ui.units.unknown");

    /// <summary>
    /// Archive and folder sizes in decimal units, as the design: "412 MB" below 1000 MB, otherwise "1.2 GB"; a dash
    /// when unknown.
    /// </summary>
    public static string Size(long? bytes)
    {
        if (bytes is not { } b || b < 0)
        {
            return Loc.Tr("ui.units.unknown");
        }

        if (b < 999.5 * Megabyte)
        {
            var mb = Math.Round(b / Megabyte);
            return Loc.Tr("ui.units.mb", ("value", mb.ToString("0", CultureInfo.CurrentCulture)));
        }

        return Loc.Tr("ui.units.gb", ("value", (b / Gigabyte).ToString("0.0", CultureInfo.CurrentCulture)));
    }

    /// <summary>A total in gigabytes with two decimals ("2.85 GB"), as in the installed list's subtitle.</summary>
    public static string SizeGigabytes(long bytes, int decimals = 2) =>
        Loc.Tr("ui.units.gb", ("value", (bytes / Gigabyte).ToString("F" + decimals, CultureInfo.CurrentCulture)));

    /// <summary>
    /// "Never", "Just now" (under a minute), "Today, 09:41", "Yesterday, 18:12", otherwise the date.
    /// </summary>
    public static string LastUsed(DateTimeOffset? value, DateTimeOffset? now = null)
    {
        if (value is not { } v)
        {
            return Loc.Tr("ui.time.never");
        }

        var local = v.ToLocalTime();
        var current = (now ?? DateTimeOffset.Now).ToLocalTime();
        if ((current - local).Duration() < TimeSpan.FromMinutes(1))
        {
            return Loc.Tr("ui.time.just_now");
        }

        var time = local.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (local.Date == current.Date)
        {
            return Loc.Tr("ui.time.today", ("time", time));
        }

        return local.Date == current.Date.AddDays(-1) ? Loc.Tr("ui.time.yesterday", ("time", time)) : Date(local);
    }

    /// <summary>"4 min ago", "2 hours ago", or the date; for "checked … ago".</summary>
    public static string Ago(DateTimeOffset value, DateTimeOffset? now = null)
    {
        var elapsed = (now ?? DateTimeOffset.Now) - value;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return Loc.Tr("ui.time.just_now");
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return Loc.Plural("ui.time.minutes_ago", (long)elapsed.TotalMinutes);
        }

        return elapsed < TimeSpan.FromDays(1) ? Loc.Plural("ui.time.hours_ago", (long)elapsed.TotalHours) : Date(value);
    }

    /// <summary>The first seven characters of a commit hash, as in the lists.</summary>
    public static string ShortHash(string? hash) => hash is null ? "" : hash.Length > 7 ? hash[..7] : hash;

    /// <summary>The channel's display name ("Stable", "LTS", "UPBGE Weekly"…).</summary>
    public static string ChannelName(Channel channel) => Loc.Tr("ui.channel." + ChannelKey(channel));

    /// <summary>
    /// The channel's key: the sidebar and settings key (<c>SidebarView</c>), the <c>ui.channel.*</c> key and the
    /// pill class. Stable and LTS are separate keys though they share a library folder.
    /// </summary>
    public static string ChannelKey(Channel channel) => channel switch
    {
        Channel.Stable => "stable",
        Channel.Lts => "lts",
        Channel.Daily => "daily",
        Channel.Experimental => "experimental",
        Channel.Patch => "patch",
        Channel.Bforartists => "bforartists",
        Channel.UpbgeStable => "upbge-stable",
        Channel.UpbgeWeekly => "upbge-weekly",
        _ => "custom",
    };

    /// <summary>The inverse of <see cref="ChannelKey"/>; null for keys that are not channels (<c>all</c>, <c>favorites</c>).</summary>
    public static Channel? ChannelFromKey(string key) => key switch
    {
        "stable" => Channel.Stable,
        "lts" => Channel.Lts,
        "daily" => Channel.Daily,
        "experimental" => Channel.Experimental,
        "patch" => Channel.Patch,
        "bforartists" => Channel.Bforartists,
        "upbge-stable" => Channel.UpbgeStable,
        "upbge-weekly" => Channel.UpbgeWeekly,
        "custom" => Channel.Custom,
        _ => null,
    };
}
