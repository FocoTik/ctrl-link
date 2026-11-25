package com.ctrllink.android.proto

import org.bouncycastle.math.ec.rfc7748.X25519
import org.json.JSONObject
import java.nio.ByteBuffer
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/** Protocol v1 crypto (PROTOCOL.md §3–§5). Mirrors protocol/test-peer/crypto.mjs. */
object Crypto {
    val rnd = SecureRandom()

    fun random(n: Int) = ByteArray(n).also(rnd::nextBytes)

    fun x25519Pub(priv: ByteArray) = ByteArray(32).also { X25519.generatePublicKey(priv, 0, it, 0) }

    fun x25519Shared(priv: ByteArray, peerPub: ByteArray): ByteArray {
        require(peerPub.size == 32) { "bad public key" }
        val out = ByteArray(32)
        // false = all-zero result, i.e. a low-order peer key
        require(X25519.calculateAgreement(priv, 0, peerPub, 0, out, 0)) { "bad public key" }
        return out
    }

    fun hmac(key: ByteArray, vararg data: ByteArray): ByteArray =
        Mac.getInstance("HmacSHA256").run {
            // An empty HMAC key equals 32 zero bytes (RFC 2104 pads keys with zeros); JCA rejects empty keys.
            init(SecretKeySpec(key.takeIf { it.isNotEmpty() } ?: ByteArray(32), "HmacSHA256"))
            data.forEach(::update)
            doFinal()
        }

    /** RFC 5869 HKDF-SHA256. */
    fun hkdf(ikm: ByteArray, salt: ByteArray, info: ByteArray, len: Int): ByteArray {
        val prk = hmac(salt, ikm)
        val out = ByteArray(len)
        var t = ByteArray(0)
        var pos = 0
        var i = 1
        while (pos < len) {
            t = hmac(prk, t, info, byteArrayOf(i++.toByte()))
            val n = minOf(t.size, len - pos)
            t.copyInto(out, pos, 0, n)
            pos += n
        }
        return out
    }

    fun sha256() = MessageDigest.getInstance("SHA-256")
    fun sha256(b: ByteArray): ByteArray = sha256().digest(b)

    class PairKeys(val pairKey: ByteArray, val sas: String, val joinerMac: ByteArray, val hostMac: ByteArray)

    fun pairingKeys(shared: ByteArray, secret: ByteArray, hostPub: ByteArray, joinerPub: ByteArray): PairKeys {
        val pairKey = hkdf(shared, secret, "ctrllink pair v1".toByteArray() + hostPub + joinerPub, 32)
        val sasBytes = hkdf(shared, ByteArray(0), "ctrllink sas v1".toByteArray() + hostPub + joinerPub, 4)
        val sas = (ByteBuffer.wrap(sasBytes).int.toLong() and 0xffffffffL) % 1_000_000
        return PairKeys(
            pairKey,
            sas.toString().padStart(6, '0'),
            hmac(pairKey, "joiner".toByteArray(), hostPub, joinerPub),
            hmac(pairKey, "host".toByteArray(), hostPub, joinerPub),
        )
    }

    /** Returns (c2sKey, s2cKey). */
    fun sessionKeys(pairKey: ByteArray, clientNonce: ByteArray, serverNonce: ByteArray): Pair<ByteArray, ByteArray> {
        val k = hkdf(pairKey, clientNonce + serverNonce, "ctrllink session v1".toByteArray(), 64)
        return k.copyOfRange(0, 32) to k.copyOfRange(32, 64)
    }

    fun nonce(dir: Int, counter: Long): ByteArray = ByteBuffer.allocate(12).putInt(dir).putLong(counter).array()

    private fun gcm(mode: Int, key: ByteArray, dir: Int, counter: Long) = Cipher.getInstance("AES/GCM/NoPadding").apply {
        init(mode, SecretKeySpec(key, "AES"), GCMParameterSpec(128, nonce(dir, counter)))
    }

    fun seal(key: ByteArray, dir: Int, counter: Long, plain: ByteArray, len: Int = plain.size): ByteArray =
        gcm(Cipher.ENCRYPT_MODE, key, dir, counter).doFinal(plain, 0, len)

    fun open(key: ByteArray, dir: Int, counter: Long, body: ByteArray): ByteArray =
        gcm(Cipher.DECRYPT_MODE, key, dir, counter).doFinal(body)
}

fun b64(b: ByteArray): String = Base64.getEncoder().encodeToString(b)
fun b64d(s: String): ByteArray = Base64.getDecoder().decode(s)
fun b64u(b: ByteArray): String = Base64.getUrlEncoder().withoutPadding().encodeToString(b)
fun b64ud(s: String): ByteArray = Base64.getUrlDecoder().decode(s)
fun hex(b: ByteArray) = b.joinToString("") { "%02x".format(it) }
fun unhex(s: String) = ByteArray(s.length / 2) { s.substring(it * 2, it * 2 + 2).toInt(16).toByte() }

fun uuidBytes(id: String): ByteArray = UUID.fromString(id).let {
    ByteBuffer.allocate(16).putLong(it.mostSignificantBits).putLong(it.leastSignificantBits).array()
}

fun uuidAt(b: ByteArray, off: Int): String = ByteBuffer.wrap(b, off, 16).let { UUID(it.long, it.long).toString() }

fun j(vararg p: Pair<String, Any?>) = JSONObject().apply { p.forEach { (k, v) -> put(k, v) } }
