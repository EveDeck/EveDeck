using System.IO;

namespace EveDeck.Services.Intel;

/// <summary>One chat channel found in the chatlog folder.</summary>
public sealed record DiscoveredChannel(string Name, int FileCount, long LastActivityMillis, bool Reserved);

/// <summary>
/// Reads the chatlog folder to find which channels this install has actually seen, so both the
/// settings UI and the tablet's channel picker can offer real names rather than asking someone to
/// type one exactly right.
/// </summary>
public static class IntelChannelDiscovery
{
    /// <summary>Never offered as an intel channel: it is how character positions are tracked.</summary>
    private const string LocalChannel = "Local";

    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    public static string DefaultChatlogsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EVE", "logs", "Chatlogs");

    public static IReadOnlyList<DiscoveredChannel> Discover(string? chatlogsFolder = null)
    {
        var folder = chatlogsFolder ?? DefaultChatlogsFolder;
        if (!Directory.Exists(folder)) return [];

        var cutoff = DateTime.Now - Window;
        var counts = new Dictionary<string, (int Files, long LastActivity)>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(folder, "*.txt"))
        {
            try
            {
                var written = File.GetLastWriteTime(path);
                if (written < cutoff) continue;

                var parsed = ChatLogFormat.ParseFileName(Path.GetFileName(path));
                if (parsed is null) continue;

                var millis = new DateTimeOffset(written).ToUnixTimeMilliseconds();
                var existing = counts.GetValueOrDefault(parsed.Channel);
                counts[parsed.Channel] = (existing.Files + 1, Math.Max(existing.LastActivity, millis));
            }
            catch
            {
                // Unreadable entry -- skipped; the next scan retries.
            }
        }

        return counts
            .Select(kv => new DiscoveredChannel(
                kv.Key,
                kv.Value.Files,
                kv.Value.LastActivity,
                kv.Key.Equals(LocalChannel, StringComparison.OrdinalIgnoreCase)))
            // Most recently active first: a channel someone joined once a year ago should not sit
            // above the one they are reporting into right now.
            .OrderByDescending(c => c.LastActivityMillis)
            .ToList();
    }
}
