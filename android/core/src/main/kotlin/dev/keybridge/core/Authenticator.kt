package dev.keybridge.core

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.sync.Mutex
import java.security.MessageDigest
import java.security.SecureRandom
import java.time.Instant

data class Credential(
    val id: ByteArray, val rpId: String, val userId: ByteArray,
    val userName: String, val displayName: String, val alias: String,
    val x: ByteArray, val y: ByteArray, val discoverable: Boolean,
    val createdAt: Long = Instant.now().toEpochMilli(), val lastUsedAt: Long = 0,
    val counter: Long = 0, val security: String = "Unknown"
)
data class CreatedKey(val alias: String, val x: ByteArray, val y: ByteArray, val security: String)
data class Approval(val operation: String, val rpId: String, val account: String, val computer: String)

interface CredentialRepository {
    fun all(): List<Credential>
    fun insert(credential: Credential)
    /** Durably reserve before signing. A cancelled operation can leave a gap; a value must never be reused. */
    fun reserveCounter(id: ByteArray): Long
    fun used(id: ByteArray, at: Long)
    fun delete(id: ByteArray)
}
interface AuthenticatorKeys {
    fun create(credentialId: ByteArray): CreatedKey
    /** Platform implementation must use a fresh, operation-bound biometric CryptoObject. */
    suspend fun sign(alias: String, message: ByteArray, approval: Approval): ByteArray
    suspend fun approve(approval: Approval)
    suspend fun select(credentials: List<Credential>, approval: Approval): Credential
    fun delete(alias: String)
}
class CtapFailure(val status: Int) : Exception("CTAP error 0x${status.toString(16)}")

/** CTAP2.0 ES256 authenticator. Private keys are supplied only by the platform key provider. */
class Authenticator(private val repository: CredentialRepository, private val keys: AuthenticatorKeys, private val userVerificationAvailable: () -> Boolean = { true }) {
    private val lock = Mutex()
    /** Local deletion/reset uses the same boundary as credential generation and signing. */
    suspend fun <T> withExclusiveMaintenance(action: suspend () -> T): T {
        lock.lock()
        try { return action() } finally { lock.unlock() }
    }
    companion object {
        const val MAX_MESSAGE = 7609
        const val INVALID_COMMAND = 0x01
        const val INVALID_PARAMETER = 0x02
        const val INVALID_LENGTH = 0x03
        const val INVALID_CBOR = 0x12
        const val MISSING_PARAMETER = 0x14
        const val LIMIT_EXCEEDED = 0x15
        const val CREDENTIAL_EXCLUDED = 0x19
        const val UNSUPPORTED_ALGORITHM = 0x26
        const val OPERATION_DENIED = 0x27
        const val UNSUPPORTED_OPTION = 0x2b
        const val INVALID_OPTION = 0x2c
        const val NO_CREDENTIALS = 0x2e
        const val KEEPALIVE_CANCEL = 0x2d
        const val NOT_ALLOWED = 0x30
    }
    suspend fun execute(message: ByteArray, computer: String): ByteArray {
        if (message.isEmpty() || message.size > MAX_MESSAGE) return byteArrayOf(INVALID_LENGTH.toByte())
        if (!lock.tryLock()) return byteArrayOf(0x06)
        try {
            val command = message[0].toInt() and 255
            if (command == 4) return if (message.size == 1) success(info()) else byteArrayOf(INVALID_LENGTH.toByte())
            if (command == 8) return byteArrayOf(NOT_ALLOWED.toByte()) // Account choice is completed on the phone.
            if (command != 1 && command != 2) return byteArrayOf(INVALID_COMMAND.toByte())
            val request = try {
                @Suppress("UNCHECKED_CAST")
                Cbor.decode(message.copyOfRange(1, message.size)) as? Map<Any, Any?> ?: fail(INVALID_CBOR)
            } catch (e: CtapFailure) { throw e } catch (_: Exception) { fail(INVALID_CBOR) }
            return success(if (command == 1) makeCredential(request, computer) else getAssertion(request, computer))
        } catch (e: CtapFailure) { return byteArrayOf(e.status.toByte())
        } catch (e: CancellationException) { throw e
        } catch (_: Exception) { return byteArrayOf(0x7f)
        } finally { lock.unlock() }
    }
    private fun success(body: Map<*, *>): ByteArray = byteArrayOf(0) + Cbor.encode(body)
    private fun info(): Map<Int, Any> = mapOf(
        1 to listOf("FIDO_2_0"), 3 to ByteArray(16),
        4 to mapOf("rk" to true, "up" to true, "uv" to userVerificationAvailable(), "plat" to false), 5 to MAX_MESSAGE
    )
    private suspend fun makeCredential(request: Map<Any, Any?>, computer: String): Map<Int, Any> {
        val clientHash = bytes(request, 1, 32, 32)
        val rp = map(request, 2); val rpId = rpId(rp["id"])
        val user = map(request, 3); val userId = bytes(user, "id", 1, 64)
        val name = optionalString(user, "name", 64); val display = optionalString(user, "displayName", 64)
        val parameters = list(request, 4)
        if (parameters.size > 64) fail(LIMIT_EXCEEDED)
        if (parameters.none { it is Map<*, *> && it["type"] == "public-key" && it["alg"] == -7 }) fail(UNSUPPORTED_ALGORITHM)
        rejectPin(request, 8, 9)
        val options = optionalMap(request, 7)
        options.forEach { (key, value) -> if (key in listOf("rk", "uv", "up") && value !is Boolean) fail(INVALID_CBOR) }
        if (options["up"] == false) fail(INVALID_OPTION)
        val discoverable = options["rk"] == true
        val exclusions = descriptors(request, 5)
        val approval = Approval("Create passkey", rpId, display.ifEmpty { name }, computer)
        if (repository.all().any { it.rpId == rpId && exclusions.any { id -> id.contentEquals(it.id) } }) {
            keys.approve(approval)
            fail(CREDENTIAL_EXCLUDED)
        }
        // No extension is advertised; unknown optional extensions are ignored under CTAP2 rules.
        optionalMap(request, 6)
        if (repository.all().size >= 1000) fail(0x28)
        val id = ByteArray(32).also { SecureRandom().nextBytes(it) }
        val created = keys.create(id)
        var persisted = false
        try {
            val cose = Cbor.encode(mapOf(1 to 2, 3 to -7, -1 to 1, -2 to created.x, -3 to created.y))
            val authData = hash(rpId.toByteArray(Charsets.UTF_8)) + byteArrayOf(0x45) + counter(0) + ByteArray(16) + byteArrayOf(0, id.size.toByte()) + id + cose
            // A fresh biometric authorizes this exact new key's use before releasing its registration.
            // With "none" attestation this proof is intentionally not sent to the RP.
            keys.sign(created.alias, authData + clientHash, approval)
            currentCoroutineContext().ensureActive()
            repository.insert(Credential(id, rpId, userId, name, display, created.alias, created.x, created.y, discoverable, security = created.security))
            persisted = true
            return mapOf(1 to "none", 2 to authData, 3 to emptyMap<Any, Any>())
        } finally { if (!persisted) keys.delete(created.alias) }
    }
    private suspend fun getAssertion(request: Map<Any, Any?>, computer: String): Map<Int, Any> {
        val rpId = rpId(request[1]); val clientHash = bytes(request, 2, 32, 32)
        rejectPin(request, 6, 7)
        val options = optionalMap(request, 5)
        options.forEach { (key, value) -> if (key in listOf("uv", "up", "rk") && value !is Boolean) fail(INVALID_CBOR) }
        if (options.containsKey("rk")) fail(UNSUPPORTED_OPTION)
        // up=false does not relax this phone's mandatory fresh biometric key-use policy.
        // Successful built-in verification itself supplies evidence of user interaction;
        // UP and UV below truthfully reflect that verification (CTAP 6.2.2, step 8).
        optionalMap(request, 4)
        val allow = descriptors(request, 3)
        val hasAllow = request.containsKey(3)
        val candidates = repository.all().filter { c -> c.rpId == rpId && if (hasAllow) allow.any { it.contentEquals(c.id) } else c.discoverable }
        if (candidates.isEmpty()) fail(NO_CREDENTIALS)
        val selection = Approval(if (options["up"] == false) "Verify passkey for Windows" else "Use passkey", rpId, "Choose an account", computer)
        val chosen = if (candidates.size == 1) candidates.single() else keys.select(candidates, selection)
        if (candidates.none { it.id.contentEquals(chosen.id) }) fail(OPERATION_DENIED)
        val count = repository.reserveCounter(chosen.id)
        val authData = hash(rpId.toByteArray(Charsets.UTF_8)) + byteArrayOf(0x05) + counter(count)
        val signature = keys.sign(chosen.alias, authData + clientHash, selection.copy(account = chosen.displayName.ifEmpty { chosen.userName }))
        currentCoroutineContext().ensureActive()
        repository.used(chosen.id, Instant.now().toEpochMilli())
        return linkedMapOf<Int, Any>(
            1 to mapOf("type" to "public-key", "id" to chosen.id), 2 to authData, 3 to signature,
            4 to mapOf("id" to chosen.userId, "name" to chosen.userName, "displayName" to chosen.displayName)
        )
    }
    private fun rejectPin(request: Map<Any, Any?>, a: Int, b: Int) { if (request.containsKey(a) || request.containsKey(b)) fail(UNSUPPORTED_OPTION) }
    private fun descriptors(request: Map<Any, Any?>, key: Int): List<ByteArray> {
        if (!request.containsKey(key)) return emptyList()
        val list = list(request, key)
        if (list.size > 64) fail(LIMIT_EXCEEDED)
        return list.mapNotNull {
            if (it !is Map<*, *>) fail(INVALID_CBOR)
            if (it["type"] !is String) fail(INVALID_CBOR)
            if (it["type"] != "public-key") return@mapNotNull null
            bytes(it, "id", 1, 1024)
        }
    }
    private fun rpId(value: Any?): String {
        val id = value as? String ?: fail(if (value == null) MISSING_PARAMETER else INVALID_CBOR)
        if (id.isBlank() || id.length > 253 || id.any { it.code < 0x21 || it.code > 0x7e } || id.contains('/') || id.contains(':')) fail(INVALID_PARAMETER)
        return id
    }
    private fun optionalString(map: Map<*, *>, key: Any, limit: Int): String {
        val value = map[key] ?: return ""
        if (value !is String) fail(INVALID_CBOR)
        return value.take(limit)
    }
    private fun bytes(map: Map<*, *>, key: Any, min: Int, max: Int): ByteArray {
        val value = map[key] ?: fail(MISSING_PARAMETER)
        if (value !is ByteArray) fail(INVALID_CBOR)
        if (value.size !in min..max) fail(INVALID_PARAMETER)
        return value
    }
    @Suppress("UNCHECKED_CAST")
    private fun map(map: Map<*, *>, key: Any): Map<Any, Any?> = (map[key] ?: fail(MISSING_PARAMETER)) as? Map<Any, Any?> ?: fail(INVALID_CBOR)
    private fun optionalMap(map: Map<*, *>, key: Any): Map<Any, Any?> = if (map.containsKey(key)) map(map, key) else emptyMap()
    private fun list(map: Map<*, *>, key: Any): List<*> = (map[key] ?: fail(MISSING_PARAMETER)) as? List<*> ?: fail(INVALID_CBOR)
    private fun counter(value: Long): ByteArray { if (value !in 0..0xffff_ffffL) fail(0x7f); return byteArrayOf((value shr 24).toByte(), (value shr 16).toByte(), (value shr 8).toByte(), value.toByte()) }
    private fun hash(value: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(value)
    private fun fail(status: Int): Nothing = throw CtapFailure(status)
}
