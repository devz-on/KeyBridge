package dev.keybridge.core

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.async
import kotlinx.coroutines.launch
import kotlinx.coroutines.yield
import org.junit.Assert.*
import org.junit.Test
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec

class AuthenticatorTest {
    private class Memory : CredentialRepository {
        val records = mutableListOf<Credential>()
        override fun all() = records.toList()
        override fun insert(credential: Credential) { records += credential }
        override fun reserveCounter(id: ByteArray): Long { val i = records.indexOfFirst { it.id.contentEquals(id) }; val c = records[i].copy(counter = records[i].counter + 1); records[i] = c; return c.counter }
        override fun used(id: ByteArray, at: Long) { val i = records.indexOfFirst { it.id.contentEquals(id) }; records[i] = records[i].copy(lastUsedAt = at) }
        override fun delete(id: ByteArray) { records.removeAll { it.id.contentEquals(id) } }
    }
    /** JVM-only test keys. These are never included in the Android production provider. */
    private class Keys : AuthenticatorKeys {
        val pairs = mutableMapOf<String, KeyPair>(); var denied = false; var cancelled = false; var approvals = 0
        override fun create(credentialId: ByteArray): CreatedKey {
            val alias = credentialId.joinToString("") { "%02x".format(it) }
            val p = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
            pairs[alias] = p; val key = p.public as ECPublicKey
            fun coord(value: java.math.BigInteger) = value.toByteArray().takeLast(32).toByteArray().let { ByteArray(32 - it.size) + it }
            return CreatedKey(alias, coord(key.w.affineX), coord(key.w.affineY), "JVM test only")
        }
        override suspend fun sign(alias: String, message: ByteArray, approval: Approval): ByteArray {
            approve(approval)
            return Signature.getInstance("SHA256withECDSA").apply { initSign(pairs.getValue(alias).private); update(message) }.sign()
        }
        override suspend fun approve(approval: Approval) { approvals++; if (cancelled) throw CancellationException(); if (denied) throw CtapFailure(Authenticator.OPERATION_DENIED) }
        override suspend fun select(credentials: List<Credential>, approval: Approval) = credentials.first()
        override fun delete(alias: String) { pairs.remove(alias) }
    }
    private val hash = ByteArray(32) { it.toByte() }
    private fun make(rp: String = "example.test", rk: Boolean = true, exclude: List<Any>? = null): ByteArray {
        val req = linkedMapOf<Int, Any>(1 to hash, 2 to mapOf("id" to rp), 3 to mapOf("id" to byteArrayOf(42), "name" to "alice", "displayName" to "Alice"), 4 to listOf(mapOf("alg" to -7, "type" to "public-key")), 7 to mapOf("rk" to rk))
        if (exclude != null) req[5] = exclude
        return byteArrayOf(1) + Cbor.encode(req)
    }
    private fun assertion(rp: String = "example.test", allow: ByteArray? = null): ByteArray {
        val req = linkedMapOf<Int, Any>(1 to rp, 2 to hash)
        if (allow != null) req[3] = listOf(mapOf("id" to allow, "type" to "public-key"))
        return byteArrayOf(2) + Cbor.encode(req)
    }
    @Suppress("UNCHECKED_CAST") private fun result(bytes: ByteArray): Map<Any, Any?> { assertEquals(0, bytes[0].toInt()); return Cbor.decode(bytes.copyOfRange(1, bytes.size)) as Map<Any, Any?> }
    @Suppress("UNCHECKED_CAST") private fun replace(message: ByteArray, key: Int, value: Any): ByteArray {
        val request = (Cbor.decode(message.copyOfRange(1, message.size)) as Map<Any, Any?>).toMutableMap()
        request[key] = value
        return byteArrayOf(message[0]) + Cbor.encode(request)
    }
    @Test fun getInfoIsHonest() = runBlocking {
        val info = result(Authenticator(Memory(), Keys()).execute(byteArrayOf(4), "Test"))
        assertEquals(listOf("FIDO_2_0"), info[1]); assertFalse((info[4] as Map<*, *>).containsKey("clientPin")); assertFalse(info.containsKey(6))
    }
    @Test fun registrationHasCanonicalCoseAndNoneAttestation() = runBlocking {
        val store = Memory(); val keys = Keys(); val engine = Authenticator(store, keys)
        val response = result(engine.execute(make(), "Test")); val data = response[2] as ByteArray
        assertEquals("none", response[1]); assertEquals(emptyMap<Any, Any>(), response[3]); assertEquals(0x45, data[32].toInt()); assertEquals(1, keys.approvals)
        assertArrayEquals(MessageDigest.getInstance("SHA-256").digest("example.test".toByteArray()), data.copyOfRange(0, 32))
        assertArrayEquals(store.records[0].id, data.copyOfRange(55, 87)); val cose = Cbor.decode(data.copyOfRange(87, data.size)) as Map<*, *>
        assertEquals(-7, cose[3]); assertEquals(32, (cose[-2] as ByteArray).size); assertEquals(32, (cose[-3] as ByteArray).size)
    }
    @Test fun assertionSignatureVerifiesAndCounterPersists() = runBlocking {
        val store = Memory(); val keys = Keys(); val engine = Authenticator(store, keys); result(engine.execute(make(), "Test"))
        repeat(3) { n ->
            val response = result(engine.execute(assertion(), "Test")); val data = response[2] as ByteArray
            assertEquals(5, data[32].toInt()); assertEquals(n + 1, data[36].toInt()); assertEquals((n + 1).toLong(), store.records[0].counter)
            val valid = Signature.getInstance("SHA256withECDSA").apply { initVerify(keys.pairs.values.first().public); update(data + hash) }.verify(response[3] as ByteArray)
            assertTrue(valid)
        }
        assertTrue(store.records[0].lastUsedAt > 0)
    }
    @Test fun nonDiscoverableRequiresMatchingAllowList() = runBlocking {
        val store = Memory(); val engine = Authenticator(store, Keys()); result(engine.execute(make(rk = false), "Test"))
        assertEquals(0x2e, engine.execute(assertion(), "Test")[0].toInt()); result(engine.execute(assertion(allow = store.records[0].id), "Test"))
        assertEquals(0x2e, engine.execute(assertion(rp = "other.test", allow = store.records[0].id), "Test")[0].toInt())
        assertEquals(0x2e, engine.execute(assertion(allow = ByteArray(32)), "Test")[0].toInt())
    }
    @Test fun excludeRequiresApprovalAndDoesNotCreateKey() = runBlocking {
        val store = Memory(); val keys = Keys(); val engine = Authenticator(store, keys); result(engine.execute(make(), "Test"))
        val exclusion = listOf(mapOf("id" to store.records[0].id, "type" to "public-key"))
        assertEquals(0x19, engine.execute(make(exclude = exclusion), "Test")[0].toInt()); assertEquals(2, keys.approvals); assertEquals(1, keys.pairs.size)
    }
    @Test fun rejectionCleansNewKeyAndCannotAuthenticate() = runBlocking {
        val store = Memory(); val keys = Keys().apply { denied = true }; val engine = Authenticator(store, keys)
        assertEquals(0x27, engine.execute(make(), "Test")[0].toInt()); assertTrue(keys.pairs.isEmpty()); assertTrue(store.all().isEmpty())
    }
    @Test fun cancellationCleansNewKey() = runBlocking {
        val store = Memory(); val keys = Keys().apply { cancelled = true }; val engine = Authenticator(store, keys)
        try { engine.execute(make(), "Test"); fail("Expected cancellation") } catch (_: CancellationException) { }
        assertTrue(keys.pairs.isEmpty()); assertTrue(store.all().isEmpty())
    }
    @Test fun failedAssertionDoesNotReuseCounter() = runBlocking {
        val store = Memory(); val keys = Keys(); val engine = Authenticator(store, keys); result(engine.execute(make(), "Test")); keys.denied = true
        assertEquals(0x27, engine.execute(assertion(), "Test")[0].toInt()); assertEquals(1L, store.records[0].counter)
        keys.denied = false; result(engine.execute(assertion(), "Test")); assertEquals(2L, store.records[0].counter)
    }
    @Test fun malformedAndUnsupportedRequestsFailClosed() = runBlocking {
        val engine = Authenticator(Memory(), Keys())
        assertEquals(3, engine.execute(byteArrayOf(), "Test")[0].toInt()); assertEquals(3, engine.execute(ByteArray(7610), "Test")[0].toInt())
        assertEquals(1, engine.execute(byteArrayOf(6), "Test")[0].toInt()); assertEquals(0x30, engine.execute(byteArrayOf(8), "Test")[0].toInt())
        assertEquals(0x12, engine.execute(byteArrayOf(1, 0xa1.toByte(), 1), "Test")[0].toInt()); assertEquals(0x14, engine.execute(byteArrayOf(1) + Cbor.encode(emptyMap<Int, Int>()), "Test")[0].toInt())
    }
    @Test fun unknownOptionsAreIgnoredButKnownInvalidOptionsFail() = runBlocking {
        val store = Memory(); val keys = Keys(); val engine = Authenticator(store, keys)
        result(engine.execute(replace(make(), 7, mapOf("rk" to true, "futureOption" to 42)), "Test"))
        result(engine.execute(replace(assertion(), 5, mapOf("uv" to true, "futureOption" to listOf(1))), "Test"))
        assertEquals(0x2b, engine.execute(replace(assertion(), 5, mapOf("rk" to false)), "Test")[0].toInt())
        assertEquals(0x12, engine.execute(replace(assertion(), 5, mapOf("uv" to "true")), "Test")[0].toInt())
        assertEquals(0x2c, engine.execute(replace(make(), 7, mapOf("up" to false)), "Test")[0].toInt())
    }
    @Test fun unsupportedDescriptorTypesCannotMatchAStoredCredential() = runBlocking {
        val store = Memory(); val engine = Authenticator(store, Keys()); result(engine.execute(make(rk = false), "Test"))
        val wrongType = listOf(mapOf("id" to store.records.single().id, "type" to "future-key-type"))
        assertEquals(0x2e, engine.execute(replace(assertion(), 3, wrongType), "Test")[0].toInt())
    }
    @Test fun upFalseWithUvTrueStillRequiresFreshApprovalAndReturnsValidSignature() = runBlocking {
        assertFreshApprovalForUpFalse(true)
    }
    @Test fun upFalseWithUvFalseStillRequiresFreshApprovalAndReturnsValidSignature() = runBlocking {
        assertFreshApprovalForUpFalse(false)
    }
    private suspend fun assertFreshApprovalForUpFalse(uv: Boolean) {
        val store = Memory(); val keys = Keys(); val engine = Authenticator(store, keys)
        result(engine.execute(make(), "Test")); assertEquals(1, keys.approvals)
        val response = result(engine.execute(replace(assertion(), 5, mapOf("up" to false, "uv" to uv)), "Test"))
        assertEquals(2, keys.approvals)
        val data = response[2] as ByteArray
        assertEquals(0x05, data[32].toInt())
        assertEquals(1L, store.records.single().counter)
        assertTrue(Signature.getInstance("SHA256withECDSA").apply {
            initVerify(keys.pairs.values.single().public); update(data + hash)
        }.verify(response[3] as ByteArray))
    }
    @Test fun upFalseNeverBypassesRejectedBiometricApproval() = runBlocking {
        for (uv in listOf(true, false)) {
            val store = Memory(); val keys = Keys(); val engine = Authenticator(store, keys)
            result(engine.execute(make(), "Test")); keys.denied = true
            val response = engine.execute(replace(assertion(), 5, mapOf("up" to false, "uv" to uv)), "Test")
            assertArrayEquals(byteArrayOf(0x27), response)
            assertEquals(2, keys.approvals)
            assertEquals(1L, store.records.single().counter)
        }
    }
    @Test fun maintenanceExcludesCredentialOperations() = runBlocking<Unit> {
        val engine = Authenticator(Memory(), Keys())
        val entered = CompletableDeferred<Unit>(); val release = CompletableDeferred<Unit>()
        val maintenance = launch { engine.withExclusiveMaintenance { entered.complete(Unit); release.await() } }
        entered.await()
        assertEquals(0x06, engine.execute(make(), "Test")[0].toInt())
        release.complete(Unit); maintenance.join()
        result(engine.execute(make(), "Test"))
    }
    @Test fun maintenanceWaitsForAnInFlightCredentialOperation() = runBlocking {
        val keys = Keys(); val signing = CompletableDeferred<Unit>(); val release = CompletableDeferred<Unit>()
        val gated = object : AuthenticatorKeys by keys {
            override suspend fun sign(alias: String, message: ByteArray, approval: Approval): ByteArray {
                signing.complete(Unit); release.await(); return keys.sign(alias, message, approval)
            }
        }
        val engine = Authenticator(Memory(), gated)
        val registration = async { engine.execute(make(), "Test") }; signing.await()
        var maintenanceEntered = false
        val maintenance = launch { engine.withExclusiveMaintenance { maintenanceEntered = true } }
        yield(); assertFalse(maintenanceEntered)
        release.complete(Unit); result(registration.await()); maintenance.join(); assertTrue(maintenanceEntered)
    }
}
