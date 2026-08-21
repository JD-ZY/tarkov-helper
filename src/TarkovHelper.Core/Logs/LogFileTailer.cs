using System.Text;

namespace TarkovHelper.Core.Logs;

// Polls a log file for appended bytes while EFT still has it open for
// writing. Not a true tail -f: mirrors TarkovMonitor's approach of opening
// with FileShare.ReadWrite and re-checking length on an interval, since
// FileSystemWatcher's Changed event fires unreliably on files that are
// held open and written continuously by another process.
public sealed class LogFileTailer : IDisposable
{
    private const int MaxBufferLength = 1024;

    private readonly string _filePath;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollTask;
    private long _position;

    public event EventHandler<string>? NewLogData;

    public LogFileTailer(string filePath, TimeSpan pollInterval, bool skipExistingContent)
    {
        _filePath = filePath;
        _pollInterval = pollInterval;
        _position = skipExistingContent && File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
    }

    public void Start()
    {
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[MaxBufferLength];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    using var stream = new FileStream(
                        _filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                    if (stream.Length > _position)
                    {
                        stream.Seek(_position, SeekOrigin.Begin);

                        // Real bug this fixes: a multi-byte UTF-8 character
                        // (EFT's logs can contain non-ASCII text, e.g.
                        // Cyrillic item/location names) can land split
                        // across two separate ReadAsync calls when new log
                        // content happens to straddle a MaxBufferLength
                        // chunk boundary. Decoding each raw byte chunk
                        // independently with Encoding.UTF8.GetString (the
                        // original approach) corrupts/mangles whatever
                        // character was split, and depending on exactly
                        // where the split falls, that corruption can land
                        // inside a line this watcher's regexes need to
                        // match (e.g. the "TRACE-NetworkGameCreate" line
                        // MapLoaded fires from) - explaining reports of the
                        // map intermittently failing to follow a raid
                        // change with no visible error, since a single
                        // mis-decoded poll cycle silently drops that
                        // raid's MapLoaded event entirely. Fixed by
                        // accumulating raw bytes across ALL reads first,
                        // then decoding once at the end - a chunk boundary
                        // can no longer split a multi-byte character
                        // mid-decode.
                        using var byteBuffer = new MemoryStream();
                        int bytesRead;
                        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
                        {
                            byteBuffer.Write(buffer, 0, bytesRead);
                        }

                        _position = stream.Position;
                        var text = Encoding.UTF8.GetString(byteBuffer.ToArray());
                        if (text.Length > 0)
                        {
                            NewLogData?.Invoke(this, text);
                        }
                    }
                }
            }
            catch (IOException)
            {
                // File may be mid-rotation or briefly locked; retry next poll.
            }

            try
            {
                await Task.Delay(_pollInterval, ct);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _pollTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Expected on cancellation.
        }
        _cts.Dispose();
    }
}
