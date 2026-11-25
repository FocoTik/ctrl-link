package com.ctrllink.android

import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.OpenableColumns
import androidx.core.content.IntentCompat
import com.ctrllink.android.proto.Crypto
import com.ctrllink.android.proto.OutFile
import com.ctrllink.android.proto.Peer
import com.ctrllink.android.proto.hex
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File
import java.io.FileInputStream
import java.io.InputStream
import java.nio.ByteBuffer

/** Builds outgoing clips from the clipboard or a Share intent (§6.4). */
object Send {
    fun fp(text: String) = hex(Crypto.sha256(text.toByteArray()))

    /** Clipboard paths (foreground listener, tile, notification). Read [clip] while our window has focus. */
    suspend fun clipboard(ctx: Context, clip: ClipData?): String {
        if (clip == null || clip.itemCount == 0) return "Clipboard is empty"
        val uris = (0 until clip.itemCount).mapNotNull { clip.getItemAt(it).uri }
        val text = if (uris.isEmpty()) (0 until clip.itemCount).joinToString("\n") { clip.getItemAt(it).coerceToText(ctx) } else null
        // Password managers flag their copies (Android 13+); they still sync but are never written to History.
        val sensitive = clip.description.extras?.getBoolean("android.content.extra.IS_SENSITIVE") == true
        return send(ctx, text, uris, fromClipboard = true, sensitive = sensitive)
    }

    suspend fun share(ctx: Context, i: Intent): String {
        val uris = if (i.action == Intent.ACTION_SEND_MULTIPLE) {
            IntentCompat.getParcelableArrayListExtra(i, Intent.EXTRA_STREAM, Uri::class.java).orEmpty()
        } else {
            listOfNotNull(IntentCompat.getParcelableExtra(i, Intent.EXTRA_STREAM, Uri::class.java))
        }
        val text = if (uris.isEmpty()) i.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString() else null
        return send(ctx, text, uris, fromClipboard = false)
    }

    private suspend fun send(ctx: Context, text: String?, uris: List<Uri>, fromClipboard: Boolean, sensitive: Boolean = false): String = withContext(Dispatchers.IO) {
        val core = Hub.core.value ?: return@withContext "Ctrl Link is starting, try again"
        if (core.sessions.isEmpty()) return@withContext "No devices connected"
        val sent: List<Peer>
        val item: HistoryItem
        if (uris.isNotEmpty()) {
            val files = try { uris.map { outFile(ctx, it) } } catch (e: Exception) { return@withContext "Couldn't read that file" }
            val image = files.size == 1 && files[0].mime.startsWith("image/")
            // Received content is only ever text or an image, so only those can loop back (§6.4).
            if (fromClipboard && image && !fresh(files[0].open().use(::sha))) return@withContext "Already in sync"
            val kind = if (image) "image" else "files"
            sent = core.sendClip(kind, files = files)
            item = HistoryItem(kind, null, uris[0].toString(), files.joinToString { it.name }, "", true, System.currentTimeMillis())
        } else {
            if (text.isNullOrEmpty()) return@withContext "Nothing to send"
            if (fromClipboard && !fresh(fp(text))) return@withContext "Already in sync"
            val t = text.trim()
            val url = !t.contains(Regex("\\s")) && (t.startsWith("http://", true) || t.startsWith("https://", true))
            val kind = if (url) "url" else "text"
            sent = core.sendText(kind, text)
            item = HistoryItem(kind, text, null, text.take(200), "", true, System.currentTimeMillis())
        }
        if (sent.isEmpty()) return@withContext "No devices connected"
        if (!sensitive) Hub.store.addHistory(item.copy(peer = sent.joinToString { it.name }))
        "Sent to ${sent.joinToString { it.name }}"
    }

    private fun fresh(f: String) = (f != Hub.lastFp).also { if (it) Hub.lastFp = f }

    private fun sha(i: InputStream): String {
        val md = Crypto.sha256()
        val buf = ByteArray(65536)
        while (true) { val n = i.read(buf); if (n < 0) break; md.update(buf, 0, n) }
        return hex(md.digest())
    }

    private fun outFile(ctx: Context, uri: Uri): OutFile {
        val cr = ctx.contentResolver
        var name: String? = null
        runCatching {
            cr.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { if (it.moveToFirst()) name = it.getString(0) }
        }
        val n = name ?: uri.lastPathSegment?.substringAfterLast('/') ?: "file"
        val mime = cr.getType(uri) ?: "application/octet-stream"
        // Open now, while we still hold the URI grant: the peer may accept minutes later.
        // shortcut: the descriptor is closed when garbage-collected; track it per clip if fd use ever matters.
        val pfd = cr.openFileDescriptor(uri, "r") ?: error("can't open $uri")
        if (pfd.statSize >= 0) return OutFile(n, mime, pfd.statSize) { PfdStream(pfd) }
        // A pipe can be read once; copy it so every peer can get it.
        val tmp = File.createTempFile("send", null, ctx.cacheDir)
        ParcelFileDescriptor.AutoCloseInputStream(pfd).use { i -> tmp.outputStream().use { i.copyTo(it) } }
        return OutFile(n, mime, tmp.length()) { tmp.inputStream() }
    }

    /** Positional reads, so several peers can stream the same descriptor at once. */
    private class PfdStream(private val pfd: ParcelFileDescriptor) : InputStream() {
        private val ch = FileInputStream(pfd.fileDescriptor).channel
        private var pos = 0L
        override fun read(): Int { val b = ByteArray(1); return if (read(b, 0, 1) < 0) -1 else b[0].toInt() and 0xff }
        override fun read(b: ByteArray, off: Int, len: Int): Int = ch.read(ByteBuffer.wrap(b, off, len), pos).also { if (it > 0) pos += it }
    }
}
