package dev.keybridge.android

import android.os.Build
import dev.keybridge.core.Approval
import dev.keybridge.core.Frame
import dev.keybridge.core.TlsUpgrade
import dev.keybridge.core.Cbor
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.TimeoutCancellationException
import org.json.JSONObject
import java.math.BigInteger
import java.net.InetAddress
import java.net.Socket
import java.net.ServerSocket
import java.security.KeyStore
import java.security.MessageDigest
import java.security.Principal
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.security.interfaces.ECPublicKey
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLEngine
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509ExtendedKeyManager
import javax.net.ssl.X509TrustManager

/** Local transport only. Self-signed certificates bootstrap a user-confirmed, pinned mTLS pairing. */
class PhoneTransport(private val runtime: BridgeRuntime) : AutoCloseable {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val running = AtomicBoolean(true)
    private val sockets = ConcurrentHashMap<Socket, String>()
    private val sessionLock = Any()
    private var sessionOwner: SSLSocket? = null
    private var listener: ServerSocket? = null
    private val identityAlias = TransportIdentity.ALIAS
    private val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
    private lateinit var certificate: X509Certificate
    fun run() {
        ensureIdentity()
        val context = SSLContext.getInstance("TLS").apply { init(arrayOf(IdentityKeyManager()), arrayOf(BootstrapTrustManager()), SecureRandom()) }
        val server = ServerSocket(37123, 4, InetAddress.getByName("127.0.0.1"))
        listener = server
        runtime.update { it.copy(running = true, connection = "Waiting for ADB tunnel", error = null) }
        while (running.get()) {
            val socket = try { server.accept() } catch (e: Exception) { if (!running.get()) break else throw e }
            if (sockets.size >= 4) { socket.close(); continue }
            sockets[socket] = ""
            scope.launch { connection(socket, context) }
        }
    }
    private suspend fun connection(rawSocket: Socket, context: SSLContext) {
        var tlsSocket: SSLSocket? = null
        try {
            rawSocket.soTimeout = 15_000
            TlsUpgrade.accept(rawSocket.inputStream, rawSocket.outputStream)
            // The upgrade has no secrets and enables no application commands. TLS remains mandatory.
            val socket = context.socketFactory.createSocket(rawSocket, "localhost", rawSocket.port, true) as SSLSocket
            tlsSocket = socket
            socket.useClientMode = false
            socket.needClientAuth = true
            socket.enabledProtocols = socket.supportedProtocols.filter { it == "TLSv1.3" || it == "TLSv1.2" }.toTypedArray()
            socket.sslParameters = socket.sslParameters.apply { applicationProtocols = arrayOf(TlsUpgrade.APPLICATION_PROTOCOL) }
            authenticatedConnection(socket, rawSocket)
        } catch (failure: Exception) {
            if (BuildConfig.DEBUG) android.util.Log.w("KeyBridgeTls", "TLS upgrade failed: ${failure.javaClass.simpleName}")
        } finally {
            sockets.remove(rawSocket)
            runCatching { tlsSocket?.close() }
            runCatching { rawSocket.close() }
        }
    }
    private suspend fun authenticatedConnection(socket: SSLSocket, rawSocket: Socket) {
        class Operation(val id: Int) { lateinit var job: Job }
        val active = AtomicReference<Operation?>(null)
        var peerName = "Unpaired computer"
        var pin = ""
        var lastId = 0L
        var greeted = false
        var handshakeComplete = false
        val writer = Any()
        fun send(frame: Frame) = synchronized(writer) { frame.writeTo(socket.outputStream) }
        fun finish(operation: Operation, frame: Frame) = synchronized(writer) {
            // Clear before publishing the terminal response while holding the same lock used by dispatch.
            // A subsequent request cannot observe the old job's post-response bookkeeping as busy.
            if (active.compareAndSet(operation, null)) frame.writeTo(socket.outputStream)
        }
        fun error(id: Int, message: String) { send(Frame(0xff, id, message.toByteArray(Charsets.UTF_8))) }
        fun updateSession(transform: (PhoneState) -> PhoneState) = synchronized(sessionLock) {
            if (sessionOwner === socket && !socket.isClosed) runtime.update(transform)
        }
        fun status(paired: Boolean, code: String? = null): ByteArray = JSONObject().apply {
            put("deviceName", "${Build.MANUFACTURER} ${Build.MODEL}")
            put("paired", paired)
            if (code != null) put("pairingCode", code)
            if (paired) {
                put("credentialCount", runtime.state.value.credentials.size)
                put("hardwareSecurity", runtime.state.value.security)
                put("biometricAvailable", runtime.state.value.biometricAvailable)
            }
        }.toString().toByteArray(Charsets.UTF_8)
        try {
            socket.soTimeout = 15_000
            socket.useClientMode = false; socket.needClientAuth = true
            socket.startHandshake()
            require(socket.applicationProtocol == TlsUpgrade.APPLICATION_PROTOCOL) { "KeyBridge ALPN negotiation is required" }
            val peer = socket.session.peerCertificates.single() as X509Certificate
            pin = fingerprint(peer.encoded); sockets[rawSocket] = pin
            handshakeComplete = true
            val code = Frame.pairingCode(certificate.encoded, peer.encoded)
            socket.soTimeout = 180_000
            while (running.get() && !socket.isClosed) {
                val frame = Frame.readFrom(socket.inputStream)
                val unsignedId = frame.requestId.toLong() and 0xffff_ffffL
                require(unsignedId != 0L) { "Request ID zero is reserved" }
                if (frame.kind == 4) {
                    val operation = synchronized(writer) { active.get() }
                    require(operation != null && frame.payload.isEmpty() && frame.requestId == operation.id && operation.job.isActive) { "Invalid cancellation" }
                    operation.job.cancel(); continue
                }
                require(unsignedId > lastId) { "Duplicate or reordered request" }; lastId = unsignedId
                if (synchronized(writer) { active.get() != null } && frame.kind in listOf(1, 2, 3)) { error(frame.requestId, "Another operation is in progress"); continue }
                when (frame.kind) {
                    1 -> {
                        require(!greeted && frame.payload.size <= 1024)
                        val hello = JSONObject(String(frame.payload, Charsets.UTF_8))
                        val ownsSession = synchronized(sessionLock) {
                            if (sessionOwner == null) { sessionOwner = socket; true } else sessionOwner === socket
                        }
                        if (!ownsSession) { error(frame.requestId, "Another computer is already connected"); return }
                        peerName = safeName(hello.optString("hostName", "Windows computer"))
                        greeted = true
                        send(Frame(0x81, frame.requestId, status(runtime.isPaired(pin), code)))
                        updateSession { it.copy(connection = if (runtime.isPaired(pin)) "Connected" else "Pairing required", computer = peerName, paired = runtime.isPaired(pin)) }
                    }
                    2 -> {
                        require(greeted && frame.payload.size <= 1024)
                        val name = safeName(JSONObject(String(frame.payload, Charsets.UTF_8)).optString("hostName", peerName))
                        val operation = Operation(frame.requestId)
                        operation.job = scope.launch(start = CoroutineStart.LAZY) {
                            try {
                                if (!runtime.isPaired(pin)) {
                                    updateSession { it.copy(connection = "Pairing") }
                                    val approve = runtime.broker.pair(Approval("Pair this computer", "Compare the code on both screens", name, name), code, pin)
                                    if (!approve) { finish(operation, Frame(0xff, frame.requestId, "Pairing rejected".toByteArray(Charsets.UTF_8))); return@launch }
                                    runtime.pair(pin, name)
                                }
                                updateSession { it.copy(connection = "Connected", paired = true, computer = name) }
                                finish(operation, Frame(0x82, frame.requestId, "{\"paired\":true}".toByteArray(Charsets.UTF_8)))
                            } catch (_: TimeoutCancellationException) {
                                runCatching { finish(operation, Frame(0xff, frame.requestId, "Pairing timed out".toByteArray(Charsets.UTF_8))) }
                            } catch (_: CancellationException) { }
                            catch (_: Exception) { runCatching { finish(operation, Frame(0xff, frame.requestId, "Pairing did not complete".toByteArray(Charsets.UTF_8))) } }
                            finally { active.compareAndSet(operation, null) }
                        }
                        active.set(operation); operation.job.start()
                    }
                    3 -> {
                        require(greeted)
                        if (!runtime.isPaired(pin)) { error(frame.requestId, "Computer is not paired"); continue }
                        require(frame.payload.size in 1..7609)
                        val operation = Operation(frame.requestId)
                        operation.job = scope.launch(start = CoroutineStart.LAZY) {
                            updateSession { it.copy(connection = "Authentication requested") }
                            val reply = try {
                                withTimeout(runtime.settings.getLong("timeout", 90).coerceIn(30, 120) * 1000) {
                                    logAssertionOptions(frame.payload)
                                    runtime.authenticator.execute(frame.payload, peerName)
                                }
                            } catch (_: TimeoutCancellationException) { byteArrayOf(0x2f) }
                            catch (_: CancellationException) { byteArrayOf(0x2d) }
                            catch (_: Exception) { byteArrayOf(0x7f) }
                            if (!runtime.isPaired(pin) || socket.isClosed) { active.compareAndSet(operation, null); return@launch }
                            try { finish(operation, Frame(0x83, frame.requestId, reply)) }
                            catch (_: Exception) { runCatching { socket.close() }; return@launch }
                            // No second terminal response is sent if diagnostics/persistence fails after publication.
                            try {
                                val command = frame.payload.first().toInt() and 255
                                if (command == 1 || command == 2) runtime.database.event(if (reply[0] == 0.toByte()) "Authentication successful" else "Authentication declined", if (command == 1) "Credential registration" else "Credential assertion")
                                runtime.refresh()
                            } catch (_: Exception) {
                                runtime.error("The request finished, but local activity or security checks could not be refreshed.")
                            }
                            synchronized(writer) {
                                if (active.get() == null) updateSession { it.copy(connection = if (reply[0] == 0.toByte()) "Authenticator ready" else "Connected") }
                            }
                        }
                        active.set(operation); operation.job.start()
                    }
                    5 -> {
                        require(greeted && frame.payload.isEmpty())
                        if (!runtime.isPaired(pin)) { error(frame.requestId, "Computer is not paired"); continue }
                        send(Frame(0x85, frame.requestId, status(true)))
                    }
                    6 -> { require(greeted); if (!runtime.isPaired(pin)) { error(frame.requestId, "Computer is not paired"); continue }; send(Frame(0x86, frame.requestId, frame.payload)) }
                    else -> error(frame.requestId, "Unsupported message kind")
                }
            }
        } catch (failure: Exception) {
            if (!handshakeComplete && BuildConfig.DEBUG) {
                // Bootstrap diagnostics only, before accepting any application request. Never log certificates or frames.
                val causes = generateSequence<Throwable>(failure) { it.cause }.take(4)
                    .joinToString(" / ") { it.javaClass.simpleName }
                android.util.Log.w("KeyBridgeTls", "Bootstrap TLS failed: $causes")
                runtime.error("Secure connection failed (${failure.javaClass.simpleName}). Check desktop transport diagnostics.")
            }
        }
        finally {
            active.getAndSet(null)?.job?.cancel(); runCatching { socket.close() }
            synchronized(sessionLock) {
                if (sessionOwner === socket) {
                    sessionOwner = null
                    runtime.update { it.copy(connection = if (it.running) "Waiting for ADB tunnel" else "Disconnected", computer = "No computer connected", paired = false) }
                }
            }
        }
    }
    fun revoke(fingerprint: String) { sockets.entries.filter { it.value == fingerprint }.forEach { runCatching { it.key.close() } } }
    override fun close() { running.set(false); runCatching { listener?.close() }; sockets.keys.forEach { runCatching { it.close() } }; scope.cancel() }
    private fun ensureIdentity() {
        certificate = TransportIdentity.loadOrCreate(store)
    }
    private fun logAssertionOptions(payload: ByteArray) {
        if (!BuildConfig.DEBUG || payload.firstOrNull()?.toInt() != 2) return
        val request = runCatching { Cbor.decode(payload.copyOfRange(1, payload.size)) as? Map<*, *> }.getOrNull()
        if (request == null) {
            android.util.Log.w("KeyBridgeCtap", "GetAssertion options: malformed CBOR")
            return
        }
        val options = request[5] as? Map<*, *>
        fun flag(name: String): String = when {
            options == null && request.containsKey(5) -> "invalid-options"
            options == null || !options.containsKey(name) -> "absent"
            options[name] is Boolean -> options[name].toString()
            else -> "invalid"
        }
        // Diagnostic booleans only: no RP, client hash, account, credential identifier or PIN data.
        android.util.Log.w("KeyBridgeCtap", "GetAssertion options: up=${flag("up")}, uv=${flag("uv")}, rk=${flag("rk")}, pinAuthPresent=${request.containsKey(6)}, pinProtocolPresent=${request.containsKey(7)}")
    }
    private inner class IdentityKeyManager : X509ExtendedKeyManager() {
        override fun getClientAliases(keyType: String?, issuers: Array<out Principal>?) = arrayOf(identityAlias)
        override fun chooseClientAlias(keyType: Array<out String>?, issuers: Array<out Principal>?, socket: Socket?) = identityAlias
        override fun getServerAliases(keyType: String?, issuers: Array<out Principal>?) = if (keyType == "EC") arrayOf(identityAlias) else null
        override fun chooseServerAlias(keyType: String?, issuers: Array<out Principal>?, socket: Socket?) = if (keyType == "EC") identityAlias else null
        override fun chooseEngineServerAlias(keyType: String?, issuers: Array<out Principal>?, engine: SSLEngine?) = if (keyType == "EC") identityAlias else null
        override fun getCertificateChain(alias: String?) = if (alias == identityAlias) arrayOf(certificate) else null
        override fun getPrivateKey(alias: String?) = if (alias == identityAlias) store.getKey(identityAlias, null) as PrivateKey else null
    }
    private class BootstrapTrustManager : X509TrustManager {
        override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
        override fun checkClientTrusted(chain: Array<out X509Certificate>, authType: String) = check(chain)
        override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String) = check(chain)
        private fun check(chain: Array<out X509Certificate>) {
            require(chain.size == 1) { "A single pinned identity is required" }
            val cert = chain.single(); cert.checkValidity()
            val key = cert.publicKey as? ECPublicKey ?: error("P-256 identity required")
            require(key.params.order == BigInteger("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", 16))
            cert.verify(key)
        }
    }
    private fun safeName(name: String) = name.filter { !it.isISOControl() }.take(80).ifBlank { "Windows computer" }
    private fun fingerprint(bytes: ByteArray) = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02X".format(it.toInt() and 255) }
}
