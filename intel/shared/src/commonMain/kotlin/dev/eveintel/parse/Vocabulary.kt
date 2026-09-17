package dev.eveintel.parse

import dev.eveintel.model.Keyword
import dev.eveintel.model.QuestionKind

/**
 * Surface forms used in intel channels.
 *
 * Written from observed channel traffic rather than copied from any existing tool. Add to these
 * freely — they are the main thing that wants tuning against your own alliance's habits.
 */
object Vocabulary {

    /** Multi-word forms are matched first, so `no visual` wins over a bare `no`. */
    val KEYWORDS: Map<String, Keyword> = mapOf(
        "clr" to Keyword.CLEAR,
        "clear" to Keyword.CLEAR,
        "cleared" to Keyword.CLEAR,

        "status" to Keyword.STATUS,
        "stat" to Keyword.STATUS,
        "stats" to Keyword.STATUS,

        "nv" to Keyword.NO_VISUAL,
        "no visual" to Keyword.NO_VISUAL,
        "novisual" to Keyword.NO_VISUAL,
        "no vis" to Keyword.NO_VISUAL,

        "spike" to Keyword.SPIKE,
        "spiked" to Keyword.SPIKE,
        "spiking" to Keyword.SPIKE,

        "camp" to Keyword.GATE_CAMP,
        "camped" to Keyword.GATE_CAMP,
        "gatecamp" to Keyword.GATE_CAMP,
        "gate camp" to Keyword.GATE_CAMP,

        "bubble" to Keyword.BUBBLES,
        "bubbles" to Keyword.BUBBLES,
        "bubbled" to Keyword.BUBBLES,
        "drag bubble" to Keyword.BUBBLES,

        "wh" to Keyword.WORMHOLE,
        "wormhole" to Keyword.WORMHOLE,

        "cyno" to Keyword.CYNO,
        "cynos" to Keyword.CYNO,

        "ess" to Keyword.ESS,
        "skyhook" to Keyword.SKYHOOK,

        "probes" to Keyword.COMBAT_PROBES,
        "combat probes" to Keyword.COMBAT_PROBES,

        "docked" to Keyword.DOCKED,
        "dockedup" to Keyword.DOCKED,
        "docked up" to Keyword.DOCKED,

        // Added from a --validate pass over real traffic, where "dscan" was the commonest
        // unrecognised word by a distance.
        "dscan" to Keyword.D_SCAN,
        "d-scan" to Keyword.D_SCAN,
        "dscanned" to Keyword.D_SCAN,
        "on dscan" to Keyword.D_SCAN,

        "neut" to Keyword.NEUTRAL,
        "neuts" to Keyword.NEUTRAL,
        "neutral" to Keyword.NEUTRAL,
        "neutrals" to Keyword.NEUTRAL,

        "red" to Keyword.HOSTILE,
        "reds" to Keyword.HOSTILE,
        "hostile" to Keyword.HOSTILE,
        "hostiles" to Keyword.HOSTILE,
    )

    /**
     * Keywords that on their own mean the system is not safe.
     *
     * A report can name no ship and no pilot and still be the most urgent line in the channel:
     * `<system> camped` or `<system> spiked` is exactly what should raise an alert. Without this
     * those lines were classified as intel, shown in the feed, and then never alerted on.
     *
     * Deliberately excluded: [Keyword.D_SCAN] (how something was seen, not what), [Keyword.DOCKED]
     * (a threat standing down), and [Keyword.NO_VISUAL] / [Keyword.STATUS] (requests and negatives).
     */
    val HOSTILE_KEYWORDS: Set<Keyword> = setOf(
        Keyword.SPIKE,
        Keyword.GATE_CAMP,
        Keyword.BUBBLES,
        Keyword.CYNO,
        Keyword.COMBAT_PROBES,
        Keyword.NEUTRAL,
        Keyword.HOSTILE,
    )

    /**
     * Requests for intel rather than reports of it. Observed heavily in real channel traffic:
     * `Petra Vance location? ship?`, `Rilo McQuade loc?`.
     */
    val QUESTIONS: Map<String, QuestionKind> = mapOf(
        "location?" to QuestionKind.LOCATION,
        "loc?" to QuestionKind.LOCATION,
        "location" to QuestionKind.LOCATION,
        "loc" to QuestionKind.LOCATION,
        "where?" to QuestionKind.LOCATION,

        "ship?" to QuestionKind.SHIP_TYPE,
        "ships?" to QuestionKind.SHIP_TYPE,
        "ship types?" to QuestionKind.SHIP_TYPE,
        "type?" to QuestionKind.SHIP_TYPE,

        "status?" to QuestionKind.STATUS,
        "stat?" to QuestionKind.STATUS,
        "status" to QuestionKind.STATUS,

        "how many?" to QuestionKind.COUNT,
        "number?" to QuestionKind.COUNT,
        "count?" to QuestionKind.COUNT,
    )

    /**
     * Shorthand for ship names. The key is what people type, the value must match an SDE `typeName`
     * exactly (case-insensitively).
     */
    val SHIP_ALIASES: Map<String, String> = mapOf(
        "exeq" to "Exequror",
        "exeq navy" to "Exequror Navy Issue",
        "navy exeq" to "Exequror Navy Issue",
        "eni" to "Exequror Navy Issue",
        "cerb" to "Cerberus",
        "sabre" to "Sabre",
        "hic" to "Broadsword",
        "loki" to "Loki",
        "prot" to "Proteus",
        "legion" to "Legion",
        "tengu" to "Tengu",
        "hurr" to "Hurricane",
        "cane" to "Hurricane",
        "cyclone" to "Cyclone",
        "drek" to "Drekavac",
        "muninn" to "Muninn",
        "eagle" to "Eagle",
        "ishtar" to "Ishtar",
        "jackdaw" to "Jackdaw",
        "hecate" to "Hecate",
        "confessor" to "Confessor",
        "svipul" to "Svipul",
        "ceptor" to "Malediction",
        "dictor" to "Sabre",
        "rokh" to "Rokh",
        "mega" to "Megathron",
        "apoc" to "Apocalypse",
        "nag" to "Naglfar",
        "dread" to "Naglfar",
        "carrier" to "Thanatos",
        "fax" to "Apostle",
        "titan" to "Erebus",
        "super" to "Nyx",
    )

    /**
     * Words that must never resolve to a system name. Prefix matching is already restricted to
     * tokens containing a digit or hyphen, so this only needs to cover the odd collision.
     */
    val SYSTEM_STOPWORDS: Set<String> = setOf(
        "on", "in", "at", "to", "is", "it", "no", "ok", "up", "gf", "o7",
    )
}
