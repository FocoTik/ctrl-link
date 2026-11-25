package com.ctrllink.android.proto

import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test
import java.io.File

/** PROTOCOL.md §8: reproduce protocol/test-vectors.json byte for byte. */
class VectorsTest {
    private val v = JSONObject(File("../../protocol/test-vectors.json").readText())
    private fun h(o: JSONObject, k: String) = unhex(o.getString(k))

    @Test fun vectors() {
        val x = v.getJSONObject("x25519")
        val hostPriv = h(x, "hostPriv")
        val joinerPriv = h(x, "joinerPriv")
        val hostPub = Crypto.x25519Pub(hostPriv)
        val joinerPub = Crypto.x25519Pub(joinerPriv)
        assertEquals(x.getString("hostPub"), hex(hostPub))
        assertEquals(x.getString("joinerPub"), hex(joinerPub))
        val shared = Crypto.x25519Shared(hostPriv, joinerPub)
        assertEquals(x.getString("shared"), hex(shared))
        assertEquals(x.getString("shared"), hex(Crypto.x25519Shared(joinerPriv, hostPub)))

        for ((name, secret) in listOf(
            "pairing_qr" to h(v.getJSONObject("pairing_qr"), "token"),
            "pairing_code" to v.getJSONObject("pairing_code").getString("code").toByteArray(),
        )) {
            val e = v.getJSONObject(name)
            val k = Crypto.pairingKeys(shared, secret, hostPub, joinerPub)
            assertEquals(name, e.getString("pairKey"), hex(k.pairKey))
            assertEquals(name, e.getString("sas"), k.sas)
            assertEquals(name, e.getString("joinerMac"), hex(k.joinerMac))
            assertEquals(name, e.getString("hostMac"), hex(k.hostMac))
        }

        val s = v.getJSONObject("session")
        val (c2s, s2c) = Crypto.sessionKeys(h(s, "pairKey"), h(s, "clientNonce"), h(s, "serverNonce"))
        assertEquals(s.getString("c2sKey"), hex(c2s))
        assertEquals(s.getString("s2cKey"), hex(s2c))
        val frames = s.getJSONArray("frames")
        assertEquals(4, frames.length())
        for (i in 0 until frames.length()) {
            val f = frames.getJSONObject(i)
            val key = if (f.getString("from") == "client") c2s else s2c
            val body = Crypto.seal(key, f.getInt("dir"), f.getLong("counter"), h(f, "plaintext"))
            assertEquals("frame $i", f.getString("body"), hex(body))
            assertArrayEquals(h(f, "plaintext"), Crypto.open(key, f.getInt("dir"), f.getLong("counter"), body))
        }
    }

    @Test fun wireFramesMatchVectors() {
        // The same frames through Wire (counters + dir handled by the session) over a socket pair.
        val s = v.getJSONObject("session")
        val (c2s, s2c) = Crypto.sessionKeys(h(s, "pairKey"), h(s, "clientNonce"), h(s, "serverNonce"))
        java.net.ServerSocket(0).use { ss ->
            val client = Wire(java.net.Socket("127.0.0.1", ss.localPort))
            val raw = java.io.DataInputStream(ss.accept().getInputStream())
            client.secure(c2s, s2c, client = true)
            val frames = s.getJSONArray("frames")
            for (i in 0..1) {
                client.send(h(frames.getJSONObject(i), "plaintext"))
                val body = ByteArray(raw.readInt()).also(raw::readFully)
                assertEquals(frames.getJSONObject(i).getString("body"), hex(body))
            }
            client.close()
        }
    }

    @Test fun helpers() {
        assertEquals("passwd", safeName("../../etc/passwd"))
        assertEquals("file", safeName("..."))
        assertEquals("a_b_c", safeName("a:b\u0001c"))
        assertEquals("evil", safeName("C:\\x\\evil"))
        assertEquals(200, safeName("é".repeat(300)).toByteArray().size)
        val taken = setOf("a.txt", "a (1).txt")
        assertEquals("a (2).txt", dedup("a.txt") { it in taken })
        assertEquals("b", dedup("b") { it in taken })
        assertEquals("other", platform("linux"))
    }
}
