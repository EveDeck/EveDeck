using System.Globalization;
using System.Text.RegularExpressions;

namespace EveDeck.Services.Intel;

// The on-disk format of EVE's chat logs, verified against real logs rather than documentation:
// UTF-16LE with a BOM at the start of the file, a further UTF-8 BOM prefixed to every message
// line, CRLF endings, and a Key: value header block terminated by a 63-dash rule.
// Ported from EveDeck-Intel shared/parse/ChatLogFormat.kt.
public static partial class ChatLogFormat
{
    public const string EveSystemAuthor = "EVE System";

    private const string MotdMarker = "Channel MOTD:";

    [GeneratedRegex(@"^(?<name>.*)_(?<date>\d{8})_(?<time>\d{6})(_(?<characterId>\d+))?\.txt$")]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(@"^﻿?\[ (?<datetime>[\d.]+ [\d:]+) \] (?<author>[^>]+) > (?<message>.*)$")]
    private static partial Regex MessagePattern();

    [GeneratedRegex(@"^\s*(?<key>[A-Za-z ]+):\s+(?<value>.*)$")]
    private static partial Regex HeaderFieldPattern();

    [GeneratedRegex(@"^Channel changed to Local : (?<system>.+)$")]
    private static partial Regex LocalChangePattern();

    [GeneratedRegex(@"<url=[^>]*>(.*?)</url>")]
    private static partial Regex LinkTagPattern();

    public sealed record ChatLogFileName(string Channel, string Date, string Time, long? CharacterId);

    public sealed record Header(string? ChannelId, string? ChannelName, string? Listener, string? SessionStarted);

    public sealed record RawMessage(long TimestampMillis, string Author, string Message);

    public static ChatLogFileName? ParseFileName(string fileName)
    {
        var match = FileNamePattern().Match(fileName);
        if (!match.Success) return null;

        var idGroup = match.Groups["characterId"];
        long? characterId = idGroup.Success && long.TryParse(idGroup.Value, out var parsed) ? parsed : null;

        return new ChatLogFileName(
            match.Groups["name"].Value,
            match.Groups["date"].Value,
            match.Groups["time"].Value,
            characterId);
    }

    // Stops at the first message line, so it is safe to hand this the whole file.
    public static Header ParseHeader(IEnumerable<string> lines)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var clean = line.TrimEnd('\r', '﻿');
            if (MessagePattern().IsMatch(clean)) break;
            var field = HeaderFieldPattern().Match(clean);
            if (!field.Success) continue;
            fields[field.Groups["key"].Value.Trim()] = field.Groups["value"].Value.Trim();
        }

        return new Header(
            fields.GetValueOrDefault("Channel ID"),
            fields.GetValueOrDefault("Channel Name"),
            fields.GetValueOrDefault("Listener"),
            fields.GetValueOrDefault("Session started"));
    }

    public static RawMessage? ParseMessage(string line)
    {
        var clean = line.Trim('\r', '\n', '﻿', ' ');
        if (clean.Length == 0) return null;

        var match = MessagePattern().Match(clean);
        if (!match.Success) return null;

        var timestamp = ParseEveTimestamp(match.Groups["datetime"].Value);
        if (timestamp is null) return null;

        return new RawMessage(
            timestamp.Value,
            match.Groups["author"].Value.Trim(),
            StripLinks(match.Groups["message"].Value.Trim()));
    }

    // Most linked items are already flattened to "text*" by the client before they reach the log.
    // At least one shape is not -- a dragged/targeted ship reference logs its markup verbatim -- so
    // collapse any that slipped through to the same convention IntelParser already understands,
    // rather than leaving raw markup for it to choke on.
    private static string StripLinks(string message) =>
        LinkTagPattern().Replace(message, m => $"{m.Groups[1].Value}*");

    public static string? ParseLocalSystemChange(RawMessage message)
    {
        if (!string.Equals(message.Author, EveSystemAuthor, StringComparison.Ordinal)) return null;
        var match = LocalChangePattern().Match(message.Message);
        return match.Success ? match.Groups["system"].Value.Trim() : null;
    }

    // Matched on the LAST occurrence of the label rather than the first: some corps' own MOTD text
    // apparently began life as a copy of the system announcement and still literally starts with
    // "Channel MOTD:" itself, which would otherwise make the boundary ambiguous.
    public static string? ParseChannelMotd(RawMessage message)
    {
        if (!string.Equals(message.Author, EveSystemAuthor, StringComparison.Ordinal)) return null;
        var index = message.Message.LastIndexOf(MotdMarker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var text = message.Message[(index + MotdMarker.Length)..].Trim();
        return text.Length == 0 ? null : text;
    }

    // "2026.09.17 17:32:24" in EVE time, which is UTC.
    internal static long? ParseEveTimestamp(string text)
    {
        if (!DateTime.TryParseExact(
                text,
                "yyyy.MM.dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return null;
        }

        return new DateTimeOffset(parsed, TimeSpan.Zero).ToUnixTimeMilliseconds();
    }
}
