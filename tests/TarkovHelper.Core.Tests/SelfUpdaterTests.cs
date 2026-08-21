using TarkovHelper.Core;

namespace TarkovHelper.Core.Tests;

public class SelfUpdaterTests
{
    [Fact]
    public void BuildUpdateScript_WaitsOnGivenProcessId()
    {
        var script = SelfUpdater.BuildUpdateScript(12345, "C:\\Temp\\update.zip", "C:\\App", "C:\\App\\TarkovHelper.App.exe");

        Assert.Contains("Get-Process -Id 12345", script);
    }

    [Fact]
    public void BuildUpdateScript_ExtractsZipOverInstallDirWithForce()
    {
        var script = SelfUpdater.BuildUpdateScript(1, "C:\\Temp\\update.zip", "C:\\App", "C:\\App\\TarkovHelper.App.exe");

        Assert.Contains("Expand-Archive -Path 'C:\\Temp\\update.zip' -DestinationPath 'C:\\App' -Force", script);
    }

    [Fact]
    public void BuildUpdateScript_RestartsTheExeAfterExtracting()
    {
        var script = SelfUpdater.BuildUpdateScript(1, "C:\\Temp\\update.zip", "C:\\App", "C:\\App\\TarkovHelper.App.exe");

        var expandIndex = script.IndexOf("Expand-Archive", StringComparison.Ordinal);
        var startIndex = script.IndexOf("Start-Process -FilePath 'C:\\App\\TarkovHelper.App.exe'", StringComparison.Ordinal);

        Assert.True(expandIndex >= 0);
        Assert.True(startIndex > expandIndex);
    }

    [Fact]
    public void BuildUpdateScript_CleansUpTheDownloadedZipAndItself()
    {
        var script = SelfUpdater.BuildUpdateScript(1, "C:\\Temp\\update.zip", "C:\\App", "C:\\App\\TarkovHelper.App.exe");

        Assert.Contains("Remove-Item -Path 'C:\\Temp\\update.zip'", script);
        Assert.Contains("Remove-Item -Path $MyInvocation.MyCommand.Path", script);
    }
}
