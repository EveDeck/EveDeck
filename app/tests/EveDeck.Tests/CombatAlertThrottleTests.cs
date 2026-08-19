using EveDeck.Models;
using EveDeck.Services;
using Xunit;

namespace EveDeck.Tests;

// Numbers here are taken from a real three-client Abyssal run (2026-08-18): ~20 incoming damage
// lines per second per character, sustained for the whole 15-minute filament timer.
public class CombatAlertThrottleTests
{
    private static readonly DateTime T0 = new(2026, 8, 18, 19, 25, 0, DateTimeKind.Utc);

    [Fact]
    public void RepeatedHitsOnOneSeat_ProduceOneRowPerBundle()
    {
        var throttle = new CombatAlertThrottle { Cooldown = TimeSpan.FromSeconds(15) };

        // 40 hits inside one 2-second bundle window -- what produced "Combat alert (40)" listing the
        // same row forty times.
        for (var i = 0; i < 40; i++)
            throttle.TryQueue("Combat", "#1", "Combat - Seat 1", T0.AddMilliseconds(i * 50));

        var messages = throttle.Flush();
        Assert.Single(messages);
        Assert.Equal("Combat - Seat 1", messages[0]);
    }

    [Fact]
    public void SeatUnderContinuousFire_DoesNotReToastEveryBundle()
    {
        var throttle = new CombatAlertThrottle { Cooldown = TimeSpan.FromSeconds(15) };
        var toasts = 0;

        // One bundle every 2 seconds for 60 seconds of unbroken damage.
        for (var second = 0; second < 60; second += 2)
        {
            throttle.TryQueue("Combat", "#1", "Combat - Seat 1", T0.AddSeconds(second));
            if (throttle.Flush().Count > 0) toasts++;
        }

        Assert.Equal(4, toasts); // one per 15s cooldown, not one per bundle
    }

    [Fact]
    public void SeparateSeats_EachGetTheirOwnRow()
    {
        var throttle = new CombatAlertThrottle { Cooldown = TimeSpan.FromSeconds(15) };

        throttle.TryQueue("Combat", "#1", "Combat - Seat 1", T0);
        throttle.TryQueue("Combat", "#2", "Combat - Seat 2", T0);
        throttle.TryQueue("Combat", "#3", "Combat - Seat 3", T0);

        Assert.Equal(3, throttle.Flush().Count);
    }

    [Fact]
    public void RareEvent_IsNotSwallowedByAnotherRulesCooldown()
    {
        // The whole reason the cooldown key carries the rule name: being scrammed mid-run must still
        // toast even though Combat on the same seat is deep inside its cooldown.
        var throttle = new CombatAlertThrottle { Cooldown = TimeSpan.FromSeconds(15) };
        throttle.TryQueue("Combat", "#1", "Combat - Seat 1", T0);
        throttle.Flush();

        Assert.True(throttle.TryQueue("Warp scramble", "#1", "Warp scramble - Seat 1", T0.AddSeconds(1)));
    }

    [Fact]
    public void CooldownExpires()
    {
        var throttle = new CombatAlertThrottle { Cooldown = TimeSpan.FromSeconds(15) };
        Assert.True(throttle.TryQueue("Combat", "#1", "Combat - Seat 1", T0));
        throttle.Flush(); // the bundle fired; only the cooldown is still holding the row back
        Assert.False(throttle.TryQueue("Combat", "#1", "Combat - Seat 1", T0.AddSeconds(14)));
        Assert.True(throttle.TryQueue("Combat", "#1", "Combat - Seat 1", T0.AddSeconds(15)));
    }

    [Fact]
    public void ZeroCooldown_StillDeduplicatesWithinABundle()
    {
        var throttle = new CombatAlertThrottle { Cooldown = TimeSpan.Zero };
        throttle.TryQueue("Combat", "#1", "Combat - Seat 1", T0);
        throttle.TryQueue("Combat", "#1", "Combat - Seat 1", T0);

        Assert.Single(throttle.Flush());
    }

    [Fact]
    public void CooldownIsClamped()
    {
        var throttle = new CombatAlertThrottle { Cooldown = TimeSpan.FromSeconds(-5) };
        Assert.Equal(TimeSpan.Zero, throttle.Cooldown);

        throttle.Cooldown = TimeSpan.FromHours(1);
        Assert.Equal(TimeSpan.FromSeconds(CombatAlertThrottle.MaxCooldownSeconds), throttle.Cooldown);
    }

    [Fact]
    public void Reset_ClearsPendingAndCooldowns()
    {
        var throttle = new CombatAlertThrottle { Cooldown = TimeSpan.FromSeconds(15) };
        throttle.TryQueue("Combat", "#1", "Combat - Seat 1", T0);
        throttle.Reset();

        Assert.Equal(0, throttle.PendingCount);
        Assert.True(throttle.TryQueue("Combat", "#1", "Combat - Seat 1", T0.AddSeconds(1)));
    }

    [Fact]
    public void FreshAppSettings_HaveAutoArmOnAndACooldown()
    {
        var settings = new AppSettings();
        Assert.True(settings.AbyssModeAutoArm);
        Assert.Equal(15, settings.CombatToastCooldownSeconds);
    }
}
