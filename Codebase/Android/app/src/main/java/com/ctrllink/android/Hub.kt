package com.ctrllink.android

import android.app.Application
import com.ctrllink.android.proto.Core
import com.ctrllink.android.proto.PairWindow
import com.ctrllink.android.proto.Peer
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import java.net.InetSocketAddress

class ClipApp : Application() {
    override fun onCreate() {
        super.onCreate()
        Hub.store = Store(this)
    }
}

class Found(val id: String, val name: String, val addr: InetSocketAddress, val pairing: Boolean)
/**
 * A big file moving right now (>= 5 MB), for the Devices screen. [speed] holds (progress 0-1, MB/s) samples, one a
 * second; [rate] is bytes/s (0 until the first sample); [finished] shows 100 % for a moment before the screen leaves.
 */
data class Transfer(
    val clipId: String, val name: String, val peer: String, val sending: Boolean, val done: Long, val total: Long,
    val startedAt: Long, val updatedAt: Long, val speed: List<Pair<Float, Float>> = emptyList(),
    val rate: Double = 0.0, val finished: Boolean = false,
)

/**
 * Live state of the one big transfer on screen. The core reports bytes per chunk ([progress]); the service calls
 * [tick] once a second, which adds a speed sample, so the graph keeps filling while the screen is closed. Thread-safe.
 */
class TransferTracker(private val now: () -> Long = System::currentTimeMillis) {
    val state = MutableStateFlow<Transfer?>(null)
    private var cur: Transfer? = null
    private var lastBytes = 0L
    private var lastAt = 0L
    private var published = 0L

    @Synchronized fun progress(clipId: String, name: String, peer: String, sending: Boolean, done: Long, total: Long) {
        val t = now()
        val c = cur
        if (c?.clipId == clipId) {
            if (c.finished) return
            cur = c.copy(done = done, updatedAt = t)
        } else {
            if (c != null && !c.finished && t - c.updatedAt < STALE_MS) return // another transfer owns the screen
            cur = Transfer(clipId, name, peer, sending, done, total, t, t, listOf(0f to 0f))
            lastBytes = 0
            lastAt = t
            published = Long.MIN_VALUE / 2 // the first chunk always shows
        }
        if (t - published >= 250) publish(t) // at most 4 UI updates a second however fast chunks arrive
    }

    /** One speed sample; returns the transfer for the progress notification, or null when idle. */
    @Synchronized fun tick(): Transfer? {
        val c = cur ?: return null
        val t = now()
        if (c.finished) return c
        if (t - c.updatedAt > 60_000) { cur = null; publish(t); return null } // no end ever came: let it go
        val dt = t - lastAt
        if (dt <= 0) return c
        val bps = (c.done - lastBytes) * 1000.0 / dt
        lastBytes = c.done
        lastAt = t
        val rate = if (c.rate == 0.0) bps else 0.5 * bps + 0.5 * c.rate // smooth the time-left estimate
        cur = c.copy(speed = (c.speed + (c.done.toFloat() / c.total to (bps / 1e6).toFloat())).takeLast(240), rate = rate)
        publish(t)
        return cur
    }

    /** The core says [clipId] stopped. Success shows 100 % until [clear]; failure clears at once. */
    @Synchronized fun end(clipId: String, ok: Boolean): Boolean {
        val c = cur?.takeIf { it.clipId == clipId } ?: return false
        cur = if (ok) c.copy(done = c.total, finished = true, speed = c.speed + (1f to (c.speed.lastOrNull()?.second ?: 0f))) else null
        publish(now())
        return true
    }

    @Synchronized fun clear(clipId: String) {
        if (cur?.clipId == clipId) { cur = null; publish(now()) }
    }

    private fun publish(t: Long) {
        published = t
        state.value = cur
    }

    private companion object { const val STALE_MS = 10_000L }
}

class PairPrompt(val name: String, val sas: String, val answer: CompletableDeferred<Boolean>)

/** Process-wide state shared by the service, the activities and the UI. */
object Hub {
    lateinit var store: Store
    val core = MutableStateFlow<Core?>(null)
    val online = MutableStateFlow(emptySet<String>())
    val found = MutableStateFlow(emptyMap<String, Found>())
    val pairWindow = MutableStateFlow<PairWindow?>(null)
    val pairPrompt = MutableStateFlow<PairPrompt?>(null)
    val transfers = TransferTracker()
    val paired = MutableSharedFlow<Peer>(extraBufferCapacity = 1)

    /** §6.4: fingerprint of the content we last wrote to (or sent from) the clipboard. */
    @Volatile var lastFp: String? = null
}
