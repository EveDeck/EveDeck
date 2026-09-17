package dev.eveintel.daemon.esi

import kotlinx.serialization.Serializable

/**
 * Shared ESI response shapes. Kept in one file because several services resolve names, and two
 * private copies of the same class in one package is a redeclaration error.
 */
@Serializable
internal data class UniverseName(
    val id: Long,
    val name: String,
    val category: String,
)

@Serializable
internal data class AllianceDetail(
    val ticker: String? = null,
    val name: String? = null,
)
