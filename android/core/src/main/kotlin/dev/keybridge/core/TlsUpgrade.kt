package dev.keybridge.core

import java.io.DataInputStream
import java.io.InputStream
import java.io.OutputStream

/** A fixed service discriminator followed unconditionally by authenticated TLS, never plaintext CTAP. */
object TlsUpgrade {
    const val APPLICATION_PROTOCOL = "keybridge/1"
    private val marker = "KBTLS1\r\n".toByteArray(Charsets.US_ASCII)

    fun accept(input: InputStream, output: OutputStream) {
        val request = ByteArray(marker.size)
        // Do not buffer: the next byte belongs to the TLS record stream on the same socket.
        DataInputStream(input).readFully(request)
        require(request.contentEquals(marker)) { "KeyBridge TLS upgrade is required" }
        output.write(marker)
        output.flush()
    }
}
