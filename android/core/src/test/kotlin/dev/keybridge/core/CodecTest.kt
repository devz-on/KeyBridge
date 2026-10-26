package dev.keybridge.core

import org.junit.Assert.*
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream

class CodecTest {
    @Test fun canonicalEncodingMatchesCtapVectors() {
        assertArrayEquals(byteArrayOf(0xa3.toByte(), 1, 2, 3, 0x26, 0x20, 1), Cbor.encode(mapOf(-1 to 1, 3 to -7, 1 to 2)))
        assertArrayEquals(byteArrayOf(0xa2.toByte(), 0x62, 0x72, 0x6b, 0xf5.toByte(), 0x62, 0x75, 0x76, 0xf5.toByte()), Cbor.encode(mapOf("uv" to true, "rk" to true)))
    }
    @Test fun canonicalMapOrderingComparesMajorTypeBeforeLength() {
        val expected = byteArrayOf(0xa2.toByte(), 0x18, 0x18, 1, 0x20, 2)
        assertArrayEquals(expected, Cbor.encode(mapOf(-1 to 2, 24 to 1)))
        assertArrayEquals(expected, Cbor.encode(Cbor.decode(expected)))
        val wrongOrder = byteArrayOf(0xa2.toByte(), 0x20, 2, 0x18, 0x18, 1)
        try { Cbor.decode(wrongOrder); fail("Accepted generic length-first order instead of CTAP canonical order") }
        catch (_: IllegalArgumentException) { }
    }
    @Test fun rejectsAmbiguousMalformedOrUnboundedCbor() {
        val invalid = listOf("a201010102", "a202000100", "1817", "9fff", "d90001", "62c0af", "a100", "1bffffffffffffffff", "00ff")
        invalid.forEach { hex -> try { Cbor.decode(hex.chunked(2).map { it.toInt(16).toByte() }.toByteArray()); fail("Accepted $hex") } catch (_: Exception) { } }
        try { Cbor.decode(ByteArray(65537)); fail() } catch (_: IllegalArgumentException) { }
    }
    @Test fun nestedValuesAndCounterRangesRoundTrip() {
        val encoded = Cbor.encode(mapOf(1 to listOf(-1, 24, 255, 256, 65536, 0xffffffffL), "x" to "hello"))
        assertArrayEquals(encoded, Cbor.encode(Cbor.decode(encoded)))
    }
    @Test fun frameBytesAreNetworkOrderAndRoundTrip() {
        val out = ByteArrayOutputStream(); Frame(3, 0x12345678, byteArrayOf(4)).writeTo(out)
        assertArrayEquals(byteArrayOf(0x4b, 0x42, 0x52, 0x47, 1, 3, 0, 0, 0x12, 0x34, 0x56, 0x78, 0, 0, 0, 1, 4), out.toByteArray())
        val frame = Frame.readFrom(ByteArrayInputStream(out.toByteArray())); assertEquals(3, frame.kind); assertEquals(0x12345678, frame.requestId); assertArrayEquals(byteArrayOf(4), frame.payload)
    }
    @Test fun frameRejectsBadMagicReservedFlagsLengthAndTruncation() {
        val out = ByteArrayOutputStream(); Frame(1, 1, byteArrayOf()).writeTo(out); val bytes = out.toByteArray()
        listOf(bytes.copyOfRange(0, 15), bytes.copyOf().also { it[0] = 0 }, bytes.copyOf().also { it[6] = 1 }, bytes.copyOf().also { it[12] = 1 }).forEach {
            try { Frame.readFrom(ByteArrayInputStream(it)); fail("Accepted bad frame") } catch (_: Exception) { }
        }
    }
    @Test fun frameReservesZeroRequestIdInBothDirections() {
        try { Frame(1, 0, byteArrayOf()).writeTo(ByteArrayOutputStream()); fail("Accepted ID zero") }
        catch (_: IllegalArgumentException) { }
        val bytes = ByteArrayOutputStream().also { Frame(1, 1, byteArrayOf()).writeTo(it) }.toByteArray()
        bytes[11] = 0
        try { Frame.readFrom(ByteArrayInputStream(bytes)); fail("Accepted ID zero") }
        catch (_: IllegalArgumentException) { }
    }
    @Test fun pairingCodeBindsOrderedBothCertificates() {
        val code = Frame.pairingCode(byteArrayOf(1), byteArrayOf(2)); assertEquals(64, code.length)
        val fullHash = java.security.MessageDigest.getInstance("SHA-256").digest("KeyBridge pairing v1".toByteArray(Charsets.UTF_8) + byteArrayOf(1, 2))
        assertEquals(fullHash.joinToString("") { "%02X".format(it.toInt() and 255) }, code)
        assertNotEquals(code, Frame.pairingCode(byteArrayOf(2), byteArrayOf(1)))
        assertNotEquals(code, Frame.pairingCode(byteArrayOf(1), byteArrayOf(3)))
    }
}
