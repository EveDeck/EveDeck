using EveDeck.Models.Intel;
using EveDeck.Services.Intel;
using System.IO;
using System.Reflection;
using Xunit;

namespace EveDeck.Tests;

// Ported from EveDeck-Intel daemon/src/test/kotlin/dev/eveintel/daemon/IntelParserTest.kt.
// Cases taken from real `alliance.intel` traffic. If the parser regresses, it regresses here first.
public class IntelParserTests
{
    private static readonly Universe SharedUniverse = Universe.LoadFromEmbeddedResource();

    // An example nullsec intel footprint, same as `eveintel.properties`.
    private static readonly IntelParser SharedParser = CreateParser();

    private static IntelParser CreateParser()
    {
        var scope = new HashSet<string> { "Providence", "Catch", "Curse", "Great Wildlands" }
            .Select(name => SharedUniverse.RegionIdsByLowerName.GetValueOrDefault(name.ToLowerInvariant(), -1))
            .Where(id => id != -1)
            .ToHashSet();
        return new IntelParser(SharedUniverse, scope);
    }

    private static IReadOnlyList<IntelToken> Tokens(string line) => SharedParser.Tokenise(line);

    private static IntelMessage Parse(string line) => SharedParser.Parse(
        "alliance.intel",
        new ChatLogFormat.RawMessage(TimestampMillis: 0L, Author: "Tester", Message: line));

    private static IReadOnlyList<IntelFeedEntry> FeedEntries(params ChatLogFormat.RawMessage[] messages)
    {
        using var tailer = new IntelLogTailer(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var feed = new IntelFeedService(SharedUniverse, tailer);
        var onMessageRead = typeof(IntelFeedService).GetMethod("OnMessageRead", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(onMessageRead);

        foreach (var message in messages)
            onMessageRead!.Invoke(feed, ["alliance.intel", "Test Listener", message]);

        return feed.History;
    }

    private static ChatLogFormat.RawMessage RawAt(int second, string message) => new(
        DateTimeOffset.Parse($"2026-09-17T17:32:{second:00}Z").ToUnixTimeMilliseconds(),
        "Test Pilot",
        message);

    [Fact]
    public void SystemPlayerAndShipInParentheses()
    {
        var result = Tokens("FZ-6A5  Varro Kaine (Cyclone)");
        Assert.Equal("FZ-6A5", ((IntelToken.SystemToken)result[0]).Name);
        Assert.Equal("Varro Kaine", ((IntelToken.PlayerToken)result[1]).Text);
        Assert.Equal("Cyclone", ((IntelToken.ShipToken)result[2]).Name);
    }

    [Fact]
    public void LinkMarkerIsStrippedAndRecorded()
    {
        var system = (IntelToken.SystemToken)Tokens("1GH-48*  Varro Kaine")[0];
        Assert.Equal("1GH-48", system.Name);
        Assert.True(system.Linked);
    }

    [Fact]
    public void CountsAttachToTheShipTheyFollow()
    {
        var result = Tokens("5-9L3H*  Halvard  Petra Vance  KrenV  Exequror Navy Issue* 3  Exequror* 1");
        var ships = result.OfType<IntelToken.ShipToken>().ToList();
        Assert.Equal(new[] { "Exequror Navy Issue", "Exequror" }, ships.Select(s => s.Name).ToList());
        Assert.Equal(new int?[] { 3, 1 }, ships.Select(s => s.Count).ToList());
        Assert.Equal(3, result.OfType<IntelToken.PlayerToken>().Count());
    }

    [Fact]
    public void ShipAliasesResolveToCanonicalNames()
    {
        var ships = Tokens("5-9L3H 4= exeq navy").OfType<IntelToken.ShipToken>();
        Assert.Equal("Exequror Navy Issue", Assert.Single(ships).Name);
    }

    [Fact]
    public void TotalAndPlusCountModifiers()
    {
        var total = Tokens("5-9L3H 4= exeq navy").OfType<IntelToken.CountToken>().Single();
        Assert.Equal(4, total.Value);
        Assert.Equal(CountModifier.Total, total.Modifier);

        var plus = Tokens("1L-AED  KrenV +4 nv").OfType<IntelToken.CountToken>().Single();
        Assert.Equal(4, plus.Value);
        Assert.Equal(CountModifier.Plus, plus.Modifier);
    }

    [Fact]
    public void KeywordsAreRecognised()
    {
        Assert.Equal(IntelKeyword.Clear, Tokens("1GH-48* clr").OfType<IntelToken.KeywordToken>().Single().Keyword);
        Assert.Equal(IntelKeyword.Clear, Tokens("CNC-4V clear").OfType<IntelToken.KeywordToken>().Single().Keyword);
        Assert.Equal(
            IntelKeyword.NoVisual,
            Tokens("F4R2-Q  Maren Quinn nv").OfType<IntelToken.KeywordToken>().Single().Keyword);
    }

    [Fact]
    public void AbbreviatedSystemNamesResolveWithinTheChannelScope()
    {
        var system = SharedParser.MatchSystem("m9u");
        Assert.NotNull(system);
        Assert.Equal("M9U-75", system!.Name);
    }

    [Fact]
    public void AlphabeticOnlyShorthandResolvesWithinTheChannelScope()
    {
        // Real traffic shows pilots dropping the digit/hyphen suffix entirely and typing only the
        // leading letters -- a region's systems often share a prefix, so context (the channel's
        // own scope) disambiguates it the same way a human reader would.
        var system = SharedParser.MatchSystem("jplr");
        Assert.NotNull(system);
        Assert.Equal("JPL-RA", system!.Name);
    }

    [Fact]
    public void AlphabeticOnlyShorthandUnderTheLengthFloorDoesNotResolve()
    {
        // "jpl" is a real prefix of JPL-RA too, but three bare letters is exactly the kind of thing
        // that risks colliding with an ordinary word elsewhere in the universe, so it needs the
        // higher floor the digit/hyphen forms don't.
        Assert.Null(SharedParser.MatchSystem("jpl"));
    }

    [Fact]
    public void APilotNameThatCollidesWithShipShorthandStaysOnePlayer()
    {
        // "mega" aliases to Megathron in Vocabulary.SHIP_ALIASES -- a pilot whose name happens to
        // start with it used to get shredded into a phantom ship plus a mangled name fragment.
        var result = Tokens("Mega Trouble*  proteus");
        var players = result.OfType<IntelToken.PlayerToken>().ToList();
        Assert.Equal(new[] { "Mega Trouble*" }, players.Select(p => p.Text).ToList());
        Assert.True(players.Single().Linked);
        var ships = result.OfType<IntelToken.ShipToken>();
        Assert.Equal(new[] { "Proteus" }, ships.Select(s => s.Name).ToList());
    }

    [Fact]
    public void OrdinaryWordsNeverResolveToSystems()
    {
        foreach (var word in new[] { "the", "going", "soon", "nice", "copy" })
        {
            Assert.Null(SharedParser.MatchSystem(word));
        }
    }

    [Fact]
    public void QuestionsAreDistinguishedFromReports()
    {
        var question = Tokens("Petra Vance location? ship?").OfType<IntelToken.QuestionToken>();
        Assert.Equal(new[] { QuestionKind.Location, QuestionKind.ShipType }, question.Select(q => q.Kind).ToList());
    }

    [Fact]
    public void ChatterMentioningAShipIsNotIntel()
    {
        var message = Parse("Nora DarkStar its my anathema");
        Assert.False(message.IsIntel());
    }

    [Fact]
    public void ASystemReportIsIntel()
    {
        Assert.True(Parse("FZ-6A5  Varro Kaine (Cyclone)").IsIntel());
        Assert.True(Parse("1GH-48* clr").IsIntel());
    }

    [Fact]
    public void FeedDedupsNearDuplicatesAcrossAStaggeredMultiClientChain()
    {
        var entries = FeedEntries(
            RawAt(20, "Jita  Test Target"),
            RawAt(22, "Jita  Test Target"),
            RawAt(24, "Jita  Test Target"));

        Assert.Single(entries);
    }

    [Fact]
    public void FeedDedupsNearDuplicatesWithEquivalentLinkMarkersAndWhitespace()
    {
        var entries = FeedEntries(
            RawAt(20, "Jita*  Test Target"),
            RawAt(21, "Jita   Test Target"));

        Assert.Single(entries);
    }

    [Fact]
    public void LocalChannelSystemChangeIsExtracted()
    {
        var raw = ChatLogFormat.ParseMessage(
            "﻿[ 2026.09.17 17:32:24 ] EVE System > Channel changed to Local : 8DL-CP");
        Assert.NotNull(raw);
        Assert.Equal("8DL-CP", ChatLogFormat.ParseLocalSystemChange(raw!));
    }

    [Fact]
    public void TimestampsAreParsedAsEveTime()
    {
        var raw = ChatLogFormat.ParseMessage("﻿[ 2026.09.17 17:32:58 ] Quiet mantis > 1GH-48 clr");
        Assert.NotNull(raw);
        Assert.Equal("Quiet mantis", raw!.Author);
        // EVE time is UTC.
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-17T17:32:58Z").ToUnixTimeMilliseconds(),
            raw.TimestampMillis);
    }

    [Fact]
    public void JumpDistanceUsesTheStargateGraph()
    {
        var from = SharedUniverse.SystemByName("9UY4-H");
        var to = SharedUniverse.SystemByName("KBP7-G");
        Assert.NotNull(from);
        Assert.NotNull(to);
        var jumps = SharedUniverse.Jumps(from!.Id, to!.Id);
        Assert.NotNull(jumps);
        Assert.InRange(jumps!.Value, 1, 20);
        Assert.Equal(0, SharedUniverse.Jumps(from.Id, from.Id));
    }

    [Fact]
    public void AKeywordAloneMakesASystemHostile()
    {
        // No ship, no pilot, no count - but "camped" is the whole point of the report. These
        // reached the feed and never raised an alert.
        Assert.True(Parse("FZ-6A5 camped").IsHostile());
        Assert.True(Parse("FZ-6A5 spiked").IsHostile());
        Assert.True(Parse("FZ-6A5 bubbles").IsHostile());
        Assert.True(Parse("FZ-6A5 neut").IsHostile());
    }

    [Fact]
    public void ClearAlwaysWinsOverAHostileKeyword()
    {
        Assert.False(Parse("FZ-6A5 camp clear").IsHostile());
        Assert.False(Parse("FZ-6A5 clr").IsHostile());
    }

    [Fact]
    public void ObservationAndStandDownKeywordsAreNotHostileOnTheirOwn()
    {
        // How something was seen, or a threat docking up, is not itself a threat.
        Assert.False(Parse("FZ-6A5 dscan").IsHostile());
        Assert.False(Parse("FZ-6A5 docked up").IsHostile());
    }

    [Fact]
    public void ANamedShipIsStillHostileWithoutAnyKeyword()
    {
        Assert.True(Parse("FZ-6A5  Varro Kaine (Cyclone)").IsHostile());
    }

    [Fact]
    public void FactionHullsResolveWithoutTheTrailingIssue()
    {
        Assert.Equal(
            "Caracal Navy Issue",
            Tokens("FZ-6A5 caracal navy").OfType<IntelToken.ShipToken>().Single().Name);
        Assert.Equal(
            "Stabber Fleet Issue",
            Tokens("FZ-6A5 stabber fleet").OfType<IntelToken.ShipToken>().Single().Name);
    }

    [Fact]
    public void TheBareHullStillResolvesToItself()
    {
        Assert.Equal(
            "Caracal",
            Tokens("FZ-6A5 caracal").OfType<IntelToken.ShipToken>().Single().Name);
    }

    [Fact]
    public void PluralHullNamesResolveToTheSingularShip()
    {
        Assert.Equal(
            "Sabre",
            Tokens("FZ-6A5 Sabres").OfType<IntelToken.ShipToken>().Single().Name);
        Assert.Equal(
            "Caracal Navy Issue",
            Tokens("FZ-6A5 Caracal Navy Issues").OfType<IntelToken.ShipToken>().Single().Name);
    }

    [Fact]
    public void ChineseClientHullNamesResolveToTheSdeName()
    {
        Assert.Equal(
            "Loki",
            Tokens("FZ-6A5  洛基级").OfType<IntelToken.ShipToken>().Single().Name);
        Assert.Equal(
            "Caracal Navy Issue",
            Tokens("FZ-6A5  狞獾级海军型").OfType<IntelToken.ShipToken>().Single().Name);
    }

    [Fact]
    public void ShipTypeQuestionAliasesAreRecognised()
    {
        var question = Tokens("Petra Vance shiptypes? types?").OfType<IntelToken.QuestionToken>();
        Assert.Equal(new[] { QuestionKind.ShipType, QuestionKind.ShipType }, question.Select(q => q.Kind).ToList());
    }

    [Fact]
    public void StylisedDigitHandlesAreStillRecognisedAsPlayers()
    {
        var result = Tokens("FZ-6A5  Varro Kaine  d3adsh0t");
        var players = result.OfType<IntelToken.PlayerToken>().ToList();
        Assert.Equal(2, players.Count);
        Assert.Equal("Varro Kaine", players[0].Text);
        Assert.Equal("d3adsh0t", players[1].Text);
    }

    [Fact]
    public void RawUrlMarkupIsFlattenedBeforeTokenising()
    {
        var raw = ChatLogFormat.ParseMessage(
            "﻿[ 2026.09.17 17:32:24 ] Quiet mantis > " +
            "<url=showinfo:5//30004807>FZ-6A5</url>  Varro Kaine");
        Assert.NotNull(raw);
        var system = (IntelToken.SystemToken)Tokens(raw!.Message)[0];
        Assert.Equal("FZ-6A5", system.Name);
        Assert.True(system.Linked);
    }

    [Fact]
    public void APilotLinkedTwiceViaACompoundShipLinkIsNotDuplicated()
    {
        var message = Parse("FZ-6A5  Kelvra  Kelvra (Stork)");
        Assert.Equal(new[] { "Kelvra" }, message.Players);
        Assert.Equal("Stork", message.Ships.Single().Name);
    }
}
