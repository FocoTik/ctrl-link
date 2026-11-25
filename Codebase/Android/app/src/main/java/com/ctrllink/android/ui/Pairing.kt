package com.ctrllink.android.ui

import android.graphics.Bitmap
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import android.content.ClipData
import android.content.ClipboardManager
import android.widget.Toast
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.Role
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.FilterQuality
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.ctrllink.android.Hub
import com.ctrllink.android.R
import com.ctrllink.android.proto.Core
import com.ctrllink.android.proto.PairException
import com.ctrllink.android.proto.PairWindow
import com.ctrllink.android.proto.Peer
import com.ctrllink.android.proto.lanIps
import com.ctrllink.android.proto.parseInvite
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.qrcode.QRCodeWriter
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import java.net.InetSocketAddress

/**
 * Back arrow, a centred column (top-aligned, or vertically centred for [center]) and an optional footer row pinned
 * to the bottom with 24/16 padding, as in the Figma pairing frames.
 */
@Composable
fun PairFrame(
    onBack: () -> Unit,
    center: Boolean = false,
    footer: (@Composable RowScope.() -> Unit)? = null,
    content: @Composable ColumnScope.() -> Unit,
) {
    Column(Modifier.fillMaxSize()) {
        IconButton(onClick = onBack, modifier = Modifier.padding(4.dp)) {
            Icon(painterResource(R.drawable.ic_back), contentDescription = "Back", tint = T.text)
        }
        Column(
            Modifier.weight(1f).fillMaxWidth().verticalScroll(rememberScrollState()).padding(horizontal = 24.dp, vertical = 8.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = if (center) Arrangement.spacedBy(12.dp, Alignment.CenterVertically) else Arrangement.spacedBy(12.dp),
            content = content,
        )
        if (footer != null) Row(
            Modifier.fillMaxWidth().padding(horizontal = 24.dp, vertical = 16.dp),
            horizontalArrangement = Arrangement.spacedBy(12.dp), content = footer,
        )
    }
}

/** Black modules on white with the standard 4-module quiet zone, one pixel per module (scaled without smoothing). */
private fun qr(text: String): Bitmap {
    val m = QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, mapOf(EncodeHintType.MARGIN to 4))
    val px = IntArray(m.width * m.height) { if (m[it % m.width, it / m.width]) android.graphics.Color.BLACK else android.graphics.Color.WHITE }
    return Bitmap.createBitmap(px, m.width, m.height, Bitmap.Config.ARGB_8888)
}

/** SAS confirmation, shared by host and joiner: one serif numeral "042917", sized to fit the width. */
@Composable
private fun ColumnScope.Sas(sas: String, headline: String, note: String?) {
    Label("Confirm on both screens")
    BoxWithConstraints(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
        // Newsreader Light digits are ~0.5 em wide; keep six of them inside the column.
        val size = (minOf(120f, maxWidth.value / 3.4f) / LocalDensity.current.fontScale).sp // fits at any font scale
        Text(sas, style = heroStyle(size), color = T.text, maxLines = 1, modifier = Modifier.semantics { contentDescription = sas.toList().joinToString(" ") })
    }
    Headline(headline)
    note?.let { Text(it, color = T.secondary, fontSize = 15.sp, textAlign = TextAlign.Center) }
}

/** Countdown: label and m:ss over a 6 dp track filling white → amber → sky. */
@Composable
private fun Countdown(left: Long, frac: Float) {
    Column(Modifier.fillMaxWidth().padding(horizontal = 22.dp, vertical = 16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Label("Code expires in", Modifier.weight(1f))
            Text("%d:%02d".format(left / 60, left % 60), color = T.text, fontSize = 13.sp)
        }
        Box(Modifier.fillMaxWidth().height(6.dp).clip(CircleShape).background(T.hairline)) {
            Box(Modifier.fillMaxWidth(frac.coerceIn(0f, 1f)).height(6.dp).clip(CircleShape)
                .background(Brush.horizontalGradient(listOf(T.text, Color(0xFFF5C26B), Color(0xFF3AA4F2)))))
        }
    }
}

@Composable
fun HostScreen(onBack: () -> Unit) {
    val core by Hub.core.collectAsStateWithLifecycle()
    val live by Hub.pairWindow.collectAsStateWithLifecycle()
    val prompt by Hub.pairPrompt.collectAsStateWithLifecycle()
    var win by remember { mutableStateOf<PairWindow?>(null) }
    var pairedWith by remember { mutableStateOf<String?>(null) }
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }

    LaunchedEffect(core) { if (pairedWith == null && win == null) win = core?.openPairWindow() }
    LaunchedEffect(Unit) { Hub.paired.collect { pairedWith = it.name } }
    LaunchedEffect(Unit) { while (true) { now = System.currentTimeMillis(); delay(250) } }
    DisposableEffect(Unit) { onDispose { Hub.core.value?.closePairWindow() } }

    val ctx = LocalContext.current
    val w = win
    val c = core
    val p = prompt
    when {
        pairedWith != null -> PairFrame(onBack, center = true, footer = { PillButton("Done", filled = true, onClick = onBack, modifier = Modifier.weight(1f)) }) {
            Label("Paired")
            Headline("Paired with $pairedWith")
        }
        p != null -> PairFrame(onBack, center = true, footer = {
            PillButton("Decline", filled = false, onClick = { p.answer.complete(false) })
            PillButton("Accept", filled = true, onClick = { p.answer.complete(true) }, modifier = Modifier.weight(1f))
        }) {
            Sas(p.sas, "${p.name} wants to pair", null)
        }
        c == null || w == null -> PairFrame(onBack, center = true) { Label("Starting") }
        live !== w -> PairFrame(onBack, center = true, footer = {
            PillButton("New code", filled = true, onClick = { win = c.openPairWindow() }, modifier = Modifier.weight(1f))
        }) {
            Label("Pair a device")
            Headline("This code has expired")
        }
        else -> {
            val uri = remember(w) { c.pairUri(w, lanIps()) }
            val bmp = remember(uri) { qr(uri).asImageBitmap() }
            PairFrame(onBack, footer = {
                PillButton("Copy pairing link", filled = false, modifier = Modifier.weight(1f), onClick = {
                    ctx.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("Ctrl Link pairing link", uri))
                    if (android.os.Build.VERSION.SDK_INT < 33) Toast.makeText(ctx, "Pairing link copied", Toast.LENGTH_SHORT).show()
                })
            }) {
                Label("Pair a device")
                Headline("Scan or enter the code")
                // The quiet zone is part of the bitmap, so the white card is the code's own margin.
                Image(bmp, contentDescription = "Pairing QR code", filterQuality = FilterQuality.None,
                    modifier = Modifier.fillMaxWidth(0.84f).widthIn(max = 320.dp).aspectRatio(1f).clip(RoundedCornerShape(12.dp)).background(Color.White))
                BoxWithConstraints(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
                    HeroText(w.code.chunked(3).joinToString(" "), size = (minOf(96f, maxWidth.value / 3.9f) / LocalDensity.current.fontScale).sp)
                }
                val left = ((w.expiresAt - now) / 1000).coerceAtLeast(0)
                Countdown(left, (w.expiresAt - now).coerceAtLeast(0) / (5 * 60_000f))
            }
        }
    }
}

private fun reasonText(r: String) = when (r) {
    "bad_code" -> "That code is wrong. Check it and try again."
    "rejected" -> "Pairing was declined on the other device."
    "timeout" -> "The other device didn't answer in time."
    "busy" -> "The other device is pairing with someone else. Try again in a moment."
    "closed" -> "The pairing code on the other device has closed. Open a new one."
    "no_host" -> "No device is showing a pairing code. Open Pair device on the other device first."
    "unreachable" -> "Couldn't reach the other device. Check that both are on the same Wi-Fi."
    "version" -> "Update Ctrl Link on the other device."
    "invalid" -> "That isn't a Ctrl Link pairing code."
    else -> "Pairing failed a security check. Try again."
}

/** Six large serif digit slots across the width, each on a hairline; the next slot's line is white. */
@Composable
private fun CodeField(code: String, onChange: (String) -> Unit) {
    val focus = remember { FocusRequester() }
    LaunchedEffect(Unit) { runCatching { focus.requestFocus() } }
    BasicTextField(
        value = code,
        onValueChange = { onChange(it.filter(Char::isDigit).take(6)) },
        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
        modifier = Modifier.fillMaxWidth().focusRequester(focus).semantics { contentDescription = "6-digit code" },
        decorationBox = {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                repeat(6) { i ->
                    Column(Modifier.weight(1f), horizontalAlignment = Alignment.CenterHorizontally) {
                        Text(code.getOrNull(i)?.toString() ?: " ", style = heroStyle(64.sp), color = T.text)
                        Spacer(Modifier.height(8.dp))
                        Box(Modifier.fillMaxWidth().height(1.dp).background(if (i == code.length) T.text else T.hairline))
                    }
                }
            }
        },
    )
}

@Composable
fun JoinScreen(invite: String?, onBack: () -> Unit) {
    val scope = rememberCoroutineScope()
    var code by rememberSaveable { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var status by remember { mutableStateOf<String?>(null) }
    var sas by remember { mutableStateOf<Pair<String, String>?>(null) } // sas to host name
    var paired by remember { mutableStateOf<Peer?>(null) }

    fun pair(block: suspend (Core) -> Peer) {
        busy = true; status = null; sas = null
        scope.launch {
            try {
                paired = block(Hub.core.filterNotNull().first())
            } catch (e: PairException) {
                status = reasonText(e.reason)
            } catch (e: Exception) {
                status = reasonText("unreachable")
            }
            busy = false; sas = null
        }
    }

    fun joinUri(s: String) {
        val inv = parseInvite(s) ?: run { status = reasonText("invalid"); return }
        pair { c ->
            val addrs = inv.hosts.mapNotNull { runCatching { InetSocketAddress(it, inv.port) }.getOrNull() } +
                listOfNotNull(Hub.found.value[inv.id]?.addr) // §4.1: fall back to mDNS by id
            c.join(addrs, "qr", inv.token, inv.key)
        }
    }

    fun joinCode() = pair { c ->
        val hosts = withTimeoutOrNull(8000) { Hub.found.first { m -> m.values.any { it.pairing } } }
            ?.values?.filter { it.pairing } ?: throw PairException("no_host")
        var last = PairException("no_host")
        for (h in hosts) {
            try {
                return@pair c.join(listOf(h.addr), "code", code.toByteArray()) { s -> sas = s to h.name }
            } catch (e: PairException) {
                last = e
                if (e.reason != "bad_code") throw e // §4.2: try the next pair=1 host only on bad_code
            }
        }
        throw last
    }

    val scan = rememberLauncherForActivityResult(ScanContract()) { r -> r.contents?.let(::joinUri) }
    LaunchedEffect(invite) { invite?.let(::joinUri) }
    // No Connect button in the design: six digits start the join; editing after a failure retries.
    LaunchedEffect(code) { if (code.length == 6 && !busy && invite == null) joinCode() }
    val ctx = LocalContext.current

    val p = paired
    val s = sas
    when {
        p != null -> PairFrame(onBack, center = true, footer = { PillButton("Done", filled = true, onClick = onBack, modifier = Modifier.weight(1f)) }) {
            Label("Paired")
            Headline("Paired with ${p.name}")
        }
        s != null -> PairFrame(onBack, center = true) {
            Sas(s.first, "Confirm on ${s.second}", "Accept on ${s.second} if it shows the same number.")
        }
        busy -> PairFrame(onBack, center = true) {
            Label("Join a device")
            Headline("Connecting")
        }
        else -> PairFrame(onBack, footer = {
            PillButton("Paste pairing link", filled = false, modifier = Modifier.weight(1f), onClick = {
                val text = ctx.getSystemService(ClipboardManager::class.java).primaryClip?.takeIf { it.itemCount > 0 }
                    ?.getItemAt(0)?.coerceToText(ctx)?.toString()?.trim()
                if (text.isNullOrEmpty()) status = "Copy the pairing link on the other device first." else joinUri(text)
            })
        }) {
            Label("Join a device")
            Headline("Enter the code")
            Text("Open Pair device on the other device first.", color = T.secondary, fontSize = 15.sp, textAlign = TextAlign.Center)
            Spacer(Modifier.height(16.dp))
            CodeField(code) { code = it; status = null }
            status?.let { Text(it, color = T.danger, fontSize = 15.sp, textAlign = TextAlign.Center) }
            Spacer(Modifier.height(8.dp))
            Text("Scan a QR code instead", color = T.secondary, fontSize = 15.sp, textAlign = TextAlign.Center,
                modifier = Modifier.clip(CircleShape).clickable(role = Role.Button) {
                    scan.launch(ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                        .setPrompt("Scan the code shown on the other device").setBeepEnabled(false).setOrientationLocked(false))
                }.padding(horizontal = 12.dp, vertical = 10.dp))
        }
    }
}
