package com.ctrllink.android

import android.content.Context
import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.core.booleanPreferencesKey
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import com.ctrllink.android.proto.Me
import com.ctrllink.android.proto.Peer
import com.ctrllink.android.proto.b64
import com.ctrllink.android.proto.b64d
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import org.json.JSONArray
import org.json.JSONObject
import java.security.KeyStore
import java.time.LocalDate
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

private val Context.dataStore by preferencesDataStore("ctrllink")

/** [sound]: incoming clips and notifications play the system sound. [welcomed]: the first-run Welcome was seen. */
data class Settings(val name: String, val autoSend: Boolean, val limitMb: Int, val boot: Boolean, val sound: Boolean, val welcomed: Boolean)
data class HistoryItem(val kind: String, val text: String?, val uri: String?, val label: String, val peer: String, val sent: Boolean, val ts: Long)

/**
 * Identity, settings, pairings (pairKey sealed with an Android Keystore key), per-day counts, and the clip list
 * (history.json: 14 days, at most 300 clips, text capped at 32 KB each; the same limits as the Mac).
 */
class Store(ctx: Context) {
    private val ds = ctx.dataStore
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    /** Received images live here; they are also the History thumbnails. */
    private val receivedDir = java.io.File(ctx.filesDir, "received")
    private val historyFile = java.io.File(ctx.filesDir, "history.json")
    private val historyLock = Any()
    private val defaultName = android.provider.Settings.Global.getString(ctx.contentResolver, "device_name") ?: Build.MODEL
    val me: Me
    val settings: StateFlow<Settings>
    val peers = MutableStateFlow<Map<String, Peer>>(emptyMap())
    val history = MutableStateFlow<List<HistoryItem>>(emptyList())
    /** Clips per day ("2026-11-08") per kind, last 14 days, for the History chart and today's count. */
    val stats = MutableStateFlow<Map<String, Map<String, Int>>>(emptyMap())

    init {
        val p = runBlocking { ds.data.first() }
        val id = p[ID] ?: UUID.randomUUID().toString().also { id -> runBlocking { ds.edit { it[ID] = id } } }
        val s0 = p.settings()
        me = Me(id, s0.name)
        settings = ds.data.map { it.settings() }.stateIn(scope, SharingStarted.Eagerly, s0)
        peers.value = p[PEERS]?.let { s ->
            val a = JSONArray(s)
            (0 until a.length()).mapNotNull { i ->
                val o = a.getJSONObject(i)
                runCatching { Peer(o.getString("id"), o.getString("name"), o.getString("pf"), Vault.open(o.getString("key"))) }.getOrNull()
            }.associateBy { it.id }
        }.orEmpty()
        // The list can be a few MB, so it loads off the main thread; clips added meanwhile stay in front.
        scope.launch {
            val saved = synchronized(historyLock) { runCatching { parseHistory(historyFile.readText()) }.getOrNull() }
                ?: p[HISTORY]?.let { runCatching { parseHistory(it) }.getOrNull() } // pre-file installs kept 20 in prefs
                ?: emptyList()
            history.update { trim(it + saved) }
            if (p[HISTORY] != null) { saveHistory(); ds.edit { it.remove(HISTORY) } }
        }
        stats.value = p[STATS]?.let { s ->
            val o = JSONObject(s)
            o.keys().asSequence().associateWith { d -> o.getJSONObject(d).let { k -> k.keys().asSequence().associateWith(k::getInt) } }
        }.orEmpty()
    }

    // Installs from before the Welcome screen that already paired skip it.
    private fun Preferences.settings() = Settings(this[NAME] ?: defaultName, this[AUTO_SEND] ?: true, this[LIMIT_MB] ?: 100, this[BOOT] ?: true,
        this[SOUND] ?: true, this[WELCOMED] ?: (this[PEERS] != null))

    fun setName(n: String) { me.name = n; put(NAME, n) }
    fun setAutoSend(b: Boolean) = put(AUTO_SEND, b)
    fun setLimitMb(mb: Int) = put(LIMIT_MB, mb)
    fun setBoot(b: Boolean) = put(BOOT, b)
    fun setSound(b: Boolean) = put(SOUND, b)
    fun setWelcomed() = put(WELCOMED, true)
    private fun <T> put(k: Preferences.Key<T>, v: T) = scope.launch { ds.edit { it[k] = v } }

    fun savePeer(p: Peer) { peers.update { it + (p.id to p) }; savePeers() }
    fun removePeer(id: String) { peers.update { it - id }; savePeers() }
    private fun savePeers() {
        val json = JSONArray(peers.value.values.map {
            JSONObject().put("id", it.id).put("name", it.name).put("pf", it.pf).put("key", Vault.seal(it.key))
        }).toString()
        put(PEERS, json)
    }

    fun addHistory(h: HistoryItem) {
        val today = LocalDate.now().toString()
        val keep = LocalDate.now().minusDays(14).toString()
        val st = stats.updateAndGet { m ->
            val day = m[today].orEmpty()
            (m + (today to day + (h.kind to (day[h.kind] ?: 0) + 1))).filterKeys { it > keep }
        }
        put(STATS, JSONObject(st.mapValues { JSONObject(it.value) }).toString())
        history.update { trim(listOf(h.capped()) + it) }
        scope.launch { saveHistory() }
    }

    private fun HistoryItem.capped(): HistoryItem {
        val t = text ?: return this
        if (t.length <= TEXT_CAP / 4 || t.toByteArray().size <= TEXT_CAP) return this
        var n = minOf(t.length, TEXT_CAP)
        while (t.substring(0, n).toByteArray().size > TEXT_CAP) n -= maxOf(1, (n / 16))
        if (n > 0 && t[n - 1].isHighSurrogate()) n--
        return copy(text = t.substring(0, n))
    }

    private fun trim(list: List<HistoryItem>): List<HistoryItem> {
        val oldest = System.currentTimeMillis() - 14 * 86_400_000L
        return list.filter { it.ts >= oldest }.sortedByDescending { it.ts }.take(300)
    }

    private fun parseHistory(s: String): List<HistoryItem> {
        val a = JSONArray(s)
        return (0 until a.length()).map { i ->
            val o = a.getJSONObject(i)
            HistoryItem(o.optString("kind", "text"), o.optString("text").ifEmpty { null }, o.optString("uri").ifEmpty { null }, o.getString("label"),
                o.getString("peer"), o.getBoolean("sent"), o.getLong("ts"))
        }
    }

    /** Writes the whole list to a temp file and renames it over the old one, so a crash never leaves half a file. */
    private fun saveHistory() = synchronized(historyLock) {
        val json = JSONArray(history.value.map {
            JSONObject().put("kind", it.kind).put("text", it.text).put("uri", it.uri).put("label", it.label).put("peer", it.peer).put("sent", it.sent).put("ts", it.ts)
        }).toString()
        val tmp = java.io.File(historyFile.parentFile, "history.json.tmp")
        runCatching { tmp.writeText(json); tmp.renameTo(historyFile) }
    }

    /** Forgets the clip list and the per-day counts, and deletes received images (the thumbnails). */
    fun clearHistory() {
        history.value = emptyList()
        stats.value = emptyMap()
        scope.launch {
            ds.edit { it.remove(HISTORY); it.remove(STATS) }
            synchronized(historyLock) { historyFile.delete() }
            receivedDir.deleteRecursively()
        }
    }

    private fun <T> MutableStateFlow<T>.updateAndGet(f: (T) -> T): T { update(f); return value }

    private companion object {
        val ID = stringPreferencesKey("id")
        val NAME = stringPreferencesKey("name")
        val AUTO_SEND = booleanPreferencesKey("autoSend")
        val LIMIT_MB = intPreferencesKey("limitMb")
        val BOOT = booleanPreferencesKey("boot")
        val SOUND = booleanPreferencesKey("sound")
        val WELCOMED = booleanPreferencesKey("welcomed")
        val PEERS = stringPreferencesKey("peers")
        val HISTORY = stringPreferencesKey("history") // before history.json; migrated once
        const val TEXT_CAP = 32 * 1024
        val STATS = stringPreferencesKey("stats")
    }
}

/** AES-GCM with a non-exportable Android Keystore key; output is iv(12) || ciphertext, base64. */
private object Vault {
    private const val ALIAS = "ctrllink_pairkeys"

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(ALIAS, null) as SecretKey?)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").apply {
            init(KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .build())
        }.generateKey()
    }

    fun seal(b: ByteArray): String {
        val c = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, key()) }
        return b64(c.iv + c.doFinal(b))
    }

    fun open(s: String): ByteArray {
        val d = b64d(s)
        val c = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, d, 0, 12)) }
        return c.doFinal(d, 12, d.size - 12)
    }
}
