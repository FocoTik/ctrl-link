package com.ctrllink.android.proto

import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.io.IOException
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap

/** One established, encrypted session (§5–§6). [loop] blocks on the reader until the socket closes. */
class Session(private val core: Core, private val w: Wire, @Volatile var peer: Peer, val clientId: String, private val l: CoreListener) {
    val started = System.currentTimeMillis()
    /** The peer's address on this connection, for the Devices graph. */
    val remoteAddr: java.net.InetAddress? get() = w.socket.inetAddress
    @Volatile private var lastRecv = started
    @Volatile private var closed = false
    private var ticker: Job? = null

    private class Outgoing(val files: List<Pair<OutFile, String>>) { @Volatile var job: Job? = null }
    private class InClip(val id: String, val kind: String, val total: Long, val results: Array<Received?>) {
        var left = results.size
        var got = 0L
    }
    private class Rx(val clip: InClip, val idx: Int, val meta: FileMeta, val sink: Sink) {
        val md: MessageDigest = Crypto.sha256()
        var got = 0L
    }

    private val outgoing = ConcurrentHashMap<String, Outgoing>() // clipId → files offered or streaming
    private val rx = ConcurrentHashMap<String, Rx>()              // fileId → file being received
    private val cancelled: MutableSet<String> = ConcurrentHashMap.newKeySet() // fileIds whose late chunks we drop

    fun control(o: JSONObject) = w.send(byteArrayOf(1) + o.toString().toByteArray())

    fun recvControl(): JSONObject {
        val p = w.recv()
        if (p[0].toInt() != 1) throw IOException("expected a control message")
        return JSONObject(String(p, 1, p.size - 1, Charsets.UTF_8))
    }

    fun loop() {
        ticker = core.scope.launch {
            while (isActive) {
                delay(5000)
                val quiet = System.currentTimeMillis() - lastRecv
                if (quiet > 45_000) close() else if (quiet > 15_000) runCatching { control(j("t" to "ping")) }
            }
        }
        while (true) {
            val p = w.recv()
            lastRecv = System.currentTimeMillis()
            when (p[0].toInt()) {
                1 -> onControl(JSONObject(String(p, 1, p.size - 1, Charsets.UTF_8)))
                2 -> onChunk(p)
                else -> throw IOException("unknown frame kind")
            }
        }
    }

    fun close() {
        if (closed) return
        closed = true
        ticker?.cancel()
        runCatching { w.close() }
        rx.values.forEach(::discard) // §6.2.5: no resume, delete partials
        rx.values.map { it.clip.id }.distinct().forEach { l.onTransferEnd(it, sending = false, ok = false) }
        rx.clear()
        outgoing.clear()
    }

    private fun discard(r: Rx) {
        runCatching { r.sink.out.close() }
        runCatching { r.sink.abort() }
    }

    private fun onControl(m: JSONObject) {
        val id = m.optString("id")
        when (m.optString("t")) {
            "ping" -> control(j("t" to "pong"))
            "clip" -> onClip(m)
            "accept" -> outgoing[id]?.let { o -> if (o.job == null) o.job = core.scope.launch { stream(id, o) } }
            "reject" -> outgoing.remove(id)
            "done" -> onDone(m)
            "cancel" -> cancel(id, tell = false)
            "unpair" -> {
                l.removePeer(peer.id)
                l.onUnpaired(peer)
                throw IOException("unpaired")
            }
            "error" -> {
                if (m.optString("reason") == "version") l.onVersion(peer.name)
                throw IOException("peer error")
            }
        }
    }

    // ---- receiving ----

    private fun onClip(m: JSONObject) {
        val id = m.getString("id")
        var kind = m.getString("kind")
        if (kind == "text" || kind == "url") return l.onText(peer, kind, m.getString("text"))
        if (kind != "image" && kind != "files") return
        val arr = m.getJSONArray("files")
        val metas = (0 until arr.length()).map { i ->
            arr.getJSONObject(i).let {
                FileMeta(it.getString("fileId").lowercase(), safeName(it.getString("name")),
                    it.optString("mime", "application/octet-stream"), it.getLong("size").coerceAtLeast(0))
            }
        }
        if (metas.isEmpty()) return
        if (kind == "image" && metas.size != 1) kind = "files"
        core.scope.launch {
            val ok = runCatching { l.acceptFiles(peer, id, metas) }.getOrDefault(false)
            if (closed) return@launch
            if (!ok) {
                runCatching { control(j("t" to "reject", "id" to id)) }
                return@launch
            }
            val clip = InClip(id, kind, metas.sumOf { it.size }, arrayOfNulls(metas.size))
            try {
                metas.forEachIndexed { i, f -> rx[f.fileId] = Rx(clip, i, f, l.openSink(peer, kind, f)) }
            } catch (e: Exception) {
                    return@launch cancel(id, tell = true)
            }
            if (closed) rx.values.forEach(::discard) else runCatching { control(j("t" to "accept", "id" to id)) }
        }
    }

    private fun onChunk(p: ByteArray) {
        if (p.size < 17) throw IOException("short chunk")
        val fid = uuidAt(p, 1)
        val r = rx[fid]
        if (r == null) {
            if (fid in cancelled) return // in flight when we cancelled
            throw IOException("chunk for unknown file") // no clip id to cancel; see report
        }
        val n = p.size - 17
        if (r.got + n > r.meta.size) return cancel(r.clip.id, tell = true)
        r.sink.out.write(p, 17, n)
        r.md.update(p, 17, n)
        r.got += n
        r.clip.got += n
        l.onProgress(peer, false, r.clip.id, r.meta.name, r.clip.got, r.clip.total)
    }

    private fun onDone(m: JSONObject) {
        val r = rx.remove(m.optString("fileId").lowercase()) ?: return
        val sha = hex(r.md.digest())
        r.sink.out.close()
        if (!sha.equals(m.optString("sha256"), ignoreCase = true)) {
            r.sink.abort()
            l.onTransferEnd(r.clip.id, sending = false, ok = false) // this file already left [rx], so cancel() may not report it
            return cancel(r.clip.id, tell = true)
        }
        r.clip.results[r.idx] = Received(r.sink.commit(), r.meta, sha)
        if (--r.clip.left == 0) {
            l.onTransferEnd(r.clip.id, sending = false, ok = true)
            l.onFiles(peer, r.clip.kind, r.clip.results.map { it!! })
        }
    }

    /** Stops clip [id] in either direction; [tell] sends `cancel` to the peer. */
    fun cancel(id: String, tell: Boolean) {
        val receiving = rx.entries.filter { it.value.clip.id == id }
        receiving.forEach { (fid, r) ->
            rx.remove(fid)
            cancelled += fid
            discard(r)
        }
        if (receiving.isNotEmpty()) l.onTransferEnd(id, sending = false, ok = false)
        outgoing.remove(id)?.job?.cancel()
        if (tell) runCatching { control(j("t" to "cancel", "id" to id)) }
    }

    // ---- sending ----

    fun offer(id: String, clip: JSONObject, files: List<Pair<OutFile, String>>) {
        if (files.isNotEmpty()) {
            val o = Outgoing(files)
            outgoing[id] = o
            core.scope.launch {
                delay(5 * 60_000)
                if (o.job == null && outgoing.remove(id, o)) runCatching { control(j("t" to "cancel", "id" to id)) }
            }
        }
        control(clip)
    }

    private fun stream(id: String, o: Outgoing) {
        val total = o.files.sumOf { it.first.size }
        var sent = 0L
        var ok = false
        try {
            for ((f, fid) in o.files) {
                val buf = ByteArray(17 + CHUNK)
                buf[0] = 2
                uuidBytes(fid).copyInto(buf, 1)
                val md = Crypto.sha256()
                f.open().use { input ->
                    while (true) {
                        val n = input.read(buf, 17, CHUNK)
                        if (n < 0) break
                        if (outgoing[id] !== o) return // cancelled or session closed
                        md.update(buf, 17, n)
                        w.send(buf, 17 + n)
                        sent += n
                        l.onProgress(peer, true, id, f.name, sent, total)
                    }
                }
                control(j("t" to "done", "id" to id, "fileId" to fid, "sha256" to hex(md.digest())))
            }
            ok = true
        } catch (e: Exception) {
            if (!closed) runCatching { control(j("t" to "cancel", "id" to id)) }
        } finally {
            outgoing.remove(id, o)
            l.onTransferEnd(id, sending = true, ok = ok)
        }
    }
}
