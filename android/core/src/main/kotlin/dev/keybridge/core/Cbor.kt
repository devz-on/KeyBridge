package dev.keybridge.core

import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction

/** Bounded CTAP canonical CBOR. No tags, floats, indefinite lengths or duplicate keys. */
object Cbor {
    private const val MAX = 65_536
    fun encode(value: Any?): ByteArray = ByteArrayOutputStream().also { write(it, value, 0) }.toByteArray().also {
        require(it.size <= MAX) { "CBOR is too large" }
    }
    fun decode(bytes: ByteArray): Any? {
        require(bytes.size <= MAX) { "CBOR is too large" }
        val reader = Reader(bytes)
        return reader.read(0).also { require(reader.position == bytes.size) { "Trailing CBOR data" } }
    }
    private fun head(out: ByteArrayOutputStream, major: Int, n: Long) {
        require(n >= 0)
        when {
            n < 24 -> out.write((major shl 5) or n.toInt())
            n <= 255 -> { out.write((major shl 5) or 24); out.write(n.toInt()) }
            n <= 65_535 -> { out.write((major shl 5) or 25); out.write((n shr 8).toInt()); out.write(n.toInt()) }
            n <= 0xffff_ffffL -> { out.write((major shl 5) or 26); for (i in 3 downTo 0) out.write((n shr (i * 8)).toInt()) }
            else -> { out.write((major shl 5) or 27); for (i in 7 downTo 0) out.write((n shr (i * 8)).toInt()) }
        }
    }
    private fun compare(a: ByteArray, b: ByteArray): Int {
        // CTAP canonical order compares major types before encoded lengths (unlike RFC 7049's generic form).
        val type = (a[0].toInt() and 0xe0).compareTo(b[0].toInt() and 0xe0)
        if (type != 0) return type
        if (a.size != b.size) return a.size.compareTo(b.size)
        for (i in a.indices) { val c = (a[i].toInt() and 255).compareTo(b[i].toInt() and 255); if (c != 0) return c }
        return 0
    }
    private fun write(out: ByteArrayOutputStream, value: Any?, depth: Int) {
        require(depth <= 16) { "CBOR nesting limit" }
        when (value) {
            null -> out.write(0xf6)
            is Boolean -> out.write(if (value) 0xf5 else 0xf4)
            is Int -> write(out, value.toLong(), depth)
            is Long -> if (value >= 0) head(out, 0, value) else head(out, 1, -(value + 1))
            is ByteArray -> { head(out, 2, value.size.toLong()); out.write(value) }
            is String -> { val b = value.toByteArray(Charsets.UTF_8); head(out, 3, b.size.toLong()); out.write(b) }
            is List<*> -> { head(out, 4, value.size.toLong()); value.forEach { write(out, it, depth + 1) } }
            is Map<*, *> -> {
                val entries = value.entries.map { encode(it.key) to it.value }.sortedWith { a, b -> compare(a.first, b.first) }
                head(out, 5, entries.size.toLong())
                entries.forEach { (key, v) -> out.write(key); write(out, v, depth + 1) }
            }
            else -> error("Unsupported CBOR value ${value.javaClass.simpleName}")
        }
        require(out.size() <= MAX) { "CBOR is too large" }
    }
    private class Reader(private val data: ByteArray) {
        var position = 0
        private fun byte(): Int { require(position < data.size) { "Truncated CBOR" }; return data[position++].toInt() and 255 }
        private fun number(ai: Int): Long {
            val n = when (ai) {
                in 0..23 -> ai.toLong()
                24 -> byte().toLong().also { require(it >= 24) { "Non-minimal CBOR integer" } }
                25 -> ((byte().toLong() shl 8) or byte().toLong()).also { require(it > 255) { "Non-minimal CBOR integer" } }
                26 -> (0..3).fold(0L) { acc, _ -> (acc shl 8) or byte().toLong() }.also { require(it > 65_535) { "Non-minimal CBOR integer" } }
                27 -> (0..7).fold(0L) { acc, _ -> (acc shl 8) or byte().toLong() }.also { require(it > 0xffff_ffffL) { "Unsupported or non-minimal integer" } }
                else -> error("Indefinite length / reserved CBOR value")
            }
            return n
        }
        fun read(depth: Int): Any? {
            require(depth <= 16) { "CBOR nesting limit" }
            val first = byte(); val major = first shr 5; val ai = first and 31
            if (major == 7) return when (ai) { 20 -> false; 21 -> true; 22 -> null; else -> error("Unsupported CBOR simple value") }
            val n = number(ai)
            fun count(): Int { require(n <= MAX && n <= data.size - position) { "CBOR length limit" }; return n.toInt() }
            return when (major) {
                0 -> if (n <= Int.MAX_VALUE) n.toInt() else n
                1 -> (-1L - n).let { if (it >= Int.MIN_VALUE) it.toInt() else it }
                2 -> { val length = count(); data.copyOfRange(position, position + length).also { position += length } }
                3 -> {
                    val length = count()
                    Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT)
                        .decode(ByteBuffer.wrap(data, position, length)).toString().also { position += length }
                }
                4 -> List(count()) { read(depth + 1) }
                5 -> {
                    val count = count(); require(count <= (data.size - position) / 2) { "Truncated CBOR map" }
                    val map = linkedMapOf<Any, Any?>(); var previous: ByteArray? = null
                    repeat(count) {
                        val start = position; val key = read(depth + 1)
                        require(key is Int || key is Long || key is String) { "CTAP map key type" }
                        val encoded = data.copyOfRange(start, position)
                        previous?.let { require(compare(it, encoded) < 0) { "Unordered or duplicate CBOR map key" } }
                        previous = encoded
                        require(!map.containsKey(key)) { "Duplicate CBOR key" }
                        map[key] = read(depth + 1)
                    }
                    map
                }
                else -> error("Unsupported CBOR type")
            }
        }
    }
}
