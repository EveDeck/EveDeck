package dev.eveintel.model

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * Intel keywords recognised in channel traffic. The surface forms that map onto each of these
 * live in [dev.eveintel.parse.Vocabulary].
 */
@Serializable
enum class Keyword {
    CLEAR,
    STATUS,
    NO_VISUAL,
    SPIKE,
    GATE_CAMP,
    BUBBLES,
    WORMHOLE,
    CYNO,
    ESS,
    SKYHOOK,
    COMBAT_PROBES,
    DOCKED,

    /** Seen on directional scan. How the sighting was made, not a threat level in itself. */
    D_SCAN,

    /** A pilot who is not blue. In nullsec that is a threat until proven otherwise. */
    NEUTRAL,

    /** Explicitly red / hostile standing. */
    HOSTILE,
}

/** How a numeric count relates to what was already reported. */
@Serializable
enum class CountModifier {
    /** A bare number, e.g. `Cyclone 3`. */
    EXACT,

    /** `+3` — that many more on top of the last report. */
    PLUS,

    /** `4=` or `=4` — the total is now exactly this. */
    TOTAL,

    /** `x3` or `3x` — a multiplier form, treated as a plain count. */
    TIMES,
}

/** A request for intel rather than a report of it: `location?`, `ship?`, `status`. */
@Serializable
enum class QuestionKind {
    LOCATION,
    SHIP_TYPE,
    STATUS,
    COUNT,
}

@Serializable
sealed interface Token {
    val text: String

    /**
     * A resolved solar system. [text] is what was typed, [name] the canonical SDE name.
     *
     * [inferred] marks a system nobody in this message actually typed: [dev.eveintel.daemon.IntelPipeline]
     * borrowed it from the channel's most recent explicit report because this line was a terse
     * follow-up (`nv`, a bare ship name) that carries no location of its own. The UI must show the
     * difference rather than silently presenting a guess as a stated fact.
     */
    @Serializable
    @SerialName("system")
    data class System(
        override val text: String,
        val systemId: Int,
        val name: String,
        val linked: Boolean = false,
        val inferred: Boolean = false,
    ) : Token

    /** A probable character name. Not validated against ESI — see [IntelParser] for the heuristic. */
    @Serializable
    @SerialName("player")
    data class Player(
        override val text: String,
        val linked: Boolean = false,
    ) : Token

    @Serializable
    @SerialName("ship")
    data class Ship(
        override val text: String,
        val typeId: Int?,
        val name: String,
        val count: Int? = null,
        val linked: Boolean = false,
    ) : Token

    @Serializable
    @SerialName("count")
    data class Count(
        override val text: String,
        val value: Int,
        val modifier: CountModifier,
    ) : Token

    @Serializable
    @SerialName("keyword")
    data class Kw(
        override val text: String,
        val keyword: Keyword,
    ) : Token

    @Serializable
    @SerialName("question")
    data class Question(
        override val text: String,
        val kind: QuestionKind,
    ) : Token

    @Serializable
    @SerialName("url")
    data class Url(override val text: String) : Token

    /** Anything not otherwise classified. */
    @Serializable
    @SerialName("word")
    data class Word(override val text: String) : Token
}

/**
 * One parsed line of intel.
 *
 * [id] is stable across characters: the same message seen in five clients' logs produces the same
 * id, so the tablet shows it once. See [dev.eveintel.parse.messageId].
 */
@Serializable
data class IntelMessage(
    val id: String,
    val channel: String,
    val author: String,
    val timestampMillis: Long,
    val raw: String,
    val tokens: List<Token>,
) {
    val systemIds: List<Int>
        get() = tokens.filterIsInstance<Token.System>().map { it.systemId }.distinct()

    val keywords: List<Keyword>
        get() = tokens.filterIsInstance<Token.Kw>().map { it.keyword }.distinct()

    val players: List<String>
        get() = tokens.filterIsInstance<Token.Player>()
            // The same pilot is often linked twice in one line -- bare, and again with their
            // ship ("Nhar*  Nhar (Stork)*") -- so this normalises off the link marker before
            // deduping, matching systemIds/keywords/questions below.
            .map { it.text.removeSuffix("*") }
            .distinct()

    val ships: List<Token.Ship>
        get() = tokens.filterIsInstance<Token.Ship>()

    val questions: List<QuestionKind>
        get() = tokens.filterIsInstance<Token.Question>().map { it.kind }.distinct()

    /** Total hostile count reported, if the message carried any ship counts. */
    val reportedCount: Int?
        get() {
            val shipCounts = ships.mapNotNull { it.count }
            val bare = tokens.filterIsInstance<Token.Count>().map { it.value }
            val all = shipCounts + bare
            return if (all.isEmpty()) null else all.sum()
        }
}

/** Where one of the watched characters currently is, derived from their Local channel log. */
@Serializable
data class CharacterLocation(
    val characterName: String,
    val systemId: Int,
    val systemName: String,
    val sinceMillis: Long,
)
