package dev.eveintel.parse

/**
 * The on-disk format of EVE's chat logs.
 *
 * Verified against real logs in `Documents/EVE/logs/Chatlogs`:
 *  - UTF-16LE with a BOM at the start of the file
 *  - a further UTF-8 BOM (`﻿`) prefixed to *every* message line
 *  - CRLF line endings
 *  - a header block delimited by a 63-dash rule
 */
object ChatLogFormat {

    /** `alliance.intel_20260917_173219_2112000001.txt` */
    private val FILENAME = Regex("""^(?<name>.*)_(?<date>\d{8})_(?<time>\d{6})(_(?<characterId>\d+))?\.txt$""")

    /** `[ 2026.09.17 17:32:24 ] Quiet mantis > FZ-6A5  Varro Kaine (Cyclone)` */
    private val MESSAGE = Regex("""^﻿?\[ (?<datetime>[\d.]+ [\d:]+) ] (?<author>[^>]+) > (?<message>.*)$""")

    private val HEADER_FIELD = Regex("""^\s*(?<key>[A-Za-z ]+):\s+(?<value>.*)$""")

    /** `EVE System > Channel changed to Local : 8DL-CP` — how we learn where a character is. */
    private val LOCAL_CHANGE = Regex("""^Channel changed to Local : (?<system>.+)$""")

    const val EVE_SYSTEM_AUTHOR = "EVE System"

    data class FileName(
        val channel: String,
        val date: String,
        val time: String,
        val characterId: Long?,
    )

    data class Header(
        val channelId: String?,
        val channelName: String?,
        val listener: String?,
        val sessionStarted: String?,
    )

    data class RawMessage(
        val timestampMillis: Long,
        val author: String,
        val message: String,
    )

    fun parseFileName(fileName: String): FileName? {
        val match = FILENAME.matchEntire(fileName) ?: return null
        return FileName(
            channel = match.groups["name"]!!.value,
            date = match.groups["date"]!!.value,
            time = match.groups["time"]!!.value,
            characterId = match.groups["characterId"]?.value?.toLongOrNull(),
        )
    }

    /**
     * Reads the `Key: value` block at the top of a log file. Stops at the first message line, so it
     * is safe to hand this the whole file or just the first few hundred characters.
     */
    fun parseHeader(lines: List<String>): Header {
        val fields = mutableMapOf<String, String>()
        for (line in lines) {
            val clean = line.trimEnd('\r', '﻿')
            if (MESSAGE.matches(clean)) break
            val field = HEADER_FIELD.matchEntire(clean) ?: continue
            fields[field.groups["key"]!!.value.trim()] = field.groups["value"]!!.value.trim()
        }
        return Header(
            channelId = fields["Channel ID"],
            channelName = fields["Channel Name"],
            listener = fields["Listener"],
            sessionStarted = fields["Session started"],
        )
    }

    fun parseMessage(line: String): RawMessage? {
        val clean = line.trim('\r', '\n', '﻿', ' ').ifEmpty { return null }
        val match = MESSAGE.matchEntire(clean) ?: return null
        val timestamp = parseEveTimestamp(match.groups["datetime"]!!.value) ?: return null
        return RawMessage(
            timestampMillis = timestamp,
            author = match.groups["author"]!!.value.trim(),
            message = match.groups["message"]!!.value.trim(),
        )
    }

    /** Returns the system name if this is a Local-channel system change announcement. */
    fun parseLocalSystemChange(message: RawMessage): String? {
        if (message.author != EVE_SYSTEM_AUTHOR) return null
        return LOCAL_CHANGE.matchEntire(message.message)?.groups?.get("system")?.value?.trim()
    }

    /** `2026.09.17 17:32:24` in EVE time (UTC) to epoch millis. */
    internal fun parseEveTimestamp(text: String): Long? {
        val (datePart, timePart) = text.split(' ').takeIf { it.size == 2 } ?: return null
        val date = datePart.split('.').mapNotNull { it.toIntOrNull() }.takeIf { it.size == 3 } ?: return null
        val time = timePart.split(':').mapNotNull { it.toIntOrNull() }.takeIf { it.size == 3 } ?: return null
        val (year, month, day) = date
        val (hour, minute, second) = time
        val days = daysFromCivil(year, month, day)
        return ((days * 86_400L) + hour * 3600L + minute * 60L + second) * 1000L
    }

    /** Howard Hinnant's days-from-civil algorithm; keeps date handling in common code. */
    private fun daysFromCivil(year: Int, month: Int, day: Int): Long {
        val y = if (month <= 2) year - 1 else year
        val era = (if (y >= 0) y else y - 399) / 400
        val yoe = y - era * 400
        val mp = (month + 9) % 12
        val doy = (153 * mp + 2) / 5 + day - 1
        val doe = yoe * 365 + yoe / 4 - yoe / 100 + doy
        return era.toLong() * 146_097L + doe.toLong() - 719_468L
    }
}
