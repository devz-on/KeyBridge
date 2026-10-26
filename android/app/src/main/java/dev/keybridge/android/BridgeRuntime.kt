package dev.keybridge.android

import android.app.Application
import android.content.Context
import android.content.Intent
import android.os.Build
import androidx.biometric.BiometricManager
import dev.keybridge.core.Approval
import dev.keybridge.core.Authenticator
import dev.keybridge.core.Credential
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

class KeyBridgeApplication : Application() {
    lateinit var runtime: BridgeRuntime
        private set
    override fun onCreate() { super.onCreate(); runtime = BridgeRuntime(this) }
}
data class PairedComputer(val fingerprint: String, val name: String, val at: Long)
data class PhoneState(
    val running: Boolean = false, val connection: String = "Disconnected", val computer: String = "No computer connected",
    val paired: Boolean = false, val credentials: List<Credential> = emptyList(), val history: List<ActivityEntry> = emptyList(),
    val computers: List<PairedComputer> = emptyList(), val security: String = "Checking Keystore", val error: String? = null,
    val lastAuthentication: Long = 0, val biometricAvailable: Boolean = false
)

class BridgeRuntime(val context: Context) {
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    val initialized = CompletableDeferred<Unit>()
    val settings = context.getSharedPreferences("settings", Context.MODE_PRIVATE)
    private val pairs = context.getSharedPreferences("paired-computers", Context.MODE_PRIVATE)
    private val mutableState = MutableStateFlow(PhoneState())
    val state = mutableState.asStateFlow()
    @Volatile var foreground = false
    @Volatile var service: BridgeService? = null
    @Volatile var transport: PhoneTransport? = null
    val broker = ApprovalBroker({ settings.getLong("timeout", 90).coerceIn(30, 120) }) { service?.showApprovalNotification() }
    val database = CredentialDatabase(context)
    val keys = KeystoreKeys(context, broker)
    val authenticator = Authenticator(database, keys) {
        BiometricManager.from(context).canAuthenticate(BiometricManager.Authenticators.BIOMETRIC_STRONG) == BiometricManager.BIOMETRIC_SUCCESS
    }
    init {
        scope.launch {
            try {
                val credentials = database.all()
                keys.reconcile(credentials.map { it.alias }.toSet())
                val missing = credentials.count { !keys.contains(it.alias) }
                val security = keys.probeSecurity()
                update { it.copy(security = security, error = if (missing > 0) "$missing credential key(s) are unavailable. Delete affected entries and register again." else null) }
                refresh()
                initialized.complete(Unit)
            } catch (e: Exception) { error(e.message ?: "Keystore is unavailable"); initialized.completeExceptionally(e) }
        }
    }
    @Synchronized fun update(transform: (PhoneState) -> PhoneState) { mutableState.value = transform(mutableState.value) }
    fun error(message: String) { update { it.copy(error = message.take(512)) } }
    fun clearError() = update { it.copy(error = null) }
    fun refresh() {
        val current = database.all(); val entries = database.history()
        update { it.copy(credentials = current, history = entries, computers = computers(),
            lastAuthentication = entries.firstOrNull { e -> e.operation == "Authentication successful" }?.at ?: it.lastAuthentication,
            biometricAvailable = BiometricManager.from(context).canAuthenticate(BiometricManager.Authenticators.BIOMETRIC_STRONG) == BiometricManager.BIOMETRIC_SUCCESS) }
    }
    fun computers(): List<PairedComputer> = pairs.all.keys.filter { !it.endsWith(".at") }.mapNotNull { pin ->
        (pairs.getString(pin, null) ?: return@mapNotNull null).let { name -> PairedComputer(pin, name, pairs.getLong("$pin.at", 0)) }
    }.sortedByDescending { it.at }
    fun isPaired(pin: String) = pairs.contains(pin)
    fun pair(pin: String, name: String) {
        check(pairs.edit().putString(pin, name.take(80)).putLong("$pin.at", System.currentTimeMillis()).commit()) { "Could not persist pairing" }
        database.event("Computer paired", name.take(80)); refresh()
    }
    fun revoke(pin: String) {
        scope.launch {
            check(pairs.edit().remove(pin).remove("$pin.at").commit()) { "Could not revoke pairing" }
            transport?.revoke(pin); database.event("Computer revoked", "Host authorization removed"); refresh()
        }
    }
    fun start() {
        try { context.startForegroundService(Intent(context, BridgeService::class.java)) }
        catch (e: Exception) { error("Android could not start the visible bridge service: ${e.javaClass.simpleName}") }
    }
    fun stop() { context.stopService(Intent(context, BridgeService::class.java)); broker.reject() }
    fun delete(credential: Credential) {
        scope.launch {
            try {
                initialized.await()
                authenticator.withExclusiveMaintenance {
                    keys.approve(Approval("Delete passkey", credential.rpId, credential.displayName.ifEmpty { credential.userName }, "This phone"))
                    keys.delete(credential.alias); database.delete(credential.id); database.event("Credential deleted", credential.rpId); refresh()
                }
            } catch (_: kotlinx.coroutines.CancellationException) { }
            catch (_: Exception) { error("Deletion was not completed. Fresh fingerprint approval is required.") }
        }
    }
    fun reset() {
        scope.launch {
            try {
                initialized.await()
                // Close network operations before waiting for the authenticator lock or presenting local approval.
                // The foreground service's eventual onDestroy must not cancel this separate local prompt.
                transport?.close()
                context.stopService(Intent(context, BridgeService::class.java))
                authenticator.withExclusiveMaintenance {
                    keys.approve(Approval("Reset credential vault", "All KeyBridge credentials", "This cannot be undone", "This phone"))
                    database.all().forEach { keys.delete(it.alias); database.delete(it.id) }
                    check(pairs.edit().clear().commit()); database.event("Vault reset", "Credentials and paired computers removed"); refresh()
                }
            } catch (_: kotlinx.coroutines.CancellationException) { }
            catch (_: Exception) { error("Reset was not completed. Check device security and try again.") }
        }
    }
    fun diagnostics(): String = buildString {
        appendLine("KeyBridge Android ${BuildConfig.VERSION_NAME}")
        appendLine("Android API: ${Build.VERSION.SDK_INT}; device: ${Build.MANUFACTURER} ${Build.MODEL}")
        appendLine("Bridge: ${state.value.connection}; service running: ${state.value.running}")
        appendLine("Credential count: ${state.value.credentials.size}; paired host count: ${state.value.computers.size}")
        appendLine("Keystore probe: ${state.value.security}; strong biometrics: ${state.value.biometricAvailable}")
        appendLine("Protocol: KBRG/1 over mutual TLS; loopback 127.0.0.1:37123")
        appendLine("CTAP: FIDO_2_0 ES256; no U2F, ClientPIN, vendor attestation or FIDO certification")
        appendLine("RP names, account handles, credential IDs and host fingerprints are excluded.")
    }
}
