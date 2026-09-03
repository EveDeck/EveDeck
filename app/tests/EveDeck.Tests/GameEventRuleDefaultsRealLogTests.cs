using EveDeck.Models;
using Xunit;

namespace EveDeck.Tests;

// Companion to GameEventAlertTests. Every Pattern in GameEventRule.Defaults() is a case-insensitive
// SUBSTRING that GameLogWatcherService.ReadNewLines tests against each new gamelog line
// (line.IndexOf(pattern, OrdinalIgnoreCase) >= 0, on lines whose trimmed start is '['). A pattern
// that matches zero real lines is indistinguishable from a working one at runtime -- see
// app/CLAUDE.md "Count any new log pattern against the real archive before shipping it" and the
// "validate log regexes against real logs" feedback. This suite pins each default against the line
// shape EVE actually writes, sanitised of character / corp / system names per the OPSEC rule
// (public NPC ship types and ore names are kept -- the matcher never keys off them, but they make
// the shape recognisable).
//
// Match counts below are from a 1,575,515-line personal gamelog archive scanned 2026-09-02.
public class GameEventRuleDefaultsRealLogTests
{
    private const string Stamp = "[ 2026.08.16 12:00:00 ] ";

    // The exact test GameLogWatcherService.ReadNewLines applies to each new line.
    private static bool Matches(GameEventRule rule, string line) =>
        line.TrimStart().StartsWith('[') &&
        line.IndexOf(rule.Pattern, StringComparison.OrdinalIgnoreCase) >= 0;

    private static GameEventRule Rule(string name) =>
        GameEventRule.Defaults().First(r => r.Name == name);

    // -- Line shapes -------------------------------------------------------------------------------
    // Verbatim from the archive except where noted; "Anchoring Damavik" is a public NPC rat.

    // "(combat)"  1,311,585 matches.
    private const string CombatHit = Stamp +
        "(combat) <color=0xffcc0000><b>247</b> <color=0x77ffffff><font size=10>from</font> " +
        "<color=0xffffffff><b>Anchoring Damavik</b><font size=10><color=0x77ffffff> - Hits";

    // "warp scramble attempt"  16,134 matches.
    private const string WarpScramble = Stamp +
        "(combat) <color=0xffffffff><b>Warp scramble attempt</b> <color=0x77ffffff>" +
        "<font size=10>from</font> <color=0xffffffff><b>Anchoring Damavik</b>";

    // "warp disruption attempt"  7,692 matches. Same shape as the scramble line with the noun
    // swapped -- EVE writes "Warp disruption attempt", NOT the "warp disruptor attempt" this
    // default carried until 2026-09 (that string: zero matches in the whole archive).
    private const string WarpDisruption = Stamp +
        "(combat) <color=0xffffffff><b>Warp disruption attempt</b> <color=0x77ffffff>" +
        "<font size=10>from</font> <color=0xffffffff><b>Anchoring Damavik</b>";

    // "join their fleet"  947 matches. Verbatim shape; inviter name + character id sanitised.
    private const string FleetInvite = Stamp +
        "(question) <a href=\"showinfo:1379//90000001\">Fleet Boss</a> wants you to join " +
        "their fleet, do you accept?<br><br>NOTE: Attacking members of the fleet is an act of war";

    // "is inviting you to a conversation"  14 matches, logged under the (None) tag. Shape
    // reconstructed from those matches; the pattern was "wants to talk" until 2026-09 (zero matches).
    private const string ConversationInvite = Stamp +
        "(None) Hostile Pilot is inviting you to a conversation.";

    // "depleted"  1,562 matches -- ALL of them this residue-cycle line, never a rock being mined
    // out (EVE does not log that). Verbatim shape from the real log archive.
    private const string MiningResidue = Stamp +
        "(mining) <color=0x77ffffff>Additional <font size=12><color=#ffff454b>73" +
        "<color=0x77ffffff><font size=10> units depleted from asteroid as residue";

    // -- Each default fires on its real line -----------------------------------------------------

    [Fact]
    public void Combat_MatchesRealCombatLine() => Assert.True(Matches(Rule("Combat"), CombatHit));

    [Fact]
    public void WarpScramble_MatchesRealLine() => Assert.True(Matches(Rule("Warp scramble"), WarpScramble));

    [Fact]
    public void WarpDisruption_MatchesRealLine() => Assert.True(Matches(Rule("Warp disruption"), WarpDisruption));

    [Fact]
    public void FleetInvite_MatchesRealLine() => Assert.True(Matches(Rule("Fleet invite"), FleetInvite));

    [Fact]
    public void Conversation_MatchesRealLine() => Assert.True(Matches(Rule("Conversation"), ConversationInvite));

    // The "Asteroid depleted" default (Pattern "depleted") is kept by user preference even though its
    // only real-world match is the residue-cycle spam line -- this pins that understanding so nobody
    // "fixes" the pattern expecting a rock-mined-out event that EVE never logs.
    [Fact]
    public void AsteroidDepleted_OnlyEverMatchesTheResidueCycleLine() =>
        Assert.True(Matches(Rule("Asteroid depleted"), MiningResidue));

    // -- The wordings that shipped broken must not come back -----------------------------------

    [Fact]
    public void WarpDisruption_DoesNotUseTheZeroMatchWording() =>
        Assert.NotEqual("warp disruptor attempt", Rule("Warp disruption").Pattern);

    [Fact]
    public void Conversation_DoesNotUseTheZeroMatchWording() =>
        Assert.NotEqual("wants to talk", Rule("Conversation").Pattern);

    // Scram and disrupt log near-identical lines and the two rules exist to cover both wordings, so
    // each line must trip exactly its own rule -- not the other.
    [Fact]
    public void ScrambleAndDisruptionRulesDoNotCrossMatch()
    {
        Assert.True(Matches(Rule("Warp scramble"), WarpScramble));
        Assert.False(Matches(Rule("Warp scramble"), WarpDisruption));
        Assert.True(Matches(Rule("Warp disruption"), WarpDisruption));
        Assert.False(Matches(Rule("Warp disruption"), WarpScramble));
    }

    // The scramble / disruption lines are "(combat)" lines, so the generic Combat rule catches them
    // too -- the dedicated rules refine (own sound past Abyss Mode), they do not replace.
    [Fact]
    public void CombatAlsoCoversTackleLines()
    {
        Assert.True(Matches(Rule("Combat"), WarpScramble));
        Assert.True(Matches(Rule("Combat"), WarpDisruption));
    }

    // Non-'[' lines (gamelog header block, blank lines) never reach rule matching.
    [Fact]
    public void HeaderLinesAreNotMatched()
    {
        Assert.False(Matches(Rule("Combat"), "  Listener: Some Pilot"));
        Assert.False(Matches(Rule("Combat"), "------------------------------------------------------------"));
    }
}
