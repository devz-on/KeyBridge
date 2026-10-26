package dev.keybridge.android

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.content.pm.PackageManager
import android.os.IBinder
import androidx.core.app.NotificationCompat
import kotlinx.coroutines.launch

class BridgeService : Service() {
    private val runtime get() = (application as KeyBridgeApplication).runtime
    private var transport: PhoneTransport? = null
    override fun onCreate() {
        super.onCreate()
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(NotificationChannel("bridge", "ADB bridge connection", NotificationManager.IMPORTANCE_LOW))
        manager.createNotificationChannel(NotificationChannel("approval", "Authentication approval", NotificationManager.IMPORTANCE_HIGH))
        runtime.service = this
    }
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == "STOP") { stopSelf(); return START_NOT_STICKY }
        startForeground(1, notification("KeyBridge connection active", "Open KeyBridge to approve pairing and authentication.", "bridge"))
        if (transport == null) {
            val server = PhoneTransport(runtime)
            transport = server; runtime.transport = server
            runtime.scope.launch {
                try { runtime.initialized.await(); server.run() }
                catch (_: kotlinx.coroutines.CancellationException) { }
                catch (e: Exception) { runtime.error("Bridge stopped: ${e.javaClass.simpleName}. Tap Start bridge to retry."); stopSelf() }
            }
        }
        return START_NOT_STICKY
    }
    private fun notification(title: String, text: String, channel: String): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getService(this, 1, Intent(this, BridgeService::class.java).setAction("STOP"), PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        return NotificationCompat.Builder(this, channel).setSmallIcon(R.drawable.ic_keybridge)
            .setContentTitle(title).setContentText(text).setContentIntent(open).setOngoing(channel == "bridge")
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE).addAction(0, "Stop bridge", stop).build()
    }
    fun showApprovalNotification() {
        if (runtime.foreground || checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED || !runtime.settings.getBoolean("notifications", true)) return
        getSystemService(NotificationManager::class.java).notify(2, notification("KeyBridge needs your approval", "Open the app to review the request. Nothing is approved automatically.", "approval"))
    }
    override fun onDestroy() {
        // Closing the transport cancels its network approvals; local vault maintenance has its own scope.
        transport?.close(); transport = null; runtime.transport = null; runtime.service = null
        runtime.update { it.copy(running = false, connection = "Disconnected", computer = "No computer connected", paired = false) }
        getSystemService(NotificationManager::class.java).cancel(2)
        super.onDestroy()
    }
    override fun onBind(intent: Intent?): IBinder? = null
}
