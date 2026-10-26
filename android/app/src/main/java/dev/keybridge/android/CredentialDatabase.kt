package dev.keybridge.android

import android.content.ContentValues
import android.content.Context
import android.database.Cursor
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import dev.keybridge.core.Credential
import dev.keybridge.core.CredentialRepository
import dev.keybridge.core.CtapFailure
import java.io.ByteArrayOutputStream
import java.io.DataOutputStream
import java.security.KeyStore
import java.security.MessageDigest
import javax.crypto.KeyGenerator
import javax.crypto.Mac
import javax.crypto.SecretKey

/** Private app database, excluded from Android backup. Each row is bound to a non-exportable MAC key. */
class CredentialDatabase(context: Context, databaseName: String = "keybridge.db") : SQLiteOpenHelper(context, databaseName, null, 1), CredentialRepository {
    private val macKey: SecretKey
    init {
        setWriteAheadLoggingEnabled(true)
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val alias = "keybridge.metadata.integrity.v1"
        if (!store.containsAlias(alias)) {
            KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_HMAC_SHA256, "AndroidKeyStore").apply {
                init(KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY)
                    .setDigests(KeyProperties.DIGEST_SHA256).build())
            }.generateKey()
        }
        macKey = store.getKey(alias, null) as SecretKey
    }
    override fun onConfigure(db: SQLiteDatabase) { db.setForeignKeyConstraintsEnabled(true) }
    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL("""CREATE TABLE credentials(id BLOB PRIMARY KEY NOT NULL,rp TEXT NOT NULL,uid BLOB NOT NULL,
            uname TEXT NOT NULL,display TEXT NOT NULL,alias TEXT UNIQUE NOT NULL,x BLOB NOT NULL,y BLOB NOT NULL,
            resident INTEGER NOT NULL,created INTEGER NOT NULL,used INTEGER NOT NULL,counter INTEGER NOT NULL,
            security TEXT NOT NULL,mac BLOB NOT NULL)""")
        db.execSQL("CREATE TABLE activity(id INTEGER PRIMARY KEY AUTOINCREMENT,at INTEGER NOT NULL,operation TEXT NOT NULL,detail TEXT NOT NULL)")
    }
    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) = error("Unsupported database version; do not reset existing credentials")
    private fun mac(c: Credential): ByteArray {
        val bytes = ByteArrayOutputStream()
        DataOutputStream(bytes).use { out ->
            fun blob(b: ByteArray) { out.writeInt(b.size); out.write(b) }
            blob(c.id); out.writeUTF(c.rpId); blob(c.userId); out.writeUTF(c.userName); out.writeUTF(c.displayName); out.writeUTF(c.alias)
            blob(c.x); blob(c.y); out.writeBoolean(c.discoverable); out.writeLong(c.createdAt); out.writeLong(c.lastUsedAt); out.writeLong(c.counter); out.writeUTF(c.security)
        }
        return Mac.getInstance("HmacSHA256").apply { init(macKey) }.doFinal(bytes.toByteArray())
    }
    private fun row(cursor: Cursor): Credential {
        fun b(name: String) = cursor.getBlob(cursor.getColumnIndexOrThrow(name))
        fun s(name: String) = cursor.getString(cursor.getColumnIndexOrThrow(name))
        fun l(name: String) = cursor.getLong(cursor.getColumnIndexOrThrow(name))
        val c = Credential(b("id"), s("rp"), b("uid"), s("uname"), s("display"), s("alias"), b("x"), b("y"), l("resident") == 1L,
            l("created"), l("used"), l("counter"), s("security"))
        check(MessageDigest.isEqual(mac(c), b("mac"))) { "Credential metadata integrity check failed. Authentication is blocked." }
        return c
    }
    private fun values(c: Credential) = ContentValues().apply {
        put("id", c.id); put("rp", c.rpId); put("uid", c.userId); put("uname", c.userName); put("display", c.displayName)
        put("alias", c.alias); put("x", c.x); put("y", c.y); put("resident", if (c.discoverable) 1 else 0)
        put("created", c.createdAt); put("used", c.lastUsedAt); put("counter", c.counter); put("security", c.security); put("mac", mac(c))
    }
    @Synchronized override fun all(): List<Credential> = readableDatabase.query("credentials", null, null, null, null, null, "created DESC").use { cursor ->
        buildList { while (cursor.moveToNext()) add(row(cursor)) }
    }
    @Synchronized override fun insert(credential: Credential) { writableDatabase.insertOrThrow("credentials", null, values(credential)) }
    @Synchronized override fun reserveCounter(id: ByteArray): Long {
        val db = writableDatabase
        db.beginTransaction()
        try {
            val c = all().firstOrNull { it.id.contentEquals(id) } ?: throw CtapFailure(0x2e)
            if (c.counter >= 0xffff_ffffL) throw CtapFailure(0x7f)
            val next = c.copy(counter = c.counter + 1)
            check(db.update("credentials", values(next), "alias=?", arrayOf(c.alias)) == 1)
            db.setTransactionSuccessful(); return next.counter
        } finally { db.endTransaction() }
    }
    @Synchronized override fun used(id: ByteArray, at: Long) {
        val c = all().firstOrNull { it.id.contentEquals(id) } ?: throw CtapFailure(0x2e)
        check(writableDatabase.update("credentials", values(c.copy(lastUsedAt = at)), "alias=?", arrayOf(c.alias)) == 1)
    }
    @Synchronized override fun delete(id: ByteArray) {
        val c = all().firstOrNull { it.id.contentEquals(id) } ?: return
        writableDatabase.delete("credentials", "alias=?", arrayOf(c.alias))
    }
    @Synchronized fun event(operation: String, detail: String) {
        writableDatabase.insertOrThrow("activity", null, ContentValues().apply { put("at", System.currentTimeMillis()); put("operation", operation.take(80)); put("detail", detail.take(256)) })
        writableDatabase.execSQL("DELETE FROM activity WHERE id NOT IN (SELECT id FROM activity ORDER BY id DESC LIMIT 200)")
    }
    @Synchronized fun history(): List<ActivityEntry> = readableDatabase.query("activity", null, null, null, null, null, "id DESC", "200").use { c ->
        buildList { while (c.moveToNext()) add(ActivityEntry(c.getLong(c.getColumnIndexOrThrow("at")), c.getString(c.getColumnIndexOrThrow("operation")), c.getString(c.getColumnIndexOrThrow("detail")))) }
    }
}
data class ActivityEntry(val at: Long, val operation: String, val detail: String)
