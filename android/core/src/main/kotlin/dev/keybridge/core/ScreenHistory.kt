package dev.keybridge.core

/** Chronological screen history, also used for tab visits. Stored as strings in saved instance state. */
object ScreenHistory {
    fun navigate(history: List<String>, destination: String): List<String> {
        if (history.lastOrNull() == destination) return history
        return (history + destination).takeLast(32)
    }

    fun back(history: List<String>): List<String> =
        if (history.size > 1) history.dropLast(1) else listOf("Home")
}
