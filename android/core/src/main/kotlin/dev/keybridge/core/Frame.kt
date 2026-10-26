package dev.keybridge.core

import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.security.MessageDigest

data class Frame(val kind: Int, val requestId: Int, val payload: ByteArray) {
    fun writeTo(stream: OutputStream) {
        require(kind in 1..255 && requestId != 0 && payload.size <= MAX_PAYLOAD)
        DataOutputStream(stream).apply {
            writeInt(MAGIC); writeByte(1); writeByte(kind); writeShort(0)
            writeInt(requestId); writeInt(payload.size); write(payload); flush()
        }
    }
    companion object {
        const val MAX_PAYLOAD = 65_536
        private const val MAGIC = 0x4b425247
        fun readFrom(stream: InputStream): Frame {
            val input = DataInputStream(stream)
            require(input.readInt() == MAGIC) { "Invalid frame magic" }
            require(input.readUnsignedByte() == 1) { "Unsupported protocol version" }
            val kind = input.readUnsignedByte()
            require(kind != 0 && input.readUnsignedShort() == 0) { "Invalid frame flags" }
            val id = input.readInt(); val length = input.readInt()
            require(id != 0) { "Request ID zero is reserved" }
            require(length in 0..MAX_PAYLOAD) { "Frame length limit" }
            return Frame(kind, id, ByteArray(length).also { input.readFully(it) })
        }
        fun pairingCode(phoneCertificate: ByteArray, desktopCertificate: ByteArray): String =
            MessageDigest.getInstance("SHA-256").digest("KeyBridge pairing v1".toByteArray(Charsets.UTF_8) + phoneCertificate + desktopCertificate)
                .joinToString("") { "%02X".format(it.toInt() and 255) }
    }
}
