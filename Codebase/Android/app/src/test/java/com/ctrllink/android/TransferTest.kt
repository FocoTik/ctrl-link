package com.ctrllink.android

import com.ctrllink.android.proto.Core
import com.ctrllink.android.proto.LoopbackTest
import com.ctrllink.android.proto.Me
import com.ctrllink.android.proto.OutFile
import com.ctrllink.android.proto.Peer
import com.ctrllink.android.proto.parseInvite
import com.ctrllink.android.proto.shownIp
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.InputStream
import java.net.InetAddress
import java.net.InetSocketAddress
import java.util.concurrent.CopyOnWriteArrayList

class TransferTest {
    /** A sender listener that feeds a [TransferTracker] the way ClipService does. */
    private class Feed(val tracker: TransferTracker) : LoopbackTest.L() {
        val ended = CompletableDeferred<Boolean>()
        val rates = CopyOnWriteArrayList<Double>()
        override fun onProgress(from: Peer, sending: Boolean, clipId: String, name: String, done: Long, total: Long) {
            if (sending) tracker.progress(clipId, name, from.name, true, done, total)
        }
        override fun onTransferEnd(clipId: String, sending: Boolean, ok: Boolean) {
            if (sending) { tracker.end(clipId, ok); ended.complete(ok) }
        }
    }

    /** ~14 MB that trickles out at ~10 MB/s, so a real one-second sampler gets several samples on loopback. */
    private fun slow(size: Int): InputStream = object : InputStream() {
        var left = size
        override fun read(): Int = if (left-- > 0) 0 else -1
        override fun read(b: ByteArray, off: Int, len: Int): Int {
            if (left <= 0) return -1
            Thread.sleep(25)
            val n = minOf(len, left, 262_144)
            left -= n
            return n
        }
    }

    private suspend fun pair(la: LoopbackTest.L, lb: LoopbackTest.L): Pair<Core, Core> {
        val a = Core(Me("00000000-0000-4000-8000-00000000000a", "Phone"), la).also { it.start(0) }
        val b = Core(Me("00000000-0000-4000-8000-00000000000b", "Focotik's Mac mini"), lb).also { it.start(0) }
        val inv = parseInvite(b.pairUri(b.openPairWindow(), listOf("127.0.0.1")))!!
        a.join(listOf(InetSocketAddress("127.0.0.1", inv.port)), "qr", inv.token, inv.key)
        la.connected.receive(); lb.connected.receive()
        return a to b
    }

    @Test fun outgoingProgressIsSampledAndFinishes() = runBlocking {
        val tracker = TransferTracker()
        val la = Feed(tracker); val lb = LoopbackTest.L()
        val (a, b) = pair(la, lb)
        try {
            withTimeout(60_000) {
                val size = 14_000_000
                a.sendClip("files", files = listOf(OutFile("app.apk", "application/vnd.android.package-archive", size.toLong()) { slow(size) }))
                // The service's sampler, sped up 4x so the test stays short.
                val sampler = Thread {
                    while (!la.ended.isCompleted) {
                        Thread.sleep(250)
                        tracker.tick()?.let { if (!it.finished) la.rates += it.rate }
                    }
                }.apply { start() }
                assertTrue("transfer failed", la.ended.await())
                sampler.join()
                lb.files.receive()
            }
            val t = tracker.state.value!!
            assertTrue(t.finished)
            assertEquals(t.total, t.done)
            assertTrue(t.sending)
            assertEquals("Focotik's Mac mini", t.peer)
            assertTrue("only ${t.speed.size} samples", t.speed.size >= 4)
            assertTrue("no speed measured", t.speed.any { it.second > 1f })
            assertTrue("progress never moved", t.speed.map { it.first }.zipWithNext().all { (x, y) -> y >= x })
            assertTrue("no rate before the end", la.rates.any { it > 0 })
            tracker.clear(t.clipId)
            assertNull(tracker.state.value)
        } finally { a.stop(); b.stop() }
    }

    @Test fun cancelledTransferEndsAndClears() = runBlocking {
        val tracker = TransferTracker()
        val la = Feed(tracker); val lb = LoopbackTest.L()
        val (a, b) = pair(la, lb)
        try {
            withTimeout(30_000) {
                a.sendClip("files", files = listOf(OutFile("big.bin", "a/b", 14_000_000L) { slow(14_000_000) }))
                while (tracker.state.value == null) Thread.sleep(20)
                b.cancel(tracker.state.value!!.clipId) // the receiver gives up
                assertFalse(la.ended.await())
            }
            assertNull(tracker.state.value)
        } finally { a.stop(); b.stop() }
    }

    @Test fun trackerTimeLeftNeedsASample() {
        var now = 0L
        val tr = TransferTracker { now }
        tr.progress("c", "f", "p", true, 1_000_000, 10_000_000)
        assertEquals(0.0, tr.state.value!!.rate, 0.0) // shows an en dash
        now = 1000; tr.progress("c", "f", "p", true, 3_000_000, 10_000_000); tr.tick()
        assertEquals(3_000_000.0, tr.state.value!!.rate, 1.0)
        assertEquals(2, tr.state.value!!.speed.size)
        // A second transfer doesn't replace the live one.
        tr.progress("other", "g", "p", false, 1, 10_000_000)
        assertEquals("c", tr.state.value!!.clipId)
        assertFalse(tr.end("other", true))
        assertTrue(tr.end("c", false))
        assertNull(tr.state.value)
    }

    @Test fun shownIpPrefersIpv4AndHidesLinkLocal() {
        val ll = InetAddress.getByName("fe80::8f9:3474:a037:d42f")
        val v4 = InetAddress.getByName("192.168.1.14")
        assertEquals("192.168.1.14", shownIp(ll, v4))
        assertNull(shownIp(ll, null))
        assertNull(shownIp(InetAddress.getByName("127.0.0.1")))
        assertEquals("2001:db8:0:0:0:0:0:1", shownIp(InetAddress.getByName("2001:DB8::1")))
    }
}
