using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TarkovHelper.Core.Logs;

public static class LogPathResolver
{
    private static readonly Regex SessionFolderTimestamp = new(
        @"log_(?<timestamp>\d+\.\d+\.\d+_\d+-\d+-\d+)",
        RegexOptions.Compiled);

    private const string TimestampFormat = "yyyy.MM.dd_H-mm-ss";

    // Registry keys verified against TarkovMonitor's GameWatcher.GetDefaultLogsFolder().
    public static string? GetDefaultLogsFolder()
    {
        var installLocation =
            ReadInstallLocation(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkov") ??
            ReadInstallLocation(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 3932890");

        if (installLocation is null)
        {
            return null;
        }

        var logsPath = Path.Combine(installLocation, "Logs");
        if (Directory.Exists(logsPath))
        {
            return logsPath;
        }

        var buildLogsPath = Path.Combine(installLocation, "build", "Logs");
        return Directory.Exists(buildLogsPath) ? buildLogsPath : logsPath;
    }

    private static string? ReadInstallLocation(string keyPath)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        return key?.GetValue("InstallLocation") as string;
    }

    public static string? GetLatestSessionFolder(string logsRootFolder)
    {
        var all = GetAllSessionFoldersChronological(logsRootFolder);
        return all.Count > 0 ? all[^1].Folder : null;
    }

    // EFT does not delete old per-session log folders on its own, so a
    // player's full task-status history (Started/Failed/Finished events)
    // going back to whenever they last cleared their Logs folder is
    // recoverable by scanning every log_* folder, not just the latest -
    // this is what TarkovMonitor's "read past logs" feature does, and is
    // the only way to bootstrap quest state for a player who accepted
    // quests before this app was ever running (there is no separate
    // client-side save file - EFT is server-authoritative; verified no
    // such file exists via PCGamingWiki and TarkovMonitor's own source).
    public static List<(string Folder, DateTime Timestamp)> GetAllSessionFoldersChronological(string logsRootFolder)
    {
        var result = new List<(string, DateTime)>();

        if (!Directory.Exists(logsRootFolder))
        {
            return result;
        }

        foreach (var folder in Directory.EnumerateDirectories(logsRootFolder))
        {
            var name = Path.GetFileName(folder);
            var match = SessionFolderTimestamp.Match(name);
            if (!match.Success)
            {
                continue;
            }

            if (!DateTime.TryParseExact(
                    match.Groups["timestamp"].Value,
                    TimestampFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var timestamp))
            {
                continue;
            }

            result.Add((folder, timestamp));
        }

        result.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        return result;
    }
}
