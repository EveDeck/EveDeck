package dev.eveintel.daemon

import dev.eveintel.model.CountModifier
import dev.eveintel.model.Keyword
import dev.eveintel.model.QuestionKind
import dev.eveintel.model.Token
import dev.eveintel.parse.ChatLogFormat
import dev.eveintel.parse.IntelParser
import dev.eveintel.parse.isHostile
import dev.eveintel.parse.isIntel
import dev.eveintel.universe.Universe
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Cases taken from real `alliance.intel` traffic. If the parser regresses, it regresses here
 * first.
 */
class IntelParserTest {

    private val universe: Universe by lazy { loadUniverse() }

    /** An example nullsec intel footprint, same as `eveintel.properties`. */
    private val parser: IntelParser by lazy {
        val scope = setOf("Providence", "Catch", "Curse", "Great Wildlands")
            .mapNotNull { universe.regionIdsByLowerName[it.lowercase()] }
            .toSet()
        IntelParser(universe, scope)
    }

    private fun tokens(line: String) = parser.tokenise(line)

    @Test
    fun `system player and ship in parentheses`() {
        val result = tokens("FZ-6A5  Varro Kaine (Cyclone)")
        assertEquals("FZ-6A5", (result[0] as Token.System).name)
        assertEquals("Varro Kaine", (result[1] as Token.Player).text)
        assertEquals("Cyclone", (result[2] as Token.Ship).name)
    }

    @Test
    fun `link marker is stripped and recorded`() {
        val system = tokens("1GH-48*  Varro Kaine")[0] as Token.System
        assertEquals("1GH-48", system.name)
        assertTrue(system.linked)
    }

    @Test
    fun `counts attach to the ship they follow`() {
        val result = tokens("5-9L3H*  Halvard  Petra Vance  KrenV  Exequror Navy Issue* 3  Exequror* 1")
        val ships = result.filterIsInstance<Token.Ship>()
        assertEquals(listOf("Exequror Navy Issue", "Exequror"), ships.map { it.name })
        assertEquals(listOf(3, 1), ships.map { it.count })
        assertEquals(3, result.filterIsInstance<Token.Player>().size)
    }

    @Test
    fun `ship aliases resolve to canonical names`() {
        val ships = tokens("5-9L3H 4= exeq navy").filterIsInstance<Token.Ship>()
        assertEquals("Exequror Navy Issue", ships.single().name)
    }

    @Test
    fun `total and plus count modifiers`() {
        val total = tokens("5-9L3H 4= exeq navy").filterIsInstance<Token.Count>().single()
        assertEquals(4, total.value)
        assertEquals(CountModifier.TOTAL, total.modifier)

        val plus = tokens("1L-AED  KrenV +4 nv").filterIsInstance<Token.Count>().single()
        assertEquals(4, plus.value)
        assertEquals(CountModifier.PLUS, plus.modifier)
    }

    @Test
    fun `keywords are recognised`() {
        assertEquals(Keyword.CLEAR, tokens("1GH-48* clr").filterIsInstance<Token.Kw>().single().keyword)
        assertEquals(Keyword.CLEAR, tokens("CNC-4V clear").filterIsInstance<Token.Kw>().single().keyword)
        assertEquals(
            Keyword.NO_VISUAL,
            tokens("F4R2-Q  Maren Quinn nv").filterIsInstance<Token.Kw>().single().keyword,
        )
    }

    @Test
    fun `abbreviated system names resolve within the channel scope`() {
        val system = assertNotNull(parser.matchSystem("m9u"))
        assertEquals("M9U-75", system.name)
    }

    @Test
    fun `ordinary words never resolve to systems`() {
        listOf("the", "going", "soon", "nice", "copy").forEach {
            assertEquals(null, parser.matchSystem(it), "'$it' should not be a system")
        }
    }

    @Test
    fun `questions are distinguished from reports`() {
        val question = tokens("Petra Vance location? ship?").filterIsInstance<Token.Question>()
        assertEquals(listOf(QuestionKind.LOCATION, QuestionKind.SHIP_TYPE), question.map { it.kind })
    }

    @Test
    fun `chatter mentioning a ship is not intel`() {
        val message = parse("Nora DarkStar its my anathema")
        assertFalse(message.isIntel(), "chatter with no system should not be intel")
    }

    @Test
    fun `a system report is intel`() {
        assertTrue(parse("FZ-6A5  Varro Kaine (Cyclone)").isIntel())
        assertTrue(parse("1GH-48* clr").isIntel())
    }

    @Test
    fun `local channel system change is extracted`() {
        val raw = ChatLogFormat.parseMessage(
            "﻿[ 2026.09.17 17:32:24 ] EVE System > Channel changed to Local : 8DL-CP",
        )
        assertNotNull(raw)
        assertEquals("8DL-CP", ChatLogFormat.parseLocalSystemChange(raw))
    }

    @Test
    fun `timestamps are parsed as EVE time`() {
        val raw = assertNotNull(
            ChatLogFormat.parseMessage("﻿[ 2026.09.17 17:32:58 ] Quiet mantis > 1GH-48 clr"),
        )
        assertEquals("Quiet mantis", raw.author)
        // EVE time is UTC. Checks the common-code date arithmetic against the JDK.
        assertEquals(
            java.time.Instant.parse("2026-09-17T17:32:58Z").toEpochMilli(),
            raw.timestampMillis,
        )
    }

    @Test
    fun `jump distance uses the stargate graph`() {
        val from = assertNotNull(universe.systemByName("9UY4-H"))
        val to = assertNotNull(universe.systemByName("KBP7-G"))
        val jumps = assertNotNull(universe.jumps(from.id, to.id))
        assertTrue(jumps in 1..20, "expected a sane jump count, got $jumps")
        assertEquals(0, universe.jumps(from.id, from.id))
    }

    @Test
    fun `a keyword alone makes a system hostile`() {
        // No ship, no pilot, no count - but "camped" is the whole point of the report. These
        // reached the feed and never raised an alert.
        assertTrue(parse("FZ-6A5 camped").isHostile())
        assertTrue(parse("FZ-6A5 spiked").isHostile())
        assertTrue(parse("FZ-6A5 bubbles").isHostile())
        assertTrue(parse("FZ-6A5 neut").isHostile())
    }

    @Test
    fun `clear always wins over a hostile keyword`() {
        assertFalse(parse("FZ-6A5 camp clear").isHostile())
        assertFalse(parse("FZ-6A5 clr").isHostile())
    }

    @Test
    fun `observation and stand-down keywords are not hostile on their own`() {
        // How something was seen, or a threat docking up, is not itself a threat.
        assertFalse(parse("FZ-6A5 dscan").isHostile())
        assertFalse(parse("FZ-6A5 docked up").isHostile())
    }

    @Test
    fun `a named ship is still hostile without any keyword`() {
        assertTrue(parse("FZ-6A5  Varro Kaine (Cyclone)").isHostile())
    }

    @Test
    fun `faction hulls resolve without the trailing Issue`() {
        assertEquals(
            "Caracal Navy Issue",
            tokens("FZ-6A5 caracal navy").filterIsInstance<Token.Ship>().single().name,
        )
        assertEquals(
            "Stabber Fleet Issue",
            tokens("FZ-6A5 stabber fleet").filterIsInstance<Token.Ship>().single().name,
        )
    }

    @Test
    fun `the bare hull still resolves to itself`() {
        assertEquals(
            "Caracal",
            tokens("FZ-6A5 caracal").filterIsInstance<Token.Ship>().single().name,
        )
    }

    private fun parse(line: String) = parser.parse(
        "alliance.intel",
        ChatLogFormat.RawMessage(timestampMillis = 0L, author = "Tester", message = line),
    )
}
