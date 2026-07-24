using System;
using System.Collections.Generic;
using EveDeck.Models;
using EveDeck.Services;
using Xunit;

namespace EveDeck.Tests;

public class SkillQueueServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
    private const long CharacterId = 12345L;

    private static EsiSkillQueueEntry Entry(int skillId, int finishedLevel, DateTimeOffset? finishDate)
        => new() { SkillId = skillId, FinishedLevel = finishedLevel, FinishDate = finishDate };

    [Fact]
    public void FirstPoll_ActivelyTraining_NoAlerts_BaselineSet()
    {
        var queue = new List<EsiSkillQueueEntry> { Entry(100, 3, Now + TimeSpan.FromHours(1)) };

        var (alerts, state) = SkillQueueService.Evaluate(
            CharacterId, queue, previousState: null, Now, TimeSpan.FromMinutes(30));

        Assert.Empty(alerts);
        Assert.True(state.HasBaseline);
        Assert.False(state.WasEmpty);
    }

    [Fact]
    public void FirstPoll_EmptyQueue_EmitsQueueEmptyOnly()
    {
        var queue = new List<EsiSkillQueueEntry>();

        var (alerts, state) = SkillQueueService.Evaluate(
            CharacterId, queue, previousState: null, Now, TimeSpan.FromHours(24));

        var alert = Assert.Single(alerts);
        Assert.Equal(SkillAlertKind.QueueEmpty, alert.Kind);
        Assert.True(state.WasEmpty);
    }

    [Fact]
    public void SkillCompleted_AcrossPolls_EmitsCompletedForDroppedSkill()
    {
        var poll1 = new List<EsiSkillQueueEntry>
        {
            Entry(100, 3, Now + TimeSpan.FromHours(1)), // A
            Entry(200, 1, Now + TimeSpan.FromHours(3)), // B
        };
        var (_, baseline) = SkillQueueService.Evaluate(
            CharacterId, poll1, previousState: null, Now, TimeSpan.FromHours(24));

        var now2 = Now + TimeSpan.FromHours(2);
        var poll2 = new List<EsiSkillQueueEntry> { Entry(200, 1, Now + TimeSpan.FromHours(3)) };

        var (alerts, _) = SkillQueueService.Evaluate(
            CharacterId, poll2, baseline, now2, TimeSpan.FromHours(24));

        var completed = Assert.Single(alerts, a => a.Kind == SkillAlertKind.Completed);
        Assert.Equal(100, completed.SkillId);
        Assert.Equal(3, completed.FinishedLevel);
    }

    [Fact]
    public void SkillCompleted_NoDoubleFire_OnRepeatedIdenticalPoll()
    {
        var poll1 = new List<EsiSkillQueueEntry>
        {
            Entry(100, 3, Now + TimeSpan.FromHours(1)),
            Entry(200, 1, Now + TimeSpan.FromHours(3)),
        };
        var (_, baseline) = SkillQueueService.Evaluate(
            CharacterId, poll1, previousState: null, Now, TimeSpan.FromHours(24));

        var now2 = Now + TimeSpan.FromHours(2);
        var poll2 = new List<EsiSkillQueueEntry> { Entry(200, 1, Now + TimeSpan.FromHours(3)) };
        var (_, state2) = SkillQueueService.Evaluate(CharacterId, poll2, baseline, now2, TimeSpan.FromHours(24));

        var now3 = now2; // identical follow-up poll
        var poll3 = new List<EsiSkillQueueEntry> { Entry(200, 1, Now + TimeSpan.FromHours(3)) };
        var (alerts3, _) = SkillQueueService.Evaluate(CharacterId, poll3, state2, now3, TimeSpan.FromHours(24));

        Assert.DoesNotContain(alerts3, a => a.Kind == SkillAlertKind.Completed);
    }

    [Fact]
    public void QueueLow_EdgeTriggered_FiresOnceThenSuppressed()
    {
        var queue = new List<EsiSkillQueueEntry> { Entry(100, 3, Now + TimeSpan.FromHours(10)) };
        var threshold = TimeSpan.FromHours(24);

        var (alertsFirst, stateFirst) = SkillQueueService.Evaluate(
            CharacterId, queue, previousState: new SkillQueueState { HasBaseline = true, WasLow = false }, Now, threshold);

        var low = Assert.Single(alertsFirst, a => a.Kind == SkillAlertKind.QueueLow);
        Assert.NotNull(low);
        Assert.True(stateFirst.WasLow);

        var (alertsSecond, _) = SkillQueueService.Evaluate(
            CharacterId, queue, previousState: new SkillQueueState { HasBaseline = true, WasLow = true }, Now, threshold);

        Assert.DoesNotContain(alertsSecond, a => a.Kind == SkillAlertKind.QueueLow);
    }

    [Fact]
    public void QueueLow_RearmsAfterRefillAboveThreshold()
    {
        var threshold = TimeSpan.FromHours(24);
        var wasLowState = new SkillQueueState { HasBaseline = true, WasLow = true };

        // Refilled above threshold -> no QueueLow, WasLow resets to false.
        var refilled = new List<EsiSkillQueueEntry> { Entry(100, 3, Now + TimeSpan.FromHours(40)) };
        var (alertsRefill, stateRefill) = SkillQueueService.Evaluate(
            CharacterId, refilled, wasLowState, Now, threshold);

        Assert.DoesNotContain(alertsRefill, a => a.Kind == SkillAlertKind.QueueLow);
        Assert.False(stateRefill.WasLow);

        // Later poll drops back under threshold -> QueueLow fires again.
        var laterNow = Now + TimeSpan.FromHours(31); // 40h - 31h = 9h remaining < 24h
        var draining = new List<EsiSkillQueueEntry> { Entry(100, 3, Now + TimeSpan.FromHours(40)) };
        var (alertsDrain, _) = SkillQueueService.Evaluate(
            CharacterId, draining, stateRefill, laterNow, threshold);

        Assert.Contains(alertsDrain, a => a.Kind == SkillAlertKind.QueueLow);
    }

    [Fact]
    public void QueueEmpty_EdgeTriggered_FiresOnceThenSuppressed()
    {
        var nonEmptyState = new SkillQueueState { HasBaseline = true, WasEmpty = false };
        var emptyQueue = new List<EsiSkillQueueEntry>();

        var (alertsFirst, stateFirst) = SkillQueueService.Evaluate(
            CharacterId, emptyQueue, nonEmptyState, Now, TimeSpan.FromHours(24));

        var emptyAlert = Assert.Single(alertsFirst, a => a.Kind == SkillAlertKind.QueueEmpty);
        Assert.NotNull(emptyAlert);
        Assert.True(stateFirst.WasEmpty);

        var (alertsSecond, _) = SkillQueueService.Evaluate(
            CharacterId, emptyQueue, stateFirst, Now, TimeSpan.FromHours(24));

        Assert.DoesNotContain(alertsSecond, a => a.Kind == SkillAlertKind.QueueEmpty);
    }

    [Fact]
    public void PausedQueue_NullFinishDate_TreatedAsEmpty()
    {
        var nonEmptyState = new SkillQueueState { HasBaseline = true, WasEmpty = false };
        var pausedQueue = new List<EsiSkillQueueEntry> { Entry(100, 3, finishDate: null) };

        var (alerts, state) = SkillQueueService.Evaluate(
            CharacterId, pausedQueue, nonEmptyState, Now, TimeSpan.FromHours(24));

        var emptyAlert = Assert.Single(alerts, a => a.Kind == SkillAlertKind.QueueEmpty);
        Assert.NotNull(emptyAlert);
        Assert.True(state.WasEmpty);
    }

    [Fact]
    public void FirstPoll_NoBaseline_NeverEmitsCompleted()
    {
        var queue = new List<EsiSkillQueueEntry> { Entry(100, 3, Now + TimeSpan.FromHours(1)) };

        var (alerts, _) = SkillQueueService.Evaluate(
            CharacterId, queue, previousState: null, Now, TimeSpan.FromHours(24));

        Assert.DoesNotContain(alerts, a => a.Kind == SkillAlertKind.Completed);
    }
}
