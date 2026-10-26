package dev.keybridge.android

import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyInfo
import android.security.keystore.KeyPermanentlyInvalidatedException
import android.security.keystore.KeyProperties
import android.security.keystore.StrongBoxUnavailableException
import androidx.biometric.BiometricManager
import dev.keybridge.core.Approval
import dev.keybridge.core.AuthenticatorKeys
import dev.keybridge.core.CreatedKey
import dev.keybridge.core.Credential
import dev.keybridge.core.CtapFailure
import java.math.BigInteger
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec

class KeystoreKeys(private val context: Context, private val broker: ApprovalBroker) : AuthenticatorKeys {
    private val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
    private fun biometricAvailable() {
        if (BiometricManager.from(context).canAuthenticate(BiometricManager.Authenticators.BIOMETRIC_STRONG) != BiometricManager.BIOMETRIC_SUCCESS) throw CtapFailure(0x27)
    }
    override fun create(credentialId: ByteArray): CreatedKey {
        biometricAvailable()
        val alias = "keybridge.credential.${credentialId.joinToString("") { "%02x".format(it.toInt() and 255) }}"
        val builder = KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY)
            .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
            .setDigests(KeyProperties.DIGEST_SHA256)
            .setUserAuthenticationRequired(true)
            .setUserAuthenticationParameters(0, KeyProperties.AUTH_BIOMETRIC_STRONG)
            .setInvalidatedByBiometricEnrollment(true)
            .setUnlockedDeviceRequired(true)
        val canStrongBox = context.packageManager.hasSystemFeature(PackageManager.FEATURE_STRONGBOX_KEYSTORE)
        fun generate(strongBox: Boolean) = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore").apply {
            initialize(builder.setIsStrongBoxBacked(strongBox).build())
        }.generateKeyPair()
        val pair = try { generate(canStrongBox) } catch (e: StrongBoxUnavailableException) { generate(false) }
        check(pair.private.encoded == null) { "Credential key must be non-exportable" }
        val public = pair.public as ECPublicKey
        return CreatedKey(alias, coordinate(public.w.affineX), coordinate(public.w.affineY), security(pair.private))
    }
    override suspend fun sign(alias: String, message: ByteArray, approval: Approval): ByteArray {
        biometricAvailable()
        try {
            val key = store.getKey(alias, null) as? PrivateKey ?: throw CtapFailure(0x2e)
            val signature = Signature.getInstance("SHA256withECDSA").apply { initSign(key) }
            val approved = broker.biometric(approval, signature) ?: throw CtapFailure(0x27)
            check(approved === signature) { "Biometric operation mismatch" }
            approved.update(message)
            return approved.sign()
        } catch (_: KeyPermanentlyInvalidatedException) { throw CtapFailure(0x2e) }
    }
    override suspend fun approve(approval: Approval) { biometricAvailable(); broker.biometric(approval) }
    override suspend fun select(credentials: List<Credential>, approval: Approval) = broker.select(approval, credentials)
    override fun delete(alias: String) { require(alias.startsWith("keybridge.credential.")); if (store.containsAlias(alias)) store.deleteEntry(alias) }
    fun contains(alias: String) = store.containsAlias(alias)
    fun reconcile(existing: Set<String>) {
        store.aliases().toList().filter { it.startsWith("keybridge.credential.") && it !in existing }.forEach { store.deleteEntry(it) }
    }
    fun probeSecurity(): String {
        val alias = "keybridge.probe"
        try {
            val pair = KeyPairGenerator.getInstance("EC", "AndroidKeyStore").apply {
                initialize(KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN).setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1")).setDigests(KeyProperties.DIGEST_SHA256).build())
            }.generateKeyPair()
            return security(pair.private)
        } finally { if (store.containsAlias(alias)) store.deleteEntry(alias) }
    }
    private fun security(key: PrivateKey): String {
        val info = KeyFactory.getInstance(key.algorithm, "AndroidKeyStore").getKeySpec(key, KeyInfo::class.java)
        return if (Build.VERSION.SDK_INT >= 31) when (info.securityLevel) {
            KeyProperties.SECURITY_LEVEL_STRONGBOX -> "StrongBox"
            KeyProperties.SECURITY_LEVEL_TRUSTED_ENVIRONMENT -> "Trusted execution environment"
            KeyProperties.SECURITY_LEVEL_SOFTWARE -> "Software Keystore"
            else -> "Keystore security level unknown"
        } else if (info.isInsideSecureHardware) "Hardware-backed Keystore" else "Software Keystore"
    }
    private fun coordinate(n: BigInteger): ByteArray = n.toByteArray().takeLast(32).toByteArray().let { ByteArray(32 - it.size) + it }
}
