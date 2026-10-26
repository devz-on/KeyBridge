package dev.keybridge.android

import dev.keybridge.core.Approval
import dev.keybridge.core.Credential
import dev.keybridge.core.CtapFailure
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeoutOrNull
import java.security.Signature

sealed interface PhonePrompt {
    val approval: Approval
    data class Biometric(override val approval: Approval, val signature: Signature?, val result: CompletableDeferred<Signature?> = CompletableDeferred()) : PhonePrompt
    data class Account(override val approval: Approval, val credentials: List<Credential>, val result: CompletableDeferred<Credential> = CompletableDeferred()) : PhonePrompt
    data class Pair(override val approval: Approval, val code: String, val fingerprint: String, val result: CompletableDeferred<Boolean> = CompletableDeferred()) : PhonePrompt
}

/** Every operation gets an independent deferred. Cancellation never leaves an approval token behind. */
class ApprovalBroker(private val timeoutSeconds: () -> Long, private val pending: () -> Unit) {
    private val mutex = Mutex()
    private val mutablePrompt = MutableStateFlow<PhonePrompt?>(null)
    val prompt = mutablePrompt.asStateFlow()
    private suspend fun <T> ask(value: PhonePrompt, deferred: CompletableDeferred<T>): T = mutex.withLock {
        mutablePrompt.value = value; pending()
        try { (withTimeoutOrNull(timeoutSeconds() * 1000) { Delivered(deferred.await()) } ?: throw CtapFailure(0x2f)).value }
        finally { deferred.cancel(); if (mutablePrompt.value === value) mutablePrompt.value = null }
    }
    suspend fun biometric(approval: Approval, signature: Signature? = null): Signature? {
        val p = PhonePrompt.Biometric(approval, signature)
        return ask(p, p.result)
    }
    suspend fun select(approval: Approval, credentials: List<Credential>): Credential {
        val p = PhonePrompt.Account(approval, credentials)
        return ask(p, p.result)
    }
    suspend fun pair(approval: Approval, code: String, fingerprint: String): Boolean {
        val p = PhonePrompt.Pair(approval, code, fingerprint)
        return ask(p, p.result)
    }
    fun reject() {
        when (val p = mutablePrompt.value) {
            is PhonePrompt.Biometric -> p.result.completeExceptionally(CtapFailure(0x27))
            is PhonePrompt.Account -> p.result.completeExceptionally(CtapFailure(0x27))
            is PhonePrompt.Pair -> p.result.complete(false)
            null -> Unit
        }
    }
    private data class Delivered<T>(val value: T)
}
