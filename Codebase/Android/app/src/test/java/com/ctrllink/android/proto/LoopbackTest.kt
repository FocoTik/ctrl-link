package com.ctrllink.android.proto

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import java.io.File
import java.net.InetSocketAddress
import java.util.concurrent.ConcurrentHashMap

/** Two (or three) in-process cores on 127.0.0.1: QR + code pairing, then a session with a text clip and a ~5 MB file. */
class LoopbackTest {
    open class L(private val confirm: Boolean = true) : CoreListener {
        val peers = ConcurrentHashMap<String, Peer>()
        val texts = Channel<String>(Channel.UNLIMITED)
        val files = Channel<List<Received>>(Channel.UNLIMITED)
        val connected = Channel<String>(Channel.UNLIMITED)
        val hostSas = CompletableDeferred<String>()
        override fun peer(id: String) = peers[id]
        override fun savePeer(p: Peer) { peers[p.id] = p }
        override fun removePeer(id: String) { peers.remove(id) }
        override suspend fun confirmPair(name: String, sas: String) = confirm.also { hostSas.complete(sas) }
        override fun onConnected(p: Peer) { connected.trySend(p.id) }
        override fun onText(from: Peer, kind: String, text: String) { texts.trySend("$kind:$text") }
        override fun onFiles(from: Peer, kind: String, files: List<Received>) { this.files.trySend(files) }
        override fun openSink(from: Peer, kind: String, f: FileMeta): Sink = object : Sink {
            val file: File = File.createTempFile("ctrllink-rx", f.name)
            override val out = file.outputStream()
            override fun commit() = file
            override fun abort() { file.delete() }
        }
    }

    private val cores = mutableListOf<Core>()
    private fun core(id: String, name: String, l: L) = Core(Me(id, name), l).also { it.start(0); cores += it }
    private fun local(c: Core) = InetSocketAddress("127.0.0.1", c.port)

    @After fun stop() = cores.forEach { it.stop() }

    @Test fun pairAndTransfer() = runBlocking {
        withTimeout(60_000) {
            val la = L(); val lb = L()
            val a = core("00000000-0000-4000-8000-00000000000a", "Phone A", la)
            val b = core("00000000-0000-4000-8000-00000000000b", "Mac B", lb)

            // QR pairing: B hosts, A joins with the parsed URI.
            val win = b.openPairWindow()
            val inv = parseInvite(b.pairUri(win, listOf("127.0.0.1", "fe80::1%en0")))!!
            assertEquals(listOf("127.0.0.1", "fe80::1%en0"), inv.hosts)
            assertEquals("Mac B", inv.name)
            val peer = a.join(listOf(InetSocketAddress(inv.hosts[0], inv.port)), "qr", inv.token, inv.key)
            assertEquals(b.me.id, peer.id)
            assertArrayEquals(la.peers[b.me.id]!!.key, lb.peers[a.me.id]!!.key)
            assertEquals(null, b.window) // closed after one success

            // The joiner dials the session right after pairing.
            assertEquals(b.me.id, la.connected.receive())
            assertEquals(a.me.id, lb.connected.receive())

            a.sendText("text", "hello 👋")
            assertEquals("text:hello 👋", lb.texts.receive())
            b.sendText("url", "https://example.com/a?b=c")
            assertEquals("url:https://example.com/a?b=c", la.texts.receive())

            val data = ByteArray(5 * 1024 * 1024 + 123).also { Crypto.rnd.nextBytes(it) }
            a.sendClip("files", files = listOf(OutFile("big.bin", "application/octet-stream", data.size.toLong()) { data.inputStream() }))
            val got = lb.files.receive().single()
            assertEquals(hex(Crypto.sha256(data)), got.sha256)
            assertEquals(hex(Crypto.sha256(data)), hex(Crypto.sha256((got.result as File).readBytes())))
            assertEquals("big.bin", got.meta.name)

            // Text over one frame goes as a file.
            val big = "x".repeat(300_000)
            b.sendText("text", big)
            assertEquals(big, (la.files.receive().single().result as File).readText())
        }
    }

    @Test fun codePairing() = runBlocking {
        withTimeout(60_000) {
            val lb = L(); val lc = L()
            val b = core("00000000-0000-4000-8000-00000000000b", "Mac B", lb)
            val c = core("00000000-0000-4000-8000-00000000000c", "Phone C", lc)
            val win = b.openPairWindow()

            try {
                c.join(listOf(local(b)), "code", "999999".toByteArray().takeIf { win.code != "999999" } ?: "000000".toByteArray())
                fail("wrong code paired")
            } catch (e: PairException) {
                assertEquals("bad_code", e.reason)
            }
            assertTrue(lb.peers.isEmpty())

            val joinerSas = CompletableDeferred<String>()
            val peer = c.join(listOf(local(b)), "code", win.code.toByteArray()) { joinerSas.complete(it) }
            assertEquals(b.me.id, peer.id)
            assertEquals(lb.hostSas.await(), joinerSas.await())
            assertArrayEquals(lc.peers[b.me.id]!!.key, lb.peers[c.me.id]!!.key)
            assertEquals(b.me.id, lc.connected.receive())
            c.sendText("text", "via code")
            assertEquals("text:via code", lb.texts.receive())
        }
    }

    @Test fun rejectedSas() = runBlocking {
        withTimeout(30_000) {
            val b = core("00000000-0000-4000-8000-00000000000b", "Mac B", L(confirm = false))
            val c = core("00000000-0000-4000-8000-00000000000c", "Phone C", L())
            val win = b.openPairWindow()
            try {
                c.join(listOf(local(b)), "code", win.code.toByteArray())
                fail("rejected pairing succeeded")
            } catch (e: PairException) {
                assertEquals("rejected", e.reason)
            }
        }
    }
}
