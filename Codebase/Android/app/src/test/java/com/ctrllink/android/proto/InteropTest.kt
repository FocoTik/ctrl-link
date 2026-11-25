package com.ctrllink.android.proto

import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.io.File
import java.net.InetSocketAddress
import java.util.UUID

/**
 * Against protocol/test-peer (Node). Skipped unless env is set:
 *  CTRLLINK_INVITE=<uri>        we join the Node host by QR URI (dialing 127.0.0.1), or
 *  CTRLLINK_HOST_FILE=<path>    we host; "<port> <code>" is written there for `node … join <code> --connect`.
 * Either way we send a text clip and a ~5 MB file (sha printed), then expect a text line typed into the Node peer.
 */
class InteropTest {
    private var core: Core? = null
    @After fun stop() { core?.stop() }

    @Test fun nodePeer() = runBlocking {
        val invite = System.getenv("CTRLLINK_INVITE")
        val hostFile = System.getenv("CTRLLINK_HOST_FILE")
        assumeTrue(invite != null || hostFile != null)
        val l = LoopbackTest.L()
        val c = Core(Me(UUID.randomUUID().toString(), "JVM Android"), l).also { it.start(0); core = it }
        withTimeout(90_000) {
            val peerId = if (invite != null) {
                val inv = parseInvite(invite)!!
                c.join(listOf(InetSocketAddress("127.0.0.1", inv.port)), "qr", inv.token, inv.key).id
            } else {
                val w = c.openPairWindow()
                File(hostFile!!).writeText("${c.port} ${w.code}")
                println("SAS ${l.hostSas.await()}")
                l.connected.receive()
            }
            if (invite != null) assertEquals(peerId, l.connected.receive())
            println("CONNECTED $peerId")
            c.sendText("text", "hello from android ✓")
            val data = ByteArray(5 * 1024 * 1024 + 7).also { Crypto.rnd.nextBytes(it) }
            c.sendClip("files", files = listOf(OutFile("interop.bin", "application/octet-stream", data.size.toLong()) { data.inputStream() }))
            println("SENT_SHA ${hex(Crypto.sha256(data))}")
            println("GOT ${l.texts.receive()}")
        }
    }
}
