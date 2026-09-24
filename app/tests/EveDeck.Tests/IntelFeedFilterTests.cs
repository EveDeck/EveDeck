using EveDeck.Models.Intel;
using EveDeck.Services.Intel;
using Xunit;

namespace EveDeck.Tests;

public class IntelFeedFilterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-23T12:00:00Z");

    private static IntelFeedFilterSettings Settings(
        int maxJumps = 0,
        bool hideUnknownRange = false,
        bool hideClearStatus = false,
        int maxAgeMinutes = 0,
        params string[] hiddenChannels) =>
        new(
            maxJumps,
            hideUnknownRange,
            hideClearStatus,
            maxAgeMinutes,
            hiddenChannels.ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static IntelFeedEntry Entry(
        IReadOnlyList<IntelToken> tokens,
        int? jumps = 1,
        string channel = "test.intel",
        DateTimeOffset? timestamp = null)
    {
        var message = new IntelMessage(
            "id",
            channel,
            "Test Pilot",
            (timestamp ?? Now).ToUnixTimeMilliseconds(),
            "Jita Test Pilot",
            tokens);
        return new IntelFeedEntry(message, message.IsHostile(), jumps, jumps is null ? null : "Test Pilot");
    }

    private static IntelToken.SystemToken Jita() => new()
    {
        Text = "Jita",
        SystemId = 1,
        Name = "Jita",
    };

    private static IntelToken.PlayerToken Pilot() => new() { Text = "Test Pilot" };

    [Fact]
    public void AllFiltersOff_AllowsEverything()
    {
        var entry = Entry(
            [Jita(), new IntelToken.KeywordToken { Text = "clear", Keyword = IntelKeyword.Clear }],
            jumps: null,
            timestamp: Now.AddHours(-1));

        Assert.True(IntelFeedFilter.ShouldShow(entry, Settings(), Now));
    }

    [Fact]
    public void MaxRangeOff_AllowsFarReports()
    {
        var entry = Entry([Jita(), Pilot()], jumps: 99);

        Assert.True(IntelFeedFilter.ShouldShow(entry, Settings(maxJumps: 0), Now));
    }

    [Fact]
    public void MaxRange_HidesBeyondLimitButKeepsBoundary()
    {
        Assert.True(IntelFeedFilter.ShouldShow(Entry([Jita(), Pilot()], jumps: 3), Settings(maxJumps: 3), Now));
        Assert.False(IntelFeedFilter.ShouldShow(Entry([Jita(), Pilot()], jumps: 4), Settings(maxJumps: 3), Now));
    }

    [Fact]
    public void UnknownRange_OnlyHiddenWhenTheUnknownFilterIsOn()
    {
        var entry = Entry([Jita(), Pilot()], jumps: null);

        Assert.True(IntelFeedFilter.ShouldShow(entry, Settings(maxJumps: 3, hideUnknownRange: false), Now));
        Assert.False(IntelFeedFilter.ShouldShow(entry, Settings(maxJumps: 3, hideUnknownRange: true), Now));
    }

    [Fact]
    public void HideClearStatus_HidesNonHostileClearAndStatus()
    {
        var clear = Entry([Jita(), new IntelToken.KeywordToken { Text = "clear", Keyword = IntelKeyword.Clear }]);
        var status = Entry([Jita(), new IntelToken.QuestionToken { Text = "status?", Kind = QuestionKind.Status }]);

        Assert.False(IntelFeedFilter.ShouldShow(clear, Settings(hideClearStatus: true), Now));
        Assert.False(IntelFeedFilter.ShouldShow(status, Settings(hideClearStatus: true), Now));
    }

    [Fact]
    public void HideClearStatusOff_KeepsClearAndStatus()
    {
        var clear = Entry([Jita(), new IntelToken.KeywordToken { Text = "clear", Keyword = IntelKeyword.Clear }]);
        var status = Entry([Jita(), new IntelToken.QuestionToken { Text = "status?", Kind = QuestionKind.Status }]);

        Assert.True(IntelFeedFilter.ShouldShow(clear, Settings(hideClearStatus: false), Now));
        Assert.True(IntelFeedFilter.ShouldShow(status, Settings(hideClearStatus: false), Now));
    }

    [Fact]
    public void HostileQuestion_StaysVisibleWhenClearStatusFilterIsOn()
    {
        var entry = Entry([Jita(), Pilot(), new IntelToken.QuestionToken { Text = "status?", Kind = QuestionKind.Status }]);

        Assert.True(IntelFeedFilter.ShouldShow(entry, Settings(hideClearStatus: true), Now));
    }

    [Fact]
    public void MaxAge_HidesOnlyRowsOlderThanTheBoundary()
    {
        Assert.True(IntelFeedFilter.ShouldShow(
            Entry([Jita(), Pilot()], timestamp: Now.AddMinutes(-5)),
            Settings(maxAgeMinutes: 5),
            Now));
        Assert.False(IntelFeedFilter.ShouldShow(
            Entry([Jita(), Pilot()], timestamp: Now.AddSeconds(-301)),
            Settings(maxAgeMinutes: 5),
            Now));
    }

    [Fact]
    public void HiddenChannel_HidesThatChannelOnly()
    {
        Assert.False(IntelFeedFilter.ShouldShow(
            Entry([Jita(), Pilot()], channel: "test.intel"),
            Settings(0, false, false, 0, "TEST.INTEL"),
            Now));
        Assert.True(IntelFeedFilter.ShouldShow(
            Entry([Jita(), Pilot()], channel: "other.intel"),
            Settings(0, false, false, 0, "TEST.INTEL"),
            Now));
    }

    [Fact]
    public void MuteState_ExpiresTimedMutesButKeepsIndefiniteMute()
    {
        Assert.False(IntelAlertMute.IsMuted(null, Now));
        Assert.False(IntelAlertMute.IsMuted(Now, Now));
        Assert.False(IntelAlertMute.IsMuted(Now.AddSeconds(-1), Now));
        Assert.True(IntelAlertMute.IsMuted(Now.AddSeconds(1), Now));
        Assert.True(IntelAlertMute.IsMuted(DateTimeOffset.MaxValue, Now));
    }
}
