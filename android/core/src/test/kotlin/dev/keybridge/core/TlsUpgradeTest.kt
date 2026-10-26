package dev.keybridge.core

import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.EOFException

class TlsUpgradeTest {
    private val marker = "KBTLS1\r\n".toByteArray(Charsets.US_ASCII)

    @Test fun exactMarkerIsEchoedBeforeTlsAndDoesNotConsumeItsRecordBytes() {
        val tlsRecord = byteArrayOf(0x16, 3, 3, 0, 1, 1)
        val input = ByteArrayInputStream(marker + tlsRecord)
        val output = ByteArrayOutputStream()
        TlsUpgrade.accept(input, output)
        assertArrayEquals(marker, output.toByteArray())
        assertArrayEquals(tlsRecord, input.readBytes())
        assertEquals("keybridge/1", TlsUpgrade.APPLICATION_PROTOCOL)
    }
    @Test fun invalidOrPlaintextApplicationPrefacesAreRejectedWithoutReply() {
        for (bytes in listOf("KBTLS0\r\n".toByteArray(), "GET /xxx".toByteArray(), "KBRG0000".toByteArray(), ByteArray(8))) {
            val output = ByteArrayOutputStream()
            try { TlsUpgrade.accept(ByteArrayInputStream(bytes), output); fail("Accepted an invalid upgrade") }
            catch (_: IllegalArgumentException) { }
            assertEquals(0, output.size())
        }
    }
    @Test fun truncatedUpgradeIsRejectedWithoutReply() {
        val output = ByteArrayOutputStream()
        try { TlsUpgrade.accept(ByteArrayInputStream(marker.copyOf(7)), output); fail("Accepted a truncated marker") }
        catch (_: EOFException) { }
        assertEquals(0, output.size())
    }
}
