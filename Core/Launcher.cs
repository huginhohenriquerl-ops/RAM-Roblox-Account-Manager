using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RAM.Core;

/// <summary>Where to send an account. A null target means "open the Roblox app on the home screen".</summary>
public sealed record LaunchTarget(long PlaceId, string? JobId = null, string? LinkCode = null, string? ShareCode = null,
    string ShareType = "Server")
{
    public bool NeedsResolve => ShareCode != null && PlaceId == 0;
}

public static class InputParser
{
    static readonly Regex GuidRx = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
    static readonly Regex PlainIdRx = new(@"^\d{1,20}$");
    static readonly Regex GamesUrlRx = new(@"/games/(\d+)", RegexOptions.IgnoreCase);
    static readonly Regex PlaceParamRx = new(@"placeId=(\d+)", RegexOptions.IgnoreCase);
    static readonly Regex LinkCodeRx = new(@"privateServerLinkCode=([\w-]+)", RegexOptions.IgnoreCase);
    static readonly Regex InstanceRx = new(@"(?:gameInstanceId|jobId|gameId)=([0-9a-fA-F-]{36})", RegexOptions.IgnoreCase);
    static readonly Regex ShareRx = new(@"/share\?.*?code=([A-Za-z0-9]+)", RegexOptions.IgnoreCase);
    static readonly Regex ShareTypeRx = new(@"[?&]type=(Server|ExperienceInvite)\b", RegexOptions.IgnoreCase);

    public static LaunchTarget? Parse(string input, string? jobInput = null)
    {
        var s = input.Trim();
        if (s.Length == 0) return null;

        string? job = null;
        if (!string.IsNullOrWhiteSpace(jobInput))
        {
            var m = GuidRx.Match(jobInput);
            if (m.Success) job = m.Value;
        }

        // roblox.com/share?code=…&type=Server (private server) or type=ExperienceInvite (a friend's server)
        var share = ShareRx.Match(s);
        var shareType = ShareTypeRx.Match(s);
        if (share.Success && shareType.Success)
            return new LaunchTarget(0, ShareCode: share.Groups[1].Value,
                ShareType: shareType.Groups[1].Value.Equals("Server", StringComparison.OrdinalIgnoreCase) ? "Server" : "ExperienceInvite");

        long placeId = 0;
        Match match;
        if (PlainIdRx.IsMatch(s)) long.TryParse(s, out placeId);
        else if ((match = GamesUrlRx.Match(s)).Success) long.TryParse(match.Groups[1].Value, out placeId);
        else if ((match = PlaceParamRx.Match(s)).Success) long.TryParse(match.Groups[1].Value, out placeId);
        if (placeId <= 0) return null;

        var link = LinkCodeRx.Match(s);
        var instance = InstanceRx.Match(s);
        return new LaunchTarget(placeId,
            JobId: job ?? (instance.Success ? instance.Groups[1].Value : null),
            LinkCode: link.Success ? link.Groups[1].Value : null);
    }
}

public static class RobloxLauncher
{
    static readonly Random Rng = new();

    static readonly Regex TrackerArgRx = new(@"(?:-b\s+|browsertrackerid[:=])(\d{6,})", RegexOptions.IgnoreCase);

    public static string NewTrackerId() => Rng.Next(100000, 175000).ToString() + Rng.Next(100000, 900000);

    /// <summary>The browser tracker id in a RobloxPlayerBeta command line ("-b 123…" or "browsertrackerid:123…").</summary>
    public static string? TrackerIdFromCommandLine(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return null;
        var m = TrackerArgRx.Match(commandLine);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string BuildUri(string ticket, LaunchTarget? target, string tracker)
    {
        long launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string locale = CultureInfo.CurrentUICulture.Name.Replace('-', '_').ToLowerInvariant();
        if (locale.Length == 0) locale = "en_us";

        var sb = new StringBuilder("roblox-player:1+launchmode:")
            .Append(target == null ? "app" : "play")
            .Append("+gameinfo:").Append(ticket)
            .Append("+launchtime:").Append(launchTime);

        if (target != null)
        {
            const string launcher = "https://assetgame.roblox.com/game/PlaceLauncher.ashx";
            string url;
            if (!string.IsNullOrEmpty(target.LinkCode))
                url = $"{launcher}?request=RequestPrivateGame&browserTrackerId={tracker}&placeId={target.PlaceId}&linkCode={target.LinkCode}&isPlayTogetherGame=false";
            else if (!string.IsNullOrEmpty(target.JobId))
                url = $"{launcher}?request=RequestGameJob&browserTrackerId={tracker}&placeId={target.PlaceId}&gameId={target.JobId}&isPlayTogetherGame=false";
            else
                url = $"{launcher}?request=RequestGame&browserTrackerId={tracker}&placeId={target.PlaceId}&isPlayTogetherGame=false";
            sb.Append("+placelauncherurl:").Append(Uri.EscapeDataString(url));
        }

        sb.Append("+browsertrackerid:").Append(tracker)
          .Append("+robloxLocale:").Append(locale)
          .Append("+gameLocale:").Append(locale)
          .Append("+channel:+LaunchExp:InApp");
        return sb.ToString();
    }

    public static void Start(string uri, bool direct)
    {
        var exe = direct ? FindPlayerExe() : null;
        var info = exe != null
            ? new ProcessStartInfo(exe, "\"" + uri + "\"") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! }
            : new ProcessStartInfo(uri) { UseShellExecute = true };
        Process.Start(info)?.Dispose();
    }

    /// <summary>
    /// Opens a plain Roblox window with no saved account, like MultiBloxy's "Start New Roblox Instance"
    /// (Process.Start("roblox-player:")). The user signs in through Roblox's own screen.
    /// </summary>
    public static void StartBlank(bool direct)
    {
        var exe = direct ? FindPlayerExe() : null;
        var info = exe != null
            ? new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! }
            : new ProcessStartInfo("roblox-player:") { UseShellExecute = true };
        Process.Start(info)?.Dispose();
    }

    /// <summary>Newest RobloxPlayerBeta.exe from the standard install folders.</summary>
    public static string? FindPlayerExe()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox", "Versions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roblox", "Versions"),
        };
        return roots.Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateDirectories(r))
            .Select(d => Path.Combine(d, RobloxProcesses.ProcessName + ".exe"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>The executable registered for roblox-player:// (Roblox itself or a bootstrapper such as Bloxstrap).</summary>
    public static string? GetProtocolHandler()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"roblox-player\shell\open\command");
            var command = key?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(command)) return null;
            var m = Regex.Match(command, "^\"([^\"]+)\"|^(\\S+)");
            return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : command;
        }
        catch
        {
            return null;
        }
    }
}
