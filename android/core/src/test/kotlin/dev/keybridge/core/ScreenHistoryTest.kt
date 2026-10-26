package dev.keybridge.core

import org.junit.Assert.*
import org.junit.Test

class ScreenHistoryTest {
    @Test fun backRetracesTabVisitsIncludingRevisitedTabs() {
        var history = listOf("Home")
        listOf("Vault", "Activity", "Vault").forEach { history = ScreenHistory.navigate(history, it) }
        history = ScreenHistory.back(history); assertEquals("Activity", history.last())
        history = ScreenHistory.back(history); assertEquals("Vault", history.last())
        history = ScreenHistory.back(history); assertEquals(listOf("Home"), history)
    }
    @Test fun nestedPagesAndReselectionPreserveTheirActualParent() {
        var history = ScreenHistory.navigate(listOf("Home"), "Settings")
        history = ScreenHistory.navigate(history, "Settings")
        assertEquals(2, history.size)
        history = ScreenHistory.navigate(history, "Computers")
        assertEquals("Settings", ScreenHistory.back(history).last())
        assertEquals("Home", ScreenHistory.back(listOf("Security")).last())
    }
    @Test fun historyIsBoundedAndInputIsNotMutated() {
        val original = listOf("Home")
        var history = original
        repeat(100) { history = ScreenHistory.navigate(history, "Page$it") }
        assertEquals(listOf("Home"), original)
        assertEquals(32, history.size)
        assertEquals("Page98", ScreenHistory.back(history).last())
    }
}
