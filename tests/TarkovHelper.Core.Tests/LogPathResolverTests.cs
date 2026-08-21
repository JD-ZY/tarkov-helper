using TarkovHelper.Core.Logs;

namespace TarkovHelper.Core.Tests;

public class LogPathResolverTests : IDisposable
{
    private readonly string _tempDir;

    public LogPathResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TarkovHelperLogPathTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string CreateSessionFolder(string timestamp)
    {
        var path = Path.Combine(_tempDir, $"log_{timestamp}.something");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void GetAllSessionFoldersChronological_SortsOldestFirst()
    {
        CreateSessionFolder("2026.08.05_10-00-00");
        CreateSessionFolder("2026.08.01_10-00-00");
        CreateSessionFolder("2026.08.03_10-00-00");

        var result = LogPathResolver.GetAllSessionFoldersChronological(_tempDir);

        Assert.Equal(3, result.Count);
        Assert.Equal(new DateTime(2026, 8, 1, 10, 0, 0), result[0].Timestamp);
        Assert.Equal(new DateTime(2026, 8, 3, 10, 0, 0), result[1].Timestamp);
        Assert.Equal(new DateTime(2026, 8, 5, 10, 0, 0), result[2].Timestamp);
    }

    [Fact]
    public void GetAllSessionFoldersChronological_IgnoresNonMatchingFolders()
    {
        CreateSessionFolder("2026.08.01_10-00-00");
        Directory.CreateDirectory(Path.Combine(_tempDir, "not_a_session_folder"));

        var result = LogPathResolver.GetAllSessionFoldersChronological(_tempDir);

        Assert.Single(result);
    }

    [Fact]
    public void GetAllSessionFoldersChronological_MissingRoot_ReturnsEmpty()
    {
        var result = LogPathResolver.GetAllSessionFoldersChronological(Path.Combine(_tempDir, "does-not-exist"));

        Assert.Empty(result);
    }

    [Fact]
    public void GetLatestSessionFolder_ReturnsMostRecentByTimestamp()
    {
        var older = CreateSessionFolder("2026.08.01_10-00-00");
        var newer = CreateSessionFolder("2026.08.05_10-00-00");

        var result = LogPathResolver.GetLatestSessionFolder(_tempDir);

        Assert.Equal(newer, result);
    }
}
