using EveDeck.Models.Intel;

namespace EveDeck.Services.Intel;

public sealed record IntelFeedFilterSettings(
    int MaxJumps,
    bool HideUnknownRange,
    bool HideClearStatus,
    int MaxAgeMinutes,
    IReadOnlySet<string> HiddenChannels);

public static class IntelFeedFilter
{
    public static bool ShouldShow(IntelFeedEntry entry, IntelFeedFilterSettings settings, DateTimeOffset now)
    {
        if (settings.HiddenChannels.Contains(entry.Message.Channel)) return false;

        if (settings.HideUnknownRange && !entry.RangeKnown) return false;

        if (settings.MaxJumps > 0 && entry.JumpsAway is { } jumps && jumps > settings.MaxJumps)
            return false;

        if (settings.HideClearStatus && IsNonHostileClearOrStatus(entry)) return false;

        if (settings.MaxAgeMinutes > 0)
        {
            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(entry.Message.TimestampMillis);
            if (now - timestamp > TimeSpan.FromMinutes(settings.MaxAgeMinutes)) return false;
        }

        return true;
    }

    private static bool IsNonHostileClearOrStatus(IntelFeedEntry entry)
    {
        if (entry.IsHostile) return false;

        var message = entry.Message;
        return message.Keywords.Contains(IntelKeyword.Clear)
            || message.Keywords.Contains(IntelKeyword.Status)
            || message.Questions.Contains(QuestionKind.Status);
    }
}

public static class IntelAlertMute
{
    public static bool IsMuted(DateTimeOffset? mutedUntil, DateTimeOffset now) =>
        mutedUntil is DateTimeOffset until && (until == DateTimeOffset.MaxValue || until > now);
}
