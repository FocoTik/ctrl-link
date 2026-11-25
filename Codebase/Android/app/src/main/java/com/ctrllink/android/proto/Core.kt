package com.ctrllink.android.proto

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.ServerSocket
import java.net.Socket
import java.net.URLDecoder
import java.net.URLEncoder
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.coroutines.coroutineContext

const val V = 1
const val APP = "1.0.1"

class Me(val id: String, @Volatile var name: String, val pf: String = "android")
class Peer(val id: String, val name: String, val pf: String, val key: ByteArray)
class FileMeta(val fileId: String, val name: String, val mime: String, val size: Long)
class OutFile(val name: String, val mime: String, val size: Long, val open: () -> InputStream)
class Received(val result: Any?, val meta: FileMeta, val sha256: String)
interface Sink { val out: OutputStream; fun commit(): Any?; fun abort() }
class PairException(val reason: String) : Exception(reason)
class PairWindow(val priv: ByteArray, val pub: ByteArray, val token: ByteArray, val code: String, val expiresAt: Long) {
    @Volatile var fails = 0
}
class Invite(val id: String, val name: String, val pf: String, val hosts: List<String>, val port: Int, val key: ByteArray, val token: ByteArray)

/** What the app plugs into the core: storage, UI prompts and received content. */
interface CoreListener {
    fun peer(id: String): Peer?
    fun savePeer(p: Peer)
    fun removePeer(id: String)
    fun openSink(from: Peer, kind: String, f: FileMeta): Sink
    suspend fun confirmPair(name: String, sas: String): Boolean = false
    suspend fun acceptFiles(from: Peer, clipId: String, files: List<FileMeta>): Boolean = true
    fun onPaired(p: Peer) {}
    fun onPairWindow(w: PairWindow?) {}
    fun onConnected(p: Peer) {}
    fun onDisconnected(p: Peer, sessionMs: Long) {}
    fun onText(from: Peer, kind: String, text: String) {}
    fun onFiles(from: Peer, kind: String, files: List<Received>) {}
    fun onProgress(from: Peer, sending: Boolean, clipId: String, name: String, done: Long, total: Long) {}
    /** A file transfer stopped streaming: [ok] when every byte went out (sending) or every file verified (receiving). */
    fun onTransferEnd(clipId: String, sending: Boolean, ok: Boolean) {}
    fun onUnknown(peerId: String) {}
    fun onUnpaired(p: Peer) {}
    fun onVersion(name: String) {}
}

fun platform(pf: String) = if (pf in setOf("mac", "android", "windows")) pf else "other"

/** §6.3: untrusted names → last path component, no separators/control chars, no leading dots, ≤ 200 bytes. */
fun safeName(name: String): String {
    var n = name.substringAfterLast('/').substringAfterLast('\\')
        .map { if (it in "/\\:" || it.isISOControl()) '_' else it }.joinToString("").trimStart('.')
    while (n.toByteArray().size > 200) n = n.dropLast(1)
    return n.ifEmpty { "file" }
}

/** §6.3 name clashes: "a.txt" → "a (1).txt", "a (2).txt", … */
fun dedup(name: String, taken: (String) -> Boolean): String {
    if (!taken(name)) return name
    val dot = name.lastIndexOf('.').takeIf { it > 0 } ?: name.length
    return generateSequence(1) { it + 1 }.map { "${name.substring(0, dot)} ($it)${name.substring(dot)}" }.first { !taken(it) }
}

fun parseInvite(s: String): Invite? = runCatching {
    if (!s.startsWith("ctrllink://pair?")) return null
    val q = s.substringAfter('?').split('&').associate {
        it.substringBefore('=') to URLDecoder.decode(it.substringAfter('=', ""), "UTF-8")
    }
    if (q["v"]?.toIntOrNull() != V) return null
    Invite(
        q.getValue("id"), q["n"].orEmpty(), platform(q["pf"].orEmpty()),
        q["h"].orEmpty().split(',').filter { it.isNotBlank() }.map { it.trim().removePrefix("[").removeSuffix("]") },
        q.getValue("p").toInt(), b64ud(q.getValue("k")), b64ud(q.getValue("t")),
    ).takeIf { it.key.size == 32 && it.token.size == 16 }
}.getOrNull()

/** The LAN IPv4 addresses for the QR's `h=`. */
fun lanIps(): List<String> = runCatching {
    NetworkInterface.getNetworkInterfaces().toList().filter { it.isUp && !it.isLoopback }
        .flatMap { it.inetAddresses.toList() }.filterIsInstance<Inet4Address>().map { it.hostAddress!! }
}.getOrDefault(emptyList())

/**
 * The address to show for a peer: an IPv4 if there is one, else a routable IPv6 without its %scope. Never loopback or
 * link-local (fe80::), which mean nothing to a person; null then, and the caller shows the platform name.
 */
fun shownIp(vararg candidates: InetAddress?): String? {
    val ok = candidates.filterNotNull().filter { !it.isLoopbackAddress && !it.isLinkLocalAddress && !it.isAnyLocalAddress }
    return ok.firstOrNull { it is Inet4Address }?.hostAddress ?: ok.firstOrNull()?.hostAddress?.substringBefore('%')?.lowercase()
}

/** Protocol v1 peer: TCP listener, pairing (host and joiner), sessions and transfers. No Android APIs. */
class Core(val me: Me, private val l: CoreListener, private val dialDelayMs: Long = 10_000) {
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private lateinit var server: ServerSocket
    val port get() = server.localPort
    val sessions = ConcurrentHashMap<String, Session>()
    private val addrs = ConcurrentHashMap<String, InetSocketAddress>()
    private val dialing: MutableSet<String> = ConcurrentHashMap.newKeySet()
    private val pairing = AtomicBoolean(false)
    @Volatile var window: PairWindow? = null
        private set

    fun start(port: Int = 47823) {
        server = try { ServerSocket(port) } catch (e: IOException) { ServerSocket(0) }
        scope.launch {
            while (isActive) {
                val s = try { server.accept() } catch (e: IOException) { break }
                launch { handle(s) }
            }
        }
    }

    fun stop() {
        runCatching { server.close() }
        sessions.values.forEach { it.close() }
        scope.cancel()
    }

    // ---- discovery / dialing (§5.1) ----

    /** Called for every mDNS sighting (and after pairing). Dials paired peers per the tie-break. */
    fun seen(id: String, addr: InetSocketAddress, now: Boolean = false) {
        if (id == me.id) return
        addrs[id] = addr
        if (l.peer(id) == null || sessions.containsKey(id) || !dialing.add(id)) return
        scope.launch {
            try {
                if (!now && me.id > id) delay(dialDelayMs)
                if (!sessions.containsKey(id)) addrs[id]?.let { dial(id, it) }
            } catch (_: Exception) {
            } finally {
                dialing.remove(id)
            }
        }
    }

    fun redialAll() = addrs.forEach { (id, a) -> seen(id, a) }

    // ---- sending ----

    /** Sends a clip to every connected peer; returns who it went to. Safe to call from any thread. */
    fun sendClip(kind: String, text: String? = null, files: List<OutFile> = emptyList()): List<Peer> {
        val id = UUID.randomUUID().toString()
        val tagged = files.map { it to UUID.randomUUID().toString() }
        val clip = j("t" to "clip", "id" to id, "ts" to System.currentTimeMillis(), "kind" to kind)
        if (text != null) clip.put("text", text)
        if (files.isNotEmpty()) clip.put("files", JSONArray(tagged.map { (f, fid) ->
            j("fileId" to fid, "name" to f.name, "mime" to f.mime, "size" to f.size)
        }))
        val targets = sessions.values.toList()
        scope.launch { targets.forEach { runCatching { it.offer(id, clip, tagged) } } }
        return targets.map { it.peer }
    }

    /** Text or URL; over 192 KiB UTF-8 (or JSON-escaped past one frame) it goes as a .txt file (§6.1). */
    fun sendText(kind: String, text: String): List<Peer> {
        val b = text.toByteArray()
        val clipBytes = j("t" to "clip", "id" to UUID.randomUUID().toString(), "ts" to 0L, "kind" to kind, "text" to text)
            .toString().toByteArray().size
        if (b.size <= 192 * 1024 && 1 + clipBytes + 16 <= MAX_FRAME) return sendClip(kind, text)
        return sendClip("files", files = listOf(OutFile("clipboard.txt", "text/plain", b.size.toLong()) { b.inputStream() }))
    }

    /** Cancels a file transfer (sending or receiving) with every peer. */
    fun cancel(clipId: String) = sessions.values.forEach { s -> scope.launch { runCatching { s.cancel(clipId, tell = true) } } }

    fun unpair(id: String) {
        l.removePeer(id)
        sessions[id]?.let { s -> scope.launch { runCatching { s.control(j("t" to "unpair")) }; s.close() } }
    }

    // ---- pairing, host side (§4) ----

    @Synchronized fun openPairWindow(): PairWindow {
        val priv = Crypto.random(32)
        val w = PairWindow(priv, Crypto.x25519Pub(priv), Crypto.random(16),
            Crypto.rnd.nextInt(1_000_000).toString().padStart(6, '0'), System.currentTimeMillis() + 5 * 60_000)
        window = w
        l.onPairWindow(w)
        scope.launch { delay(5 * 60_000); closePairWindow(w) }
        return w
    }

    @Synchronized fun closePairWindow(w: PairWindow? = window) {
        if (w != null && window === w) {
            window = null
            l.onPairWindow(null)
        }
    }

    fun pairUri(w: PairWindow, hosts: List<String>): String {
        fun enc(s: String) = URLEncoder.encode(s, "UTF-8").replace("+", "%20")
        val h = hosts.joinToString(",") { enc(if (':' in it) "[$it]" else it) }
        return "ctrllink://pair?v=1&id=${me.id}&n=${enc(me.name)}&pf=${me.pf}&h=$h&p=$port&k=${b64u(w.pub)}&t=${b64u(w.token)}"
    }

    private suspend fun hostPair(w: Wire, req: JSONObject) {
        val win = window ?: return fail(w, "closed")
        if (!pairing.compareAndSet(false, true)) return fail(w, "busy")
        try {
            val mode = req.getString("mode")
            val joinerPub = b64d(req.getString("pub"))
            w.writeJson(j("t" to "pair_hello", "v" to V, "id" to me.id, "name" to me.name, "pf" to me.pf, "pub" to b64(win.pub)))
            val k = Crypto.pairingKeys(Crypto.x25519Shared(win.priv, joinerPub),
                if (mode == "qr") win.token else win.code.toByteArray(), win.pub, joinerPub)
            val conf = w.readJson()
            if (conf.optString("t") != "pair_confirm" ||
                !MessageDigest.isEqual(runCatching { b64d(conf.getString("mac")) }.getOrNull(), k.joinerMac)) {
                if (++win.fails >= 5) closePairWindow(win)
                return fail(w, "bad_code")
            }
            if (mode != "qr") {
                val ok = withTimeoutOrNull(60_000) { l.confirmPair(req.optString("name"), k.sas) }
                    ?: return fail(w, "timeout")
                if (!ok) return fail(w, "rejected")
            }
            if (window !== win) return fail(w, "closed")
            val peer = Peer(req.getString("id"), req.optString("name"), platform(req.optString("pf")), k.pairKey)
            l.savePeer(peer)
            w.writeJson(j("t" to "pair_ok", "mac" to b64(k.hostMac)))
            closePairWindow(win)
            l.onPaired(peer)
        } finally {
            pairing.set(false)
        }
    }

    private fun fail(w: Wire, reason: String) = w.writeJson(j("t" to "pair_fail", "reason" to reason))

    // ---- pairing, joiner side ----

    suspend fun join(
        targets: List<InetSocketAddress>, mode: String, secret: ByteArray,
        expectKey: ByteArray? = null, onSas: (String) -> Unit = {},
    ): Peer = withContext(Dispatchers.IO) {
        val s = targets.firstNotNullOfOrNull { a -> runCatching { Socket().apply { connect(a, 3000) } }.getOrNull() }
            ?: throw PairException("unreachable")
        val h = coroutineContext[Job]!!.invokeOnCompletion { s.close() } // unblocks reads if the UI gives up
        try {
            Wire(s).use { w ->
                s.soTimeout = 90_000
                val priv = Crypto.random(32)
                val pub = Crypto.x25519Pub(priv)
                w.writeJson(j("t" to "pair_req", "v" to V, "mode" to mode, "id" to me.id, "name" to me.name, "pf" to me.pf, "pub" to b64(pub)))
                val hello = w.readJson()
                if (hello.optString("t") == "error") throw PairException(hello.optString("reason", "closed"))
                if (hello.optString("t") != "pair_hello") throw PairException(hello.optString("reason", "closed"))
                if (hello.optInt("v", 1) > V) throw PairException("version")
                val hostPub = b64d(hello.getString("pub"))
                if (expectKey != null && !MessageDigest.isEqual(hostPub, expectKey)) throw PairException("key_mismatch")
                val k = Crypto.pairingKeys(Crypto.x25519Shared(priv, hostPub), secret, hostPub, pub)
                if (mode != "qr") onSas(k.sas)
                w.writeJson(j("t" to "pair_confirm", "mac" to b64(k.joinerMac)))
                val ok = w.readJson()
                if (ok.optString("t") != "pair_ok") throw PairException(ok.optString("reason", "closed"))
                if (!MessageDigest.isEqual(runCatching { b64d(ok.getString("mac")) }.getOrNull(), k.hostMac)) throw PairException("bad_mac")
                val peer = Peer(hello.getString("id"), hello.optString("name"), platform(hello.optString("pf")), k.pairKey)
                l.savePeer(peer)
                seen(peer.id, InetSocketAddress(s.inetAddress, s.port), now = true) // §4.2: joiner dials right away
                peer
            }
        } finally {
            h.dispose()
        }
    }

    // ---- sessions (§5) ----

    private suspend fun handle(s: Socket) {
        val w = Wire(s)
        try {
            s.soTimeout = 30_000
            val m = w.readJson()
            val t = m.optString("t")
            if (m.optInt("v", 1) > V) {
                w.writeJson(j("t" to "error", "reason" to "version"))
                l.onVersion(l.peer(m.optString("id"))?.name ?: m.optString("name", "the other device"))
                return
            }
            when (t) {
                "pair_req" -> hostPair(w, m)
                "hello" -> {
                    val p = l.peer(m.optString("id")) ?: return w.writeJson(j("t" to "unknown"))
                    val sn = Crypto.random(32)
                    w.writeJson(j("t" to "hello", "v" to V, "id" to me.id, "nonce" to b64(sn)))
                    val (c2s, s2c) = Crypto.sessionKeys(p.key, b64d(m.getString("nonce")), sn)
                    w.secure(send = s2c, recv = c2s, client = false)
                    run(w, p, clientId = p.id)
                }
            }
        } catch (_: Exception) {
        } finally {
            w.close()
        }
    }

    private fun dial(id: String, addr: InetSocketAddress) {
        val p = l.peer(id) ?: return
        Socket().use { s ->
            s.connect(addr, 5000)
            s.soTimeout = 30_000
            val w = Wire(s)
            val cn = Crypto.random(32)
            w.writeJson(j("t" to "hello", "v" to V, "id" to me.id, "nonce" to b64(cn)))
            val h = w.readJson()
            when (h.optString("t")) {
                "unknown" -> return l.onUnknown(id)
                "error" -> return if (h.optString("reason") == "version") l.onVersion(p.name) else Unit
                "hello" -> {}
                else -> return
            }
            if (h.optInt("v", 1) > V) {
                w.writeJson(j("t" to "error", "reason" to "version"))
                return l.onVersion(p.name)
            }
            if (h.optString("id") != id) return // §5.2: not the peer we meant to dial
            val (c2s, s2c) = Crypto.sessionKeys(p.key, cn, b64d(h.getString("nonce")))
            w.secure(send = c2s, recv = s2c, client = true)
            run(w, p, clientId = me.id)
        }
    }

    private fun run(w: Wire, p0: Peer, clientId: String) {
        val ses = Session(this, w, p0, clientId, l)
        ses.control(j("t" to "ready", "name" to me.name, "pf" to me.pf, "app" to APP))
        val r = ses.recvControl()
        if (r.optString("t") != "ready") return
        val name = r.optString("name")
        if (name.isNotEmpty() && name != p0.name) ses.peer = Peer(p0.id, name, platform(r.optString("pf", p0.pf)), p0.key).also(l::savePeer)
        w.socket.soTimeout = 0 // the session's own ping timer takes over
        var keep = true
        var replaced = false
        sessions.compute(p0.id) { _, old ->
            when {
                old == null -> ses
                ses.clientId <= old.clientId -> { replaced = true; old.close(); ses } // lower client wins; same client = newest
                else -> { keep = false; old }
            }
        }
        if (!keep) return
        if (!replaced) l.onConnected(ses.peer)
        try {
            ses.loop()
        } catch (_: Exception) {
        } finally {
            ses.close()
            if (sessions.remove(p0.id, ses)) {
                l.onDisconnected(ses.peer, System.currentTimeMillis() - ses.started)
                if (scope.isActive) scope.launch { delay(3000); addrs[p0.id]?.let { seen(p0.id, it) } }
            }
        }
    }
}
