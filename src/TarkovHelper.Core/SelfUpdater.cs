using System.Diagnostics;

namespace TarkovHelper.Core;

// Replaces the running app's own files with a freshly downloaded update and
// relaunches - a running .exe can't overwrite or delete itself on Windows,
// so this hands off to a short-lived PowerShell script that waits for this
// process to fully exit first, then extracts the new zip over the install
// directory and starts the new exe. Reserved as a public class (not
// static/inline in MainWindow) because it does no UI work and both halves
// of the swap (writing the script, launching it) are independently testable
// without needing a real WPF Application.
public class SelfUpdater
{
    // Split out from ApplyUpdateAndRestart so the script content itself
    // (the part with actual logic worth getting wrong) is testable without
    // triggering the process-exit side effect below.
    internal static string BuildUpdateScript(int waitForPid, string zipPath, string installDir, string exePath)
    {
        // -Force on Expand-Archive overwrites existing files in place;
        // everything not present in the new zip (e.g. user data, which
        // lives in %LocalAppData%\TarkovHelper, not the install dir) is
        // untouched. Waits on the PID rather than a fixed sleep, since WPF
        // shutdown time isn't constant across machines.
        return $$"""
            $ErrorActionPreference = 'Stop'
            while (Get-Process -Id {{waitForPid}} -ErrorAction SilentlyContinue) {
                Start-Sleep -Milliseconds 200
            }
            Expand-Archive -Path '{{zipPath}}' -DestinationPath '{{installDir}}' -Force
            Remove-Item -Path '{{zipPath}}' -Force -ErrorAction SilentlyContinue
            Start-Process -FilePath '{{exePath}}'
            Remove-Item -Path $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
            """;
    }

    // Applies the update and exits the CURRENT process - control never
    // returns to the caller on success. installDir is the folder containing
    // the running exe (all of TarkovHelper's files live flat in one
    // directory, no subfolder nesting to preserve); exeName is just the
    // filename (e.g. "TarkovHelper.App.exe") to relaunch once the swap
    // finishes.
    public void ApplyUpdateAndRestart(string zipPath, string installDir, string exeName)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"TarkovHelperUpdate_{Guid.NewGuid()}.ps1");
        var exePath = Path.Combine(installDir, exeName);
        var script = BuildUpdateScript(Environment.ProcessId, zipPath, installDir, exePath);

        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        Environment.Exit(0);
    }
}
