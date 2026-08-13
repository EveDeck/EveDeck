using System.Net;
using System.Net.Http;
using EveDeck.ViewModels;
using Xunit;

namespace EveDeck.Tests;

// EVE's downtime takes ESI with it, so every seat's podded check fails with a 502/504 and logs a
// warning -- 55 in one five-minute stretch on an ordinary day. These pin the two rules that stop it:
// a scheduled skip over the downtime window, and the "is this an outage or a real error?" test that
// drives the automatic backoff for patch days, which run far longer than any fixed window.
public class SeatHealthDowntimeTests
{
    private static DateTimeOffset Utc(int hour, int minute) =>
        new(2026, 8, 13, hour, minute, 0, TimeSpan.Zero);

    // -- The scheduled downtime window ----------------------------------------------------------

    [Theory]
    [InlineData(10, 59, false)]  // a minute before DT: still checking
    [InlineData(11, 0, true)]    // DT starts: window is inclusive of its start
    [InlineData(11, 5, true)]    // where the real 502 burst happened
    [InlineData(11, 29, true)]   // last minute inside a 30-minute window
    [InlineData(11, 30, false)]  // exclusive of the end
    [InlineData(12, 0, false)]
    public void IsWithinDowntimeWindow_CoversExactlyTheWindow(int hour, int minute, bool expected)
        => Assert.Equal(expected, MainWindowViewModel.IsWithinDowntimeWindow(Utc(hour, minute), "11:00", 30));

    [Fact]
    public void IsWithinDowntimeWindow_IsDisabledByAZeroWindow()
        => Assert.False(MainWindowViewModel.IsWithinDowntimeWindow(Utc(11, 5), "11:00", 0));

    [Fact]
    public void IsWithinDowntimeWindow_HonoursANonDefaultDowntimeTime()
    {
        Assert.True(MainWindowViewModel.IsWithinDowntimeWindow(Utc(3, 10), "03:00", 30));
        Assert.False(MainWindowViewModel.IsWithinDowntimeWindow(Utc(11, 10), "03:00", 30));
    }

    // A window configured long enough to run past midnight UTC must still match after the rollover,
    // where only YESTERDAY's downtime occurrence is the relevant one.
    [Fact]
    public void IsWithinDowntimeWindow_SpansMidnightUtc()
    {
        var justAfterMidnight = new DateTimeOffset(2026, 8, 13, 0, 30, 0, TimeSpan.Zero);
        Assert.True(MainWindowViewModel.IsWithinDowntimeWindow(justAfterMidnight, "23:00", 120));
        Assert.False(MainWindowViewModel.IsWithinDowntimeWindow(justAfterMidnight, "23:00", 60));
    }

    [Fact]
    public void IsWithinDowntimeWindow_FallsBackTo11UtcOnAMalformedSetting()
        => Assert.True(MainWindowViewModel.IsWithinDowntimeWindow(Utc(11, 5), "not a time", 30));

    // -- Outage vs. real error ------------------------------------------------------------------

    // The exact statuses seen in the live log during downtime.
    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public void IsEsiUnavailable_TreatsGatewayStatusesAsAnOutage(HttpStatusCode status)
        => Assert.True(MainWindowViewModel.IsEsiUnavailable(new HttpRequestException("down", null, status)));

    // "An error occurred while sending the request" -- no status at all, i.e. the socket/DNS failed.
    [Fact]
    public void IsEsiUnavailable_TreatsATransportFailureAsAnOutage()
        => Assert.True(MainWindowViewModel.IsEsiUnavailable(new HttpRequestException("socket closed")));

    [Fact]
    public void IsEsiUnavailable_TreatsAClientTimeoutAsAnOutage()
        => Assert.True(MainWindowViewModel.IsEsiUnavailable(new TaskCanceledException()));

    // These are real errors about OUR request and must keep logging loudly rather than being
    // swallowed as "EVE is down" -- 401 in particular is the auth failure that already cost an outage.
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public void IsEsiUnavailable_DoesNotSwallowRealErrors(HttpStatusCode status)
        => Assert.False(MainWindowViewModel.IsEsiUnavailable(new HttpRequestException("nope", null, status)));

    [Fact]
    public void IsEsiUnavailable_DoesNotSwallowUnrelatedExceptions()
        => Assert.False(MainWindowViewModel.IsEsiUnavailable(new InvalidOperationException("bug")));
}
