package com.ctrllink.android.proto

import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.Closeable
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.Socket

const val MAX_FRAME = 262144 + 64
const val CHUNK = 262144

/** Length-prefixed frames (§3): JSON before the session, AES-GCM with implicit nonces after. */
class Wire(val socket: Socket) : Closeable {
    private val inp = DataInputStream(BufferedInputStream(socket.getInputStream()))
    private val out = DataOutputStream(BufferedOutputStream(socket.getOutputStream()))
    private lateinit var sendKey: ByteArray
    private lateinit var recvKey: ByteArray
    private var sendDir = 0
    private var recvDir = 0
    private var sendCtr = 0L
    private var recvCtr = 0L

    fun read(): ByteArray {
        val n = inp.readInt()
        if (n !in 0..MAX_FRAME) throw IOException("bad frame length $n")
        return ByteArray(n).also { inp.readFully(it) }
    }

    @Synchronized fun write(body: ByteArray) {
        if (body.size > MAX_FRAME) throw IOException("frame too big")
        out.writeInt(body.size)
        out.write(body)
        out.flush()
    }

    fun readJson() = JSONObject(String(read(), Charsets.UTF_8))
    fun writeJson(o: JSONObject) = write(o.toString().toByteArray())

    fun secure(send: ByteArray, recv: ByteArray, client: Boolean) {
        sendKey = send
        recvKey = recv
        sendDir = if (client) 1 else 2
        recvDir = 3 - sendDir
    }

    /** Encrypts `plain[0 until len]` (kind byte + payload) as the next frame. */
    @Synchronized fun send(plain: ByteArray, len: Int = plain.size) = write(Crypto.seal(sendKey, sendDir, sendCtr++, plain, len))

    fun recv(): ByteArray = Crypto.open(recvKey, recvDir, recvCtr++, read())

    override fun close() = socket.close()
}
