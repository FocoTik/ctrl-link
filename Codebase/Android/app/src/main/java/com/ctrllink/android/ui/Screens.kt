package com.ctrllink.android.ui

import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.draw.alpha
import android.content.ClipData
import android.content.Intent
import android.graphics.BitmapFactory
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.IconButton
import androidx.compose.runtime.produceState
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import com.ctrllink.android.Transfer
import com.ctrllink.android.proto.lanIps
import com.ctrllink.android.proto.shownIp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import android.content.ClipboardManager
import android.net.Uri
import android.text.format.DateFormat
import android.text.format.Formatter
import android.widget.Toast
import androidx.activity.compose.BackHandler
import androidx.lifecycle.compose.LifecycleEventEffect
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.sp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import com.ctrllink.android.ClipService
import com.ctrllink.android.HistoryItem
import com.ctrllink.android.FileOffer
import com.ctrllink.android.Hub
import com.ctrllink.android.R
import com.ctrllink.android.Send
import com.ctrllink.android.proto.APP
import com.ctrllink.android.proto.Peer
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.launch
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.TextStyle as DayStyle
import java.util.Locale

private val tabs = listOf("Devices", "History", "Settings")

@Composable
fun App(invite: MutableStateFlow<String?>) {
    val store = Hub.store
    var tab by rememberSaveable { mutableIntStateOf(0) }
    // Sub-screens as a tiny back stack ("paired/host"), so Pair a new device returns to Paired devices.
    var stack by rememberSaveable { mutableStateOf("") }
    var splash by rememberSaveable { mutableStateOf(true) }
    val settings by store.settings.collectAsStateWithLifecycle()
    val inv by invite.collectAsStateWithLifecycle()
    val offers by Hub.offers.collectAsStateWithLifecycle()
    // Back on the offer screen leaves it unanswered (the notification still has Accept / Decline) and shows the app.
    var skipped by rememberSaveable { mutableStateOf(emptySet<String>()) }
    LifecycleEventEffect(Lifecycle.Event.ON_START) { skipped = emptySet() } // reopening the app asks again
    val offer = offers.firstOrNull { it.id !in skipped }
    val screen = stack.substringAfterLast('/').ifEmpty { null }
    fun push(s: String) { stack = if (stack.isEmpty()) s else "$stack/$s" }
    val back = { if (inv != null) invite.value = null else stack = stack.substringBeforeLast('/', "") }
    BackHandler(screen != null || inv != null) { back() }
    BackHandler(offer != null && !splash) { offer?.let { skipped = skipped + it.id } } // declared last, so it wins

    // As long as the service takes to come up, held for at least 600 ms so it reads as a screen rather than a flash,
    // and never more than 1.5 s.
    LaunchedEffect(Unit) {
        if (splash) {
            val t0 = System.currentTimeMillis()
            withTimeoutOrNull(1500) { Hub.core.first { it != null } }
            delay((600 - (System.currentTimeMillis() - t0)).coerceAtLeast(0))
            splash = false
        }
    }

    Box(Modifier.fillMaxSize().background(T.bg).systemBarsPadding().imePadding()) {
        when {
            splash -> Splash()
            offer != null -> OfferScreen(offer, onBack = { skipped = skipped + offer.id })
            inv != null || screen == "join" -> JoinScreen(inv, back)
            screen == "host" -> HostScreen(back)
            screen == "paired" -> PairedDevicesScreen(back, onPairNew = { push("host") })
            !settings.welcomed -> Welcome(onPair = { store.setWelcomed(); push("host") }, onSkip = store::setWelcomed)
            else -> Column {
                Pills(tabs, tab, { tab = it }, Modifier.padding(horizontal = 16.dp, vertical = 8.dp))
                when (tab) {
                    0 -> DevicesScreen(onPair = { push("host") }, onJoin = { push("join") })
                    1 -> HistoryScreen()
                    else -> SettingsScreen(onPaired = { push("paired") })
                }
            }
        }
    }
}

fun platformName(pf: String) = when (pf) { "mac" -> "Mac"; "android" -> "Android"; "windows" -> "Windows"; else -> "Other" }

private val bottomRoom = PaddingValues(bottom = 32.dp)
private val side = Modifier.padding(horizontal = 24.dp)

private fun LazyListScope.gap(h: Int) = item { Spacer(Modifier.height(h.dp)) }

// ---- Devices ----

/** This phone's LAN address for the graph: prefer a private IPv4 (Wi-Fi), else the first one. */
private fun myIp(): String? = lanIps().let { ips -> ips.firstOrNull { it.startsWith("192.168.") || it.startsWith("10.") || it.startsWith("172.") } ?: ips.firstOrNull() }

@Composable
private fun DevicesScreen(onPair: () -> Unit, onJoin: () -> Unit) {
    val store = Hub.store
    val peers by store.peers.collectAsStateWithLifecycle()
    val online by Hub.online.collectAsStateWithLifecycle()
    val found by Hub.found.collectAsStateWithLifecycle()
    val settings by store.settings.collectAsStateWithLifecycle()
    val tr by Hub.transfers.state.collectAsStateWithLifecycle()
    val ctx = LocalContext.current

    tr?.let { t ->
        return TransferPanel(t) {
            ctx.startService(Intent(ctx, ClipService::class.java).setAction(ClipService.ACT_CANCEL).putExtra("id", t.clipId))
        }
    }

    val ip = remember(online, found) { myIp() }
    val sorted = peers.values.sortedWith(compareBy({ it.id !in online }, { it.name.lowercase() }))
    val nodes = sorted.map { p ->
        val addr = shownIp(Hub.core.value?.sessions?.get(p.id)?.remoteAddr, found[p.id]?.addr?.address)
        GraphNode(p.name, addr ?: if (p.id in online) platformName(p.pf) else "Offline", p.pf, p.id in online, caps = addr == null || ':' !in addr)
    }
    // Four peers fit; beyond that three are shown and the rest fold into "+N more".
    // The fold is lit only if one of the devices in it is online (online peers sort first, so usually it isn't).
    val shown = if (nodes.size <= 4) nodes else nodes.take(3) +
        GraphNode("${nodes.size - 3} more", "Paired", "", online = nodes.drop(3).any { it.online }, more = nodes.size - 3)
    val n = online.size

    // The horizon spans the whole screen (so it fades out instead of ending at an edge); its rim sits just above the caption.
    BoxWithConstraints(Modifier.fillMaxSize()) {
    Horizon(Modifier.fillMaxSize(), top = maxHeight - 212.dp)
    Column(Modifier.fillMaxSize()) {
        Box(Modifier.weight(1f).fillMaxWidth()) {
            Column(Modifier.fillMaxSize()) {
                DeviceGraph(GraphNode(settings.name, ip ?: "No network", "android", online = true, current = true), shown, Modifier.weight(1f))
                Column(Modifier.fillMaxWidth().padding(horizontal = 24.dp, vertical = 8.dp),
                    horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Label("On this network")
                    Headline(if (n == 0) "Looking for devices" else "${n + 1} devices connected")
                }
            }
        }
        Row(Modifier.fillMaxWidth().padding(horizontal = 24.dp, vertical = 16.dp),
            horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
            PillButton("Pair device", filled = true, onClick = onPair, modifier = Modifier.weight(1f))
            PillButton("Enter code", filled = false, onClick = onJoin, modifier = Modifier.weight(1f))
            SendButton()
        }
    }
    }
}

/** Incoming files over the auto-accept limit: the same Accept / Decline as the notification, reachable from the app. */
@Composable
private fun OfferScreen(o: FileOffer, onBack: () -> Unit) {
    val ctx = LocalContext.current
    val what = if (o.files.size == 1) o.files[0] else "${o.files.size} files"
    val names = if (o.files.size <= 2) o.files.joinToString(", ") else "${o.files.take(2).joinToString(", ")} and ${o.files.size - 2} more"
    PairFrame(onBack, center = true, footer = {
        PillButton("Decline", filled = false, onClick = { o.answer.complete(false) })
        PillButton("Accept", filled = true, onClick = { o.answer.complete(true) }, modifier = Modifier.weight(1f))
    }) {
        Label("Incoming files")
        Headline("${o.from} wants to send $what", size = 28.sp)
        Text("$names · ${Formatter.formatFileSize(ctx, o.total)}", color = T.secondary, fontSize = 14.sp, lineHeight = 20.sp,
            textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth())
    }
}

/**
 * Send clipboard, kept on Android because 10+ can't read the clipboard in the background; we're in the foreground
 * here, so it can. Same action as the notification button and the quick tile.
 */
@Composable
private fun SendButton() {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    RoundIconButton(R.drawable.ic_up, "Send clipboard", onClick = {
        val clip = ctx.getSystemService(ClipboardManager::class.java).primaryClip
        scope.launch { Toast.makeText(ctx, Send.clipboard(ctx, clip), Toast.LENGTH_SHORT).show() }
    })
}

/** Transfer in progress (Figma 43:886): who, file, hero percent, speed graph, size / time left, Cancel. */
@Composable
private fun TransferPanel(tr: Transfer, onCancel: () -> Unit) {
    val ctx = LocalContext.current
    // 100 only once the core says it's done (bytes in the socket buffer aren't delivered yet).
    val pct = if (tr.finished) 100 else (tr.done * 100 / tr.total).toInt().coerceAtMost(99)
    // Time left needs a measured rate: an en dash until the first one-second sample.
    val left = when {
        tr.finished -> "0:00"
        tr.rate <= 0.0 -> "\u2013"
        else -> ((tr.total - tr.done) / tr.rate).toLong().let { "%d:%02d".format(it / 60, it % 60) }
    }
    BoxWithConstraints(Modifier.fillMaxSize()) {
    Horizon(Modifier.fillMaxSize(), top = maxHeight * 0.74f)
    Column(Modifier.fillMaxSize()) {
        Box(Modifier.weight(1f).fillMaxWidth()) {
            Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 24.dp).padding(top = 71.dp),
                horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Label(if (tr.sending) "Sending to ${tr.peer}" else "Receiving from ${tr.peer}")
                Headline(tr.name, size = 26.sp)
                Hero(pct, unit = "%")
                TransferGraph(tr.speed)
                Spacer(Modifier.height(8.dp))
                StatRow("Size" to Formatter.formatShortFileSize(ctx, tr.total), "Time left" to left)
            }
        }
        Row(Modifier.fillMaxWidth().padding(horizontal = 24.dp, vertical = 16.dp)) {
            PillButton(if (tr.finished) "Done" else "Cancel", filled = false, onClick = onCancel, modifier = Modifier.weight(1f))
        }
    }
    }
}
// ---- History ----

private fun HistoryItem.day(): LocalDate = Instant.ofEpochMilli(ts).atZone(ZoneId.systemDefault()).toLocalDate()

@Composable
private fun HistoryScreen() {
    val store = Hub.store
    val history by store.history.collectAsStateWithLifecycle()
    val stats by store.stats.collectAsStateWithLifecycle()
    val ctx = LocalContext.current
    val days = remember { (6 downTo 0).map { LocalDate.now().minusDays(it.toLong()) } }
    var selected by rememberSaveable { mutableIntStateOf(6) }
    // Legend filters, as on the Mac: kinds switched off leave the bars, the total and the list.
    var hidden by rememberSaveable { mutableStateOf(emptyList<String>()) }
    val perDay = days.map { d -> stats[d.toString()].orEmpty().filterKeys { it !in hidden } }
    val total = perDay.sumOf { it.values.sum() }
    val dayItems = history.filter { it.day() == days[selected] && it.kind !in hidden }

    LazyColumn(contentPadding = bottomRoom) {
        item {
            val strip = rememberScrollState()
            LaunchedEffect(strip.maxValue) { strip.scrollTo(strip.maxValue) } // open on today
            Row(Modifier.fillMaxWidth().horizontalScroll(strip).padding(start = 4.dp, end = 16.dp, top = 8.dp, bottom = 8.dp),
                horizontalArrangement = Arrangement.End) {
                days.forEachIndexed { i, d ->
                    val on = i == selected
                    Column(Modifier.clickable(role = Role.Tab) { selected = i }.semantics { this.selected = on }
                        .padding(horizontal = 8.dp, vertical = 6.dp),
                        horizontalAlignment = Alignment.CenterHorizontally) {
                        Text(if (i == 6) "Today" else "${d.dayOfWeek.getDisplayName(DayStyle.SHORT, Locale.getDefault())} ${d.dayOfMonth}",
                            color = if (on) T.text else T.secondary, fontSize = 13.sp, lineHeight = 18.sp, maxLines = 1)
                        Spacer(Modifier.height(6.dp))
                        Box(Modifier.width(40.dp).height(1.dp).background(if (on) T.text else T.bg))
                    }
                }
            }
        }
        gap(16)
        item {
            Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
                Headline(if (total >= 70) "Busy week" else "Quiet week", side)
                Hero(total)
                Label("Average ${(Math.round(total / 0.7) / 10.0).let { if (it % 1.0 == 0.0) it.toInt().toString() else it.toString() }} a day")
            }
        }
        gap(24)
        item { Bars(perDay, days, selected) { selected = it } }
        gap(20)
        item { Legend(hidden) { k -> hidden = if (k in hidden) hidden - k else hidden + k } }
        gap(23)
        item { Label(if (selected == 6) "Today" else days[selected].format(java.time.format.DateTimeFormatter.ofLocalizedDate(java.time.format.FormatStyle.MEDIUM)), side.padding(bottom = 8.dp)) }
        if (dayItems.isEmpty()) item {
            Text(if (hidden.isEmpty()) "No clips on this day. Clips you send or receive show up here for 14 days; tap one to copy it again."
                else "No matching clips on this day.",
                color = T.secondary, fontSize = 15.sp, lineHeight = 20.sp, modifier = side.padding(vertical = 8.dp))
        }
        if (dayItems.isNotEmpty()) item { Hairline(side) }
        itemsIndexed(dayItems, key = { i, h -> "${h.ts}-$i" }) { _, h -> ClipRow(h) { copyAgain(ctx, h) } }
    }
}

/** Figma Clip Row: 3 dp kind bar, content, meta line, and the kind icon (an image shows its thumbnail instead). */
@Composable
private fun ClipRow(h: HistoryItem, onClick: () -> Unit) {
    val ctx = LocalContext.current
    Column(side) {
        Row(
            Modifier.fillMaxWidth().height(IntrinsicSize.Min).clickable(onClickLabel = "Copy again", onClick = onClick).padding(12.dp),
            horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(Modifier.width(3.dp).fillMaxHeight().background(kindColors[h.kind] ?: T.secondary, CircleShape))
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(h.text ?: h.label, color = T.text, fontSize = 15.sp, lineHeight = 20.sp, maxLines = 2, overflow = TextOverflow.Ellipsis)
                Text("${if (h.sent) "To" else "From"} ${h.peer} · ${DateFormat.getTimeFormat(ctx).format(h.ts)}",
                    color = T.secondary, fontSize = 13.sp, lineHeight = 18.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            val thumb = if (h.kind == "image" && h.uri != null) thumbnail(h.uri) else null
            if (thumb != null) {
                Image(thumb, null, Modifier.size(42.dp).clip(RoundedCornerShape(4.dp)), contentScale = ContentScale.Crop)
            } else {
                Icon(painterResource(when (h.kind) { "url" -> R.drawable.ic_kind_link; "files", "image" -> R.drawable.ic_kind_file; else -> R.drawable.ic_kind_text }),
                    null, Modifier.size(20.dp), tint = T.text)
            }
        }
        Hairline()
    }
}

/** A ~128 px thumbnail of a received image (null while loading, or once Clear history deleted the file). */
@Composable
private fun thumbnail(uri: String): ImageBitmap? {
    val ctx = LocalContext.current
    val bmp by produceState<ImageBitmap?>(null, uri) {
        value = withContext(Dispatchers.IO) {
            runCatching {
                val u = Uri.parse(uri)
                val o = BitmapFactory.Options().apply { inJustDecodeBounds = true }
                ctx.contentResolver.openInputStream(u)?.use { BitmapFactory.decodeStream(it, null, o) }
                var sample = 1
                while (minOf(o.outWidth, o.outHeight) / (sample * 2) >= 128) sample *= 2
                ctx.contentResolver.openInputStream(u)?.use {
                    BitmapFactory.decodeStream(it, null, BitmapFactory.Options().apply { inSampleSize = sample })
                }?.asImageBitmap()
            }.getOrNull()
        }
    }
    return bmp
}

private fun copyAgain(ctx: android.content.Context, h: HistoryItem) {
    val cm = ctx.getSystemService(ClipboardManager::class.java)
    if (h.text != null) {
        Hub.lastFp = Send.fp(h.text)
        cm.setPrimaryClip(ClipData.newPlainText("Ctrl Link", h.text))
    } else if (h.uri != null) {
        runCatching { cm.setPrimaryClip(ClipData.newUri(ctx.contentResolver, h.label, Uri.parse(h.uri))) }
    }
    Toast.makeText(ctx, "Copied", Toast.LENGTH_SHORT).show()
}

/**
 * Figma Stacked Bar x 7: 28 dp bars in equal columns, square segments with 2 dp gaps (files at the bottom, text on
 * top), an axis segment and the day letter under each. Grows from the baseline, staggered 30 ms (snaps with reduced
 * motion). Tapping a column selects that day.
 */
@Composable
private fun Bars(perDay: List<Map<String, Int>>, days: List<LocalDate>, selected: Int, onSelect: (Int) -> Unit) {
    val reduce = reduceMotion()
    val grow = remember { Animatable(if (reduce) 1f else 0f) }
    LaunchedEffect(Unit) { if (!reduce) grow.animateTo(1f, tween(300 + 30 * 6, easing = LinearEasing)) }
    val max = perDay.maxOf { it.values.sum() }.coerceAtLeast(1)
    val hairline = T.hairline
    Row(side.fillMaxWidth()) {
        perDay.forEachIndexed { i, counts ->
            val n = counts.values.sum()
            val day = days[i].dayOfWeek.getDisplayName(DayStyle.FULL, Locale.getDefault())
            Column(
                Modifier.weight(1f).clickable(role = Role.Tab) { onSelect(i) }
                    .semantics { contentDescription = "$day, $n ${if (n == 1) "clip" else "clips"}"; this.selected = i == selected },
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Canvas(Modifier.fillMaxWidth().height(173.dp)) {
                    val w = 28.dp.toPx().coerceAtMost(size.width)
                    val gap = 2.dp.toPx()
                    val t = ((grow.value * (300 + 180) - 30 * i) / 300f).coerceIn(0f, 1f)
                    var y = size.height
                    for ((kind, color) in kindColors.entries.reversed()) {
                        val c = counts[kind] ?: continue
                        val h = size.height * c / max * t
                        drawRect(color, androidx.compose.ui.geometry.Offset((size.width - w) / 2, y - h + gap),
                            androidx.compose.ui.geometry.Size(w, (h - gap).coerceAtLeast(0f)))
                        y -= h
                    }
                }
                Spacer(Modifier.height(6.dp))
                Box(Modifier.fillMaxWidth().padding(horizontal = 2.dp).height(1.dp).background(if (i == selected) T.text else hairline))
                Spacer(Modifier.height(6.dp))
                Label(days[i].dayOfWeek.getDisplayName(DayStyle.NARROW, Locale.getDefault()),
                    color = if (i == selected) T.text else T.secondary, align = androidx.compose.ui.text.style.TextAlign.Center)
            }
        }
    }
}

/** Figma Chart Legend Items; each is a filter that hides its kind from the bars, the total and the list. */
@Composable
private fun Legend(hidden: List<String>, onToggle: (String) -> Unit) {
    Column(side, verticalArrangement = Arrangement.spacedBy(8.dp)) {
        kindColors.entries.chunked(2).forEach { row ->
            Row(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                row.forEach { (kind, color) ->
                    val off = kind in hidden
                    val name = when (kind) { "url" -> "Links"; "text" -> "Text"; "image" -> "Image"; else -> "Files" }
                    Column(
                        Modifier.weight(1f).heightIn(min = 40.dp)
                            .toggleable(!off, role = Role.Checkbox) { onToggle(kind) }
                            .semantics { stateDescription = if (off) "Hidden" else "Shown" }
                            .alpha(if (off) 0.3f else 1f).padding(vertical = 4.dp),
                        verticalArrangement = Arrangement.spacedBy(6.dp, Alignment.CenterVertically),
                    ) {
                        Label(name)
                        Box(Modifier.fillMaxWidth().height(4.dp).background(color, CircleShape))
                    }
                }
            }
        }
    }
}

// ---- Settings ----

private val limits = listOf(25 to "25 MB", 100 to "100 MB", 500 to "500 MB", 2000 to "2 GB")

/** Figma Settings Row: title (15/20) and optional note (13/18) on the left, a control 12 dp to the right, hairline below. */
@Composable
private fun SettingRow(title: String, sub: String? = null, modifier: Modifier = Modifier, trailing: @Composable () -> Unit) {
    Column(side) {
        Row(modifier.fillMaxWidth().padding(vertical = 14.dp), verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(title, color = T.text, fontSize = 15.sp, lineHeight = 20.sp)
                sub?.let { Text(it, color = T.secondary, fontSize = 13.sp, lineHeight = 18.sp) }
            }
            trailing()
        }
        Hairline()
    }
}

/** A Settings Row with the Figma Toggle; the whole row is the switch, for a big touch target and one TalkBack stop. */
@Composable
private fun ToggleRow(title: String, sub: String?, checked: Boolean, onChange: (Boolean) -> Unit) =
    SettingRow(title, sub, Modifier.toggleable(checked, role = Role.Switch, onValueChange = onChange)) { Toggle(checked) }

@Composable
private fun SettingsScreen(onPaired: () -> Unit) {
    val store = Hub.store
    val s by store.settings.collectAsStateWithLifecycle()
    val peers by store.peers.collectAsStateWithLifecycle()
    var name by remember { mutableStateOf(s.name) }
    var clear by remember { mutableStateOf(false) }

    LazyColumn(contentPadding = PaddingValues(bottom = 32.dp)) {
        gap(16)
        item { Label("This device", side) }
        item {
            // Figma Text Field: caption, 4 dp, a 44 dp field (15/20) on a 1 dp strong underline (2 dp while focused).
            var focused by remember { mutableStateOf(false) }
            Column(side.padding(top = 8.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Label("Device name")
                BasicTextField(
                    value = name,
                    onValueChange = { name = it.take(63); if (it.isNotBlank()) store.setName(it.trim().take(63)) },
                    singleLine = true,
                    textStyle = TextStyle(color = T.text, fontSize = 15.sp, lineHeight = 20.sp),
                    cursorBrush = SolidColor(T.text),
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
                    modifier = Modifier.fillMaxWidth().heightIn(min = 44.dp).onFocusChanged { focused = it.isFocused }
                        .semantics { contentDescription = "Device name" },
                    decorationBox = { inner -> Box(Modifier.heightIn(min = 44.dp), contentAlignment = Alignment.CenterStart) { inner() } },
                )
                Box(Modifier.fillMaxWidth().padding(top = 0.dp).height(if (focused) 2.dp else 1.dp).background(T.text))
            }
        }
        gap(32)
        item { Label("Sending", side) }
        item {
            ToggleRow("Send automatically", "Sends what you copy. With Ctrl Link closed this needs Copy from any app; the tile, notification and Share always work.",
                s.autoSend, store::setAutoSend)
        }
        item { ToggleRow("Start on boot", null, s.boot, store::setBoot) }
        item { ToggleRow("Notification sound", "Play a sound when a clip arrives", s.sound, store::setSound) }
        item { CopyFromAnyApp() }
        gap(32)
        item { Label("Accept files without asking up to", side) }
        item { Pills(limits.map { it.second }, limits.indexOfFirst { it.first == s.limitMb }, { store.setLimitMb(limits[it].first) }, side.padding(vertical = 12.dp)) }
        item { SettingRow("Clear history") { PillButton("Clear", filled = false, onClick = { clear = true }) } }
        gap(24)
        item { Label("Devices", side) }
        item {
            val sorted = peers.values.sortedBy { it.name.lowercase() }
            SettingRow("Paired devices · ${sorted.size}", sorted.joinToString { it.name }.ifEmpty { "None yet" },
                Modifier.clickable(role = Role.Button, onClick = onPaired)) {
                Icon(painterResource(R.drawable.ic_chevron_right), null, Modifier.size(20.dp), tint = T.text)
            }
        }
        gap(32)
        item { Label("Ctrl Link $APP · ${store.me.id.take(8)}", side) }
    }

    if (clear) AlertDialog(
        onDismissRequest = { clear = false },
        title = { Text("Clear history?", fontFamily = Serif) },
        text = { Text("Deletes the saved clips, the daily counts and received image thumbnails on this phone. Paired devices stay paired.") },
        confirmButton = { TextButton(onClick = { store.clearHistory(); clear = false }) { Text("Clear", color = T.danger) } },
        dismissButton = { TextButton(onClick = { clear = false }) { Text("Cancel") } },
    )
}

/** Settings › Paired devices (Figma 80:2389). */
@Composable
fun PairedDevicesScreen(onBack: () -> Unit, onPairNew: () -> Unit) {
    val store = Hub.store
    val peers by store.peers.collectAsStateWithLifecycle()
    var unpair by remember { mutableStateOf<Peer?>(null) }
    Column(Modifier.fillMaxSize()) {
        IconButton(onClick = onBack, modifier = Modifier.padding(4.dp)) {
            Icon(painterResource(R.drawable.ic_back), contentDescription = "Back", tint = T.text)
        }
        LazyColumn(contentPadding = PaddingValues(bottom = 32.dp)) {
            item { Label("Paired devices", side.padding(top = 8.dp, bottom = 8.dp)) }
            item { Hairline(side) }
            if (peers.isEmpty()) item {
                Text("None yet.", color = T.secondary, fontSize = 15.sp, modifier = side.padding(vertical = 14.dp))
            }
            items(peers.values.sortedBy { it.name.lowercase() }, key = { it.id }) { p ->
                SettingRow(p.name, platformName(p.pf)) { PillButton("Unpair", filled = false, onClick = { unpair = p }) }
            }
            item {
                Text("Unpairing deletes the keys on both devices. To connect again, pair with a new code.",
                    color = T.secondary, fontSize = 13.sp, lineHeight = 18.sp, modifier = side.padding(vertical = 12.dp))
            }
            item {
                Box(side.fillMaxWidth(), contentAlignment = Alignment.CenterEnd) {
                    PillButton("Pair a new device", filled = false, onClick = onPairNew)
                }
            }
        }
    }
    unpair?.let { p ->
        AlertDialog(
            onDismissRequest = { unpair = null },
            title = { Text("Unpair ${p.name}?", fontFamily = Serif) },
            text = { Text("Both devices forget each other. You can pair again any time.") },
            confirmButton = {
                TextButton(onClick = {
                    Hub.core.value?.unpair(p.id) ?: store.removePeer(p.id)
                    unpair = null
                }) { Text("Unpair", color = T.danger) }
            },
            dismissButton = { TextButton(onClick = { unpair = null }) { Text("Cancel") } },
        )
    }
}

/** Background copy detection needs READ_LOGS (adb, once) and the overlay permission (to open SendActivity). */
@Composable
private fun CopyFromAnyApp() {
    val ctx = LocalContext.current
    var tick by remember { mutableIntStateOf(0) }
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) { tick++ }
    val logs = remember(tick) { ClipService.canReadLogs(ctx) }
    val overlay = remember(tick) { android.provider.Settings.canDrawOverlays(ctx) }
    Column(side) {
    Column(Modifier.padding(vertical = 14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Text("Copy from any app", color = T.text, fontSize = 15.sp, lineHeight = 20.sp, modifier = Modifier.weight(1f))
            Text(if (logs && overlay) "On" else "Off", color = if (logs && overlay) T.positive else T.secondary, fontSize = 15.sp, lineHeight = 20.sp)
        }
        if (logs && overlay) {
            Text("Copies in other apps are sent even when Ctrl Link is closed.", color = T.secondary, fontSize = 13.sp, lineHeight = 18.sp)
        } else {
            Text("Two steps, once:", color = T.secondary, fontSize = 13.sp, lineHeight = 18.sp)
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Label("1", color = if (overlay) T.positive else T.text)
                if (overlay) Text("Display over other apps is allowed", color = T.secondary, fontSize = 13.sp, lineHeight = 18.sp)
                else PillButton("Allow display over other apps", filled = false, onClick = {
                    ctx.startActivity(android.content.Intent(android.provider.Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                        Uri.parse("package:${ctx.packageName}")))
                })
            }
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Label("2", color = if (logs) T.positive else T.text)
                Text(if (logs) "Log access is granted" else
                    "From your Mac, with the phone plugged in and USB debugging on, run tools/android-enable-copy-sync.sh",
                    color = T.secondary, fontSize = 13.sp, lineHeight = 18.sp)
            }
        }
        Text("Android 13 and later ask \"Allow Ctrl Link to access all device logs?\" when Ctrl Link starts. Tap Allow.",
            color = T.secondary, fontSize = 13.sp, lineHeight = 18.sp)
    }
    Hairline()
    }
}
