package dev.keybridge.android

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.math.BigInteger
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec
import java.util.Date
import javax.security.auth.x500.X500Principal

/** The TLS key is separate from the authenticator's biometric-bound credential keys. */
object TransportIdentity {
    const val ALIAS = "keybridge.transport.identity.v2"

    fun loadOrCreate(store: KeyStore, alias: String = ALIAS): X509Certificate {
        require(alias == ALIAS || alias.startsWith("keybridge.transport.test."))
        if (!store.containsAlias(alias)) {
            val now = System.currentTimeMillis()
            KeyPairGenerator.getInstance("EC", "AndroidKeyStore").apply {
                initialize(KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY)
                    .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                    // TLS computes its own transcript digest before invoking raw ECDSA signing.
                    .setDigests(KeyProperties.DIGEST_NONE, KeyProperties.DIGEST_SHA256)
                    .setCertificateSubject(X500Principal("CN=KeyBridge Android"))
                    .setCertificateSerialNumber(BigInteger(128, SecureRandom()).abs())
                    .setCertificateNotBefore(Date(now - 86_400_000)).setCertificateNotAfter(Date(now + 10L * 365 * 86_400_000)).build())
            }.generateKeyPair()
        }
        return (store.getCertificate(alias) as X509Certificate).also {
            it.checkValidity(); it.verify(it.publicKey)
        }
    }
}
