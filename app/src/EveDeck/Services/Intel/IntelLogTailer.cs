using System.IO;
using System.Text;

namespace EveDeck.Services.Intel;

/// <summary>
/// Sub-second tail over EVE's intel-channel chat logs.
///
/// Deliberately polls rather than using a FileSystemWatcher. EVE holds its log files open, and
/// Windows does not refresh directory metadata for open handles, so both watcher events and
/// <see cref="FileInfo.Length"/> go stale; the length is therefore read from an open
/// <see cref="FileStream"/> instead. <see cref="ChatLogWatcherService"/> keeps its watcher because a
/// 45-second worst case is tolerable for keyword alerts and Local tracking — intel is not, which is
/// why this exists alongside it rather than replacing it.
///
/// Reading logs EVE itself writes to disk is plain file I/O and never sends input into a client —
/// see COMPLIANCE.md.
/// </summary>
public sealed class IntelLogTailer : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Enumerating the Chatlogs directory is O(thousands) — EVE opens a new Local file on every
    /// jump — so the file set is refreshed on a much slower cadence than the polling loop.
    /// </summary>
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan FileAgeCutoff = TimeSpan.FromHours(24);

    private readonly string _chatlogsFolder;
    private readonly Dictionary<string, long> _offsetByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _listenerByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cancellation;
    private List<string> _activeFiles = [];
    private DateTime _lastRescanUtc = DateTime.MinValue;

    /// <summary>A parsed line, with the channel it arrived on and the character whose log carried it.</summary>
    public event Action<string, string?, ChatLogFormat.RawMessage>? MessageRead;

    public event Action<string>? ErrorOccurred;

    public IntelLogTailer(string? chatlogsFolder = null)
    {
        _chatlogsFolder = chatlogsFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EVE", "logs", "Chatlogs");
    }

    public void SetChannels(IEnumerable<string> channels)
    {
        lock (_gate)
        {
            _channels.Clear();
            foreach (var channel in channels)
            {
                if (!string.IsNullOrWhiteSpace(channel)) _channels.Add(channel.Trim());
            }

            _lastRescanUtc = DateTime.MinValue;
        }
    }

    public void Start()
    {
        if (_cancellation is not null) return;
        if (!Directory.Exists(_chatlogsFolder)) return;

        _cancellation = new CancellationTokenSource();
        _ = Task.Run(() => PollLoopAsync(_cancellation.Token));
    }

    public void Stop()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        // Intel history is not replayed on startup: a channel's backlog is stale by definition and
        // would arrive as a burst of alerts for fights that already ended.
        var primed = false;

        while (!token.IsCancellationRequested)
        {
            try
            {
                RescanIfDue();

                foreach (var path in Snapshot())
                {
                    if (token.IsCancellationRequested) break;
                    ReadNewLines(path, suppressEmit: !primed);
                }

                primed = true;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Intel tailer poll failed: {ex}");
            }

            try
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private List<string> Snapshot()
    {
        lock (_gate) return [.._activeFiles];
    }

    private void RescanIfDue()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _lastRescanUtc < RescanInterval) return;
            _lastRescanUtc = DateTime.UtcNow;

            if (_channels.Count == 0)
            {
                _activeFiles = [];
                return;
            }

            // Newest file per channel per character: EVE opens a fresh file each session, and only
            // the latest is still being appended to.
            var newestByKey = new Dictionary<string, (string Path, string Stamp)>(StringComparer.OrdinalIgnoreCase);
            var cutoff = DateTime.Now - FileAgeCutoff;

            foreach (var path in Directory.EnumerateFiles(_chatlogsFolder, "*.txt"))
            {
                try
                {
                    if (File.GetLastWriteTime(path) < cutoff) continue;

                    var parsed = ChatLogFormat.ParseFileName(Path.GetFileName(path));
                    if (parsed is null || !_channels.Contains(parsed.Channel)) continue;

                    var key = $"{parsed.Channel}\u0000{parsed.CharacterId}";
                    var stamp = $"{parsed.Date}{parsed.Time}";
                    if (!newestByKey.TryGetValue(key, out var existing) ||
                        string.CompareOrdinal(stamp, existing.Stamp) > 0)
                    {
                        newestByKey[key] = (path, stamp);
                    }
                }
                catch
                {
                    // Unreadable entry — skipped; the next rescan retries.
                }
            }

            _activeFiles = newestByKey.Values.Select(v => v.Path).ToList();

            // Drop state for rotated-away files so neither map grows for the lifetime of the app.
            var live = new HashSet<string>(_activeFiles, StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _offsetByPath.Keys.Where(k => !live.Contains(k)).ToList())
                _offsetByPath.Remove(stale);
            foreach (var stale in _listenerByPath.Keys.Where(k => !live.Contains(k)).ToList())
                _listenerByPath.Remove(stale);
        }
    }

    private void ReadNewLines(string path, bool suppressEmit)
    {
        long offset;
        lock (_gate) _offsetByPath.TryGetValue(path, out offset);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // The length must come from the open handle, not the directory entry.
        var length = stream.Length;
        if (length < offset) offset = 0; // truncated or recreated — start over

        if (suppressEmit)
        {
            lock (_gate) _offsetByPath[path] = AlignToEven(length);
            return;
        }

        if (length <= offset)
        {
            lock (_gate) _offsetByPath[path] = AlignToEven(offset);
            return;
        }

        offset = AlignToEven(offset);
        stream.Seek(offset, SeekOrigin.Begin);

        var byteCount = (int)AlignToEven(length - offset);
        if (byteCount <= 0) return;

        var buffer = new byte[byteCount];
        var read = stream.Read(buffer, 0, byteCount);
        if (read <= 0) return;

        // UTF-16 is two bytes per code unit; a read ending mid-character corrupts the next one.
        read = (int)AlignToEven(read);
        var text = Encoding.Unicode.GetString(buffer, 0, read);

        lock (_gate) _offsetByPath[path] = offset + read;

        var lines = text.Split('\n');
        var listener = ResolveListener(path, lines);

        var parsedFileName = ChatLogFormat.ParseFileName(Path.GetFileName(path));
        var channel = parsedFileName?.Channel ?? Path.GetFileNameWithoutExtension(path);

        foreach (var line in lines)
        {
            var message = ChatLogFormat.ParseMessage(line);
            if (message is null) continue;
            MessageRead?.Invoke(channel, listener, message);
        }
    }

    private string? ResolveListener(string path, string[] lines)
    {
        lock (_gate)
        {
            if (_listenerByPath.TryGetValue(path, out var known)) return known;
        }

        var listener = ChatLogFormat.ParseHeader(lines).Listener;
        if (string.IsNullOrEmpty(listener)) return null;

        lock (_gate) _listenerByPath[path] = listener;
        return listener;
    }

    private static long AlignToEven(long value) => value - (value % 2);

    public void Dispose() => Stop();
}
