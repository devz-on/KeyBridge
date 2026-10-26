package dev.keybridge.android

import android.content.Context
import android.security.keystore.KeyInfo
import android.security.keystore.KeyProperties
import android.security.keystore.UserNotAuthenticatedException
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import dev.keybridge.core.Credential
import dev.keybridge.core.Approval
import dev.keybridge.core.CtapFailure
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.security.KeyFactory
import java.security.KeyStore
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.Signature
import java.security.MessageDigest
import java.util.UUID

@RunWith(AndroidJUnit4::class)
class SecurityInstrumentedTest {
    private val context: Context get() = ApplicationProvider.getApplicationContext()
    private fun id() = ByteArray(32).also { SecureRandom().nextBytes(it) }
    @Test fun credentialKeyIsNonExportableAndRequiresPerUseBiometric() {
        val keys = KeystoreKeys(context, ApprovalBroker({ 30 }) {})
        val created = keys.create(id())
        try {
            val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
            val key = store.getKey(created.alias, null) as PrivateKey
            assertNull(key.encoded)
            val info = KeyFactory.getInstance("EC", "AndroidKeyStore").getKeySpec(key, KeyInfo::class.java)
            assertTrue(info.isUserAuthenticationRequired)
            assertEquals(0, info.userAuthenticationValidityDurationSeconds)
            assertTrue(info.isInvalidatedByBiometricEnrollment)
            var rejected = false
            var failureDescription = "No exception: the unauthenticated signature unexpectedly succeeded"
            try { Signature.getInstance("SHA256withECDSA").apply { initSign(key); update(byteArrayOf(1, 2, 3)) }.sign() }
            catch (e: Exception) {
                val causes = generateSequence<Throwable>(e) { it.cause }.toList()
                failureDescription = causes.joinToString(" / ") { it.javaClass.simpleName + ": " + it.message }
                rejected = causes.any { it is UserNotAuthenticatedException || (android.os.Build.VERSION.SDK_INT >= 33 && it is android.security.KeyStoreException && it.requiresUserAuthentication()) }
            }
            assertTrue("A signature without fresh biometric authorization must fail: $failureDescription", rejected)
        } finally { keys.delete(created.alias) }
        assertFalse(keys.contains(created.alias))
    }
    @Test fun transportIdentityPermitsTlsTranscriptSigningWithoutExport() {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val alias = "keybridge.transport.test.${UUID.randomUUID()}"
        try {
            val certificate = TransportIdentity.loadOrCreate(store, alias)
            val key = store.getKey(alias, null) as PrivateKey
            assertNull(key.encoded)
            val info = KeyFactory.getInstance("EC", "AndroidKeyStore").getKeySpec(key, KeyInfo::class.java)
            assertTrue(info.digests.contains(KeyProperties.DIGEST_NONE))
            assertTrue(info.digests.contains(KeyProperties.DIGEST_SHA256))
            assertFalse(info.isUserAuthenticationRequired)
            val digest = MessageDigest.getInstance("SHA-256").digest("TLS transcript regression test".toByteArray(Charsets.UTF_8))
            val signature = Signature.getInstance("NONEwithECDSA").apply { initSign(key); update(digest) }.sign()
            assertTrue(Signature.getInstance("NONEwithECDSA").apply { initVerify(certificate.publicKey); update(digest) }.verify(signature))
            assertArrayEquals(certificate.encoded, TransportIdentity.loadOrCreate(store, alias).encoded)
        } finally { if (store.containsAlias(alias)) store.deleteEntry(alias) }
    }
    @Test fun metadataPersistsAndCounterIsDurableBeforeSigning() {
        val dbName = "keybridge-test-${UUID.randomUUID()}.db"
        val credentialId = id()
        val c = Credential(credentialId, "instrumentation.invalid", byteArrayOf(1), "Test account", "Test", "test-only-${UUID.randomUUID()}", ByteArray(32), ByteArray(32), true, security = "Instrumentation metadata only")
        try {
            CredentialDatabase(context, dbName).use { db -> db.insert(c); assertEquals(1L, db.reserveCounter(c.id)); assertEquals(2L, db.reserveCounter(c.id)); db.used(c.id, 12345) }
            CredentialDatabase(context, dbName).use { db -> val read = db.all().single(); assertArrayEquals(c.id, read.id); assertEquals(2L, read.counter); assertEquals(12345, read.lastUsedAt); db.delete(c.id); assertTrue(db.all().isEmpty()) }
        } finally { context.deleteDatabase(dbName) }
    }
    @Test fun alteredMetadataFailsIntegrityCheck() {
        val dbName = "keybridge-test-${UUID.randomUUID()}.db"
        try {
            CredentialDatabase(context, dbName).use { db ->
                val c = Credential(id(), "integrity.invalid", byteArrayOf(1), "", "", "test-only-${UUID.randomUUID()}", ByteArray(32), ByteArray(32), false)
                db.insert(c); db.writableDatabase.execSQL("UPDATE credentials SET counter=42")
                try { db.all(); fail("Unauthenticated metadata change must be rejected") } catch (_: IllegalStateException) { }
            }
        } finally { context.deleteDatabase(dbName) }
    }
    @Test fun cancelledApprovalCannotBeCompletedOrReused() = runBlocking {
        val broker = ApprovalBroker({ 30 }) {}
        val request = Approval("Instrumentation cancellation", "test.invalid", "Test", "This phone")
        val pending = launch { broker.biometric(request) }
        val old = withTimeout(2000) { broker.prompt.filterNotNull().first() } as PhonePrompt.Biometric
        pending.cancelAndJoin()
        assertNull(broker.prompt.value)
        assertFalse(old.result.complete(null))
        val next = launch { broker.biometric(request) }
        val current = withTimeout(2000) { broker.prompt.filterNotNull().first() }
        assertNotSame(old, current)
        next.cancelAndJoin()
        assertNull(broker.prompt.value)
    }
    @Test fun approvalTimeoutIsDistinctFromHostCancellation() = runBlocking {
        val broker = ApprovalBroker({ 1 }) {}
        val request = async {
            try { broker.biometric(Approval("Instrumentation timeout", "test.invalid", "Test", "This phone")); fail("Approval timed out without an error") }
            catch (e: CtapFailure) { assertEquals(0x2f, e.status) }
        }
        withTimeout(3000) { request.await() }
        assertNull(broker.prompt.value)
    }
}
