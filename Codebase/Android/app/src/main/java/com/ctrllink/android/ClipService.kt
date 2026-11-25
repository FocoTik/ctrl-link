package com.ctrllink.android

import android.app.DownloadManager
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.ClipData
import android.content.ClipboardManager
import android.content.ContentValues
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.Network
import android.net.Uri
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.provider.Settings
import android.text.format.Formatter
import android.util.Log
import androidx.annotation.RequiresApi
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.FileProvider
import com.ctrllink.android.proto.Core
import com.ctrllink.android.proto.CoreListener
import com.ctrllink.android.proto.FileMeta
import com.ctrllink.android.proto.PairWindow
import com.ctrllink.android.proto.Peer
import com.ctrllink.android.proto.Received
import com.ctrllink.android.proto.Sink
import com.ctrllink.android.proto.dedup
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.FlowPreview
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.debounce
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import java.io.File
import java.io.FileOutputStream
import java.net.Inet4Address
import java.net.InetSocketAddress
import java.util.concurrent.ConcurrentHashMap
import kotlin.coroutines.resume

/** Foreground service: listener, mDNS, sessions, notifications, and writing received content. */
class ClipService : Service(), CoreListener {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val store get() = Hub.store
    private var core: Core? = null
    private lateinit var nm: NotificationManager
    private lateinit var nsd: NsdManager
    private lateinit var cm: ConnectivityManager
    private var lock: WifiManager.MulticastLock? = null
    private var reg: NsdManager.RegistrationListener? = null
    private var disc: NsdManager.DiscoveryListener? = null
    private val resolveQueue = Channel<NsdServiceInfo>(Channel.UNLIMITED)
    private val decisions = ConcurrentHashMap<String, CompletableDeferred<Boolean>>()
    private var netCb: ConnectivityManager.NetworkCallback? = null
    // Background copy detection (KDE Connect's method): a registered listener makes ClipboardService log a denial on
    // every copy while we're in the background; with READ_LOGS we see that line and open SendActivity to read the clip.
    private val copyListener = ClipboardManager.OnPrimaryClipChangedListener {}
    private var logcat: Process? = null
    private var logJob: Job? = null
    private var copyDebounce: Job? = null

    override fun onBind(intent: Intent?) = null

    @OptIn(FlowPreview::class)
    override fun onCreate() {
        super.onCreate()
        nm = getSystemService(NotificationManager::class.java)
        // A channel's sound can't change after creation, so the Notification sound setting picks between a
        // sounding channel (the original ids) and a silent twin per notification; see [channel].
        fun quiet(id: String, name: String, importance: Int) = NotificationChannel(id, name, importance).apply {
            setSound(null, null)
            enableVibration(false)
        }
        nm.createNotificationChannels(listOf(
            NotificationChannel(CH_STATUS, "Connection status", NotificationManager.IMPORTANCE_LOW),
            NotificationChannel(CH_EVENTS, "Clips and devices", NotificationManager.IMPORTANCE_DEFAULT),
            NotificationChannel(CH_ASK, "Incoming files", NotificationManager.IMPORTANCE_HIGH),
            quiet(CH_EVENTS_QUIET, "Clips and devices (silent)", NotificationManager.IMPORTANCE_DEFAULT),
            quiet(CH_ASK_QUIET, "Incoming files (silent)", NotificationManager.IMPORTANCE_HIGH),
        ))
        ServiceCompat.startForeground(this, ID_STATUS, status(emptySet()),
            if (Build.VERSION.SDK_INT >= 29) ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE else 0)
        lock = getSystemService(WifiManager::class.java).createMulticastLock("ctrllink").apply {
            setReferenceCounted(false)
            acquire()
        }
        nsd = getSystemService(NsdManager::class.java)
        cm = getSystemService(ConnectivityManager::class.java)
        scope.launch {
            val c = withContext(Dispatchers.IO) { Core(store.me, this@ClipService).also { it.start() } }
            core = c
            Hub.core.value = c
            for (i in resolveQueue) resolve(i)
        }
        scope.launch {
            Hub.core.collect { c ->
                if (c == null || netCb != null) return@collect
                // Fires once right away; again on every network change.
                netCb = object : ConnectivityManager.NetworkCallback() {
                    override fun onAvailable(network: Network) { scope.launch { restartNsd(); c.redialAll() } }
                }.also(cm::registerDefaultNetworkCallback)
            }
        }
        scope.launch { store.settings.map { it.name }.distinctUntilChanged().drop(1).debounce(1000).collect { register() } }
        scope.launch { Hub.online.collect { nm.notify(ID_STATUS, status(it)) } }
        scope.launch { sampleTransfers() }
        getSystemService(ClipboardManager::class.java).addPrimaryClipChangedListener(copyListener)
        if (canReadLogs(this)) logJob = scope.launch(Dispatchers.IO) { watchCopies() }
    }

    private suspend fun watchCopies() {
        val needle = "Denying clipboard access to $packageName"
        while (currentCoroutineContext().isActive) {
            runCatching {
                val p = ProcessBuilder("logcat", "-T", "1", "-s", "ClipboardService:E").redirectErrorStream(true).start()
                logcat = p
                p.inputStream.bufferedReader().forEachLine { if (needle in it) scope.launch { onCopiedElsewhere() } }
            }.onFailure { Log.w(TAG, "logcat reader stopped", it) }
            delay(2000) // reader died: restart
        }
    }

    private fun onCopiedElsewhere() {
        copyDebounce?.cancel()
        copyDebounce = scope.launch {
            delay(500)
            if (!store.settings.value.autoSend || !Settings.canDrawOverlays(this@ClipService)) return@launch
            startActivity(Intent(this@ClipService, SendActivity::class.java).setAction(SendActivity.ACT_CLIPBOARD)
                .putExtra(SendActivity.EXTRA_BACKGROUND, true)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_NO_ANIMATION))
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACT_ACCEPT -> decisions[intent.getStringExtra("id")]?.complete(true)
            ACT_DECLINE -> decisions[intent.getStringExtra("id")]?.complete(false)
            ACT_CANCEL -> intent.getStringExtra("id")?.let(::cancelTransfer)
            ACT_REPOST -> nm.notify(ID_STATUS, status(Hub.online.value)) // swiped away: Android 14+ allows that even for ongoing ones
        }
        return START_STICKY
    }

    override fun onDestroy() {
        getSystemService(ClipboardManager::class.java).removePrimaryClipChangedListener(copyListener)
        logJob?.cancel()
        logcat?.destroy()
        netCb?.let { runCatching { cm.unregisterNetworkCallback(it) } }
        reg?.let { runCatching { nsd.unregisterService(it) } }
        disc?.let { runCatching { nsd.stopServiceDiscovery(it) } }
        lock?.release()
        core?.let { c -> Thread { c.stop() }.start() }
        Hub.core.value = null
        Hub.online.value = emptySet()
        scope.cancel()
        super.onDestroy()
    }

    // ---- mDNS (§2) ----

    private fun restartNsd() {
        register()
        disc?.let { runCatching { nsd.stopServiceDiscovery(it) } }
        Hub.found.value = emptyMap()
        disc = object : NsdManager.DiscoveryListener {
            override fun onServiceFound(info: NsdServiceInfo) { resolveQueue.trySend(info) }
            override fun onServiceLost(info: NsdServiceInfo) { Hub.found.update { m -> m.filterValues { it.name != info.serviceName } } }
            override fun onDiscoveryStarted(serviceType: String) {}
            override fun onDiscoveryStopped(serviceType: String) {}
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) { Log.w(TAG, "discovery failed $errorCode") }
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {}
        }.also { nsd.discoverServices(TYPE, NsdManager.PROTOCOL_DNS_SD, it) }
    }

    private fun register() {
        val c = core ?: return
        reg?.let { runCatching { nsd.unregisterService(it) } }
        val info = NsdServiceInfo().apply {
            serviceName = store.me.name
            serviceType = TYPE
            port = c.port
            setAttribute("v", "1")
            setAttribute("id", store.me.id)
            setAttribute("pf", "android")
            setAttribute("pair", if (c.window != null) "1" else "0")
        }
        reg = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(info: NsdServiceInfo) {}
            override fun onRegistrationFailed(info: NsdServiceInfo, errorCode: Int) { Log.w(TAG, "register failed $errorCode") }
            override fun onServiceUnregistered(info: NsdServiceInfo) {}
            override fun onUnregistrationFailed(info: NsdServiceInfo, errorCode: Int) {}
        }.also { nsd.registerService(info, NsdManager.PROTOCOL_DNS_SD, it) }
    }

    @Suppress("DEPRECATION") // resolveService is fine for one-at-a-time resolution on every API level we support
    private suspend fun resolve(info: NsdServiceInfo) {
        val r = withTimeoutOrNull(10_000) {
            suspendCancellableCoroutine<NsdServiceInfo?> { c ->
                nsd.resolveService(info, object : NsdManager.ResolveListener {
                    override fun onServiceResolved(s: NsdServiceInfo) { if (c.isActive) c.resume(s) }
                    override fun onResolveFailed(s: NsdServiceInfo, errorCode: Int) { if (c.isActive) c.resume(null) }
                })
            }
        } ?: return
        val id = r.attributes["id"]?.let { String(it) } ?: return
        // Prefer IPv4: it dials without a scope id and is what people recognise on the Devices graph.
        val hosts = if (Build.VERSION.SDK_INT >= 34) r.hostAddresses else listOfNotNull(r.host)
        val host = hosts.firstOrNull { it is Inet4Address } ?: hosts.firstOrNull() ?: return
        if (id == store.me.id) return
        val addr = InetSocketAddress(host, r.port)
        Hub.found.update { it + (id to Found(id, r.serviceName, addr, r.attributes["pair"]?.let { String(it) } == "1")) }
        core?.seen(id, addr)
    }

    // ---- CoreListener ----

    override fun peer(id: String) = store.peers.value[id]
    override fun savePeer(p: Peer) = store.savePeer(p)
    override fun removePeer(id: String) = store.removePeer(id)

    override suspend fun confirmPair(name: String, sas: String): Boolean {
        val p = PairPrompt(name, sas, CompletableDeferred())
        Hub.pairPrompt.value = p
        try {
            return p.answer.await()
        } finally {
            Hub.pairPrompt.compareAndSet(p, null)
        }
    }

    override fun onPaired(p: Peer) { Hub.paired.tryEmit(p) }

    override fun onPairWindow(w: PairWindow?) {
        Hub.pairWindow.value = w
        scope.launch { register() } // TXT pair=1/0
    }

    override fun onConnected(p: Peer) {
        Hub.online.value = core?.sessions?.keys?.toSet().orEmpty()
        notify(("c" + p.id).hashCode(), "Connected to ${p.name}")
    }

    override fun onDisconnected(p: Peer, sessionMs: Long) {
        Hub.online.value = core?.sessions?.keys?.toSet().orEmpty()
        if (sessionMs > 10_000) notify(("c" + p.id).hashCode(), "Disconnected from ${p.name}")
    }

    override fun onText(from: Peer, kind: String, text: String) {
        Hub.lastFp = Send.fp(text)
        getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("Ctrl Link", text))
        store.addHistory(HistoryItem(kind, text, null, text.take(200), from.name, sent = false, ts = System.currentTimeMillis()))
        notify(ID_CLIP, "Copied from ${from.name}", text.take(120))
    }

    override suspend fun acceptFiles(from: Peer, clipId: String, files: List<FileMeta>): Boolean {
        val total = files.sumOf { it.size }
        if (total <= store.settings.value.limitMb * 1_000_000L) return true
        val d = CompletableDeferred<Boolean>()
        decisions[clipId] = d
        val offer = FileOffer(clipId, from.name, files.map { it.name }, total, d)
        Hub.offers.update { it + offer }
        val nid = clipId.hashCode()
        nm.notify(nid, builder(CH_ASK)
            .setContentTitle("${from.name} wants to send ${if (files.size == 1) files[0].name else "${files.size} files"}")
            .setContentText(Formatter.formatFileSize(this, total))
            .setContentIntent(activityPi(Intent(this, MainActivity::class.java)))
            .addAction(0, "Accept", servicePi(ACT_ACCEPT, clipId))
            .addAction(0, "Decline", servicePi(ACT_DECLINE, clipId))
            .setTimeoutAfter(5 * 60_000)
            .build())
        return try {
            withTimeoutOrNull(5 * 60_000) { d.await() } ?: false
        } finally {
            decisions.remove(clipId)
            Hub.offers.update { it - offer }
            nm.cancel(nid)
        }
    }

    override fun openSink(from: Peer, kind: String, f: FileMeta): Sink = when {
        kind == "image" -> fileSink(File(filesDir, "received"), f.name)
        Build.VERSION.SDK_INT >= 29 -> mediaSink(f)
        // shortcut: API 26–28 save to app-specific Downloads (no storage permission); move to public Downloads if needed
        else -> fileSink(File(getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS), "Ctrl Link"), f.name)
    }

    private fun fileSink(dir: File, name: String): Sink {
        dir.mkdirs()
        val file = synchronized(this) { File(dir, dedup(name) { File(dir, it).exists() }).also { it.createNewFile() } }
        return object : Sink {
            override val out = FileOutputStream(file)
            override fun commit(): Uri = FileProvider.getUriForFile(this@ClipService, "$packageName.files", file)
            override fun abort() { file.delete() }
        }
    }

    @RequiresApi(29)
    private fun mediaSink(f: FileMeta): Sink {
        val col = MediaStore.Downloads.EXTERNAL_CONTENT_URI
        val dir = Environment.DIRECTORY_DOWNLOADS + "/Ctrl Link/"
        val uri = synchronized(this) {
            val name = dedup(f.name) { n ->
                contentResolver.query(col, arrayOf(MediaStore.MediaColumns._ID),
                    "${MediaStore.MediaColumns.RELATIVE_PATH}=? AND ${MediaStore.MediaColumns.DISPLAY_NAME}=?", arrayOf(dir, n), null
                )?.use { it.count > 0 } == true
            }
            contentResolver.insert(col, ContentValues().apply {
                put(MediaStore.MediaColumns.DISPLAY_NAME, name)
                put(MediaStore.MediaColumns.MIME_TYPE, f.mime)
                put(MediaStore.MediaColumns.RELATIVE_PATH, dir)
                put(MediaStore.MediaColumns.IS_PENDING, 1)
            })!!
        }
        return object : Sink {
            override val out = contentResolver.openOutputStream(uri)!!
            override fun commit(): Uri {
                contentResolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null)
                return uri
            }
            override fun abort() { contentResolver.delete(uri, null, null) }
        }
    }

    override fun onFiles(from: Peer, kind: String, files: List<Received>) {
        val first = files.first()
        val uri = first.result as Uri
        store.addHistory(HistoryItem(kind, null, uri.toString(),
            if (files.size == 1) first.meta.name else "${first.meta.name} and ${files.size - 1} more", from.name, false, System.currentTimeMillis()))
        if (kind == "image") {
            Hub.lastFp = first.sha256
            getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newUri(contentResolver, "Image", uri))
            notify(ID_CLIP, "Image copied from ${from.name}", first.meta.name)
            return
        }
        val open = if (files.size == 1) {
            Intent(Intent.ACTION_VIEW).setDataAndType(uri, first.meta.mime).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        } else {
            Intent(DownloadManager.ACTION_VIEW_DOWNLOADS)
        }.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        val pi = PendingIntent.getActivity(this, uri.hashCode(), open, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        nm.notify(uri.hashCode(), builder(CH_EVENTS)
            .setContentTitle(if (files.size == 1) "File from ${from.name}" else "${files.size} files from ${from.name}")
            .setContentText(files.joinToString { it.meta.name })
            .setContentIntent(pi)
            .addAction(0, "Open", pi)
            .setAutoCancel(true)
            .build())
    }

    /** Cancel from the Devices screen or the progress notification. */
    private fun cancelTransfer(clipId: String) {
        core?.cancel(clipId)
        Hub.transfers.clear(clipId)
        nm.cancel(("p$clipId").hashCode())
    }

    override fun onProgress(from: Peer, sending: Boolean, clipId: String, name: String, done: Long, total: Long) {
        if (total >= 5_000_000) Hub.transfers.progress(clipId, name, from.name, sending, done, total)
    }

    override fun onTransferEnd(clipId: String, sending: Boolean, ok: Boolean) {
        if (!Hub.transfers.end(clipId, ok)) return
        nm.cancel(("p$clipId").hashCode())
        // Hold 100 % briefly so the finish registers, then the Devices screen returns.
        if (ok) scope.launch { delay(1200); Hub.transfers.clear(clipId) }
    }

    /** Once a second: a speed sample for the graph (also while the app is closed) and the progress notification. */
    private suspend fun sampleTransfers() {
        while (currentCoroutineContext().isActive) {
            delay(1000)
            val t = Hub.transfers.tick() ?: continue
            if (t.finished) continue
            nm.notify(("p${t.clipId}").hashCode(), builder(CH_STATUS)
                .setContentTitle(t.name)
                .setContentText("${Formatter.formatFileSize(this, t.done)} of ${Formatter.formatFileSize(this, t.total)}")
                .setProgress(1000, (t.done * 1000 / t.total).toInt(), false)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .addAction(0, "Cancel", servicePi(ACT_CANCEL, t.clipId))
                .setTimeoutAfter(30_000) // safety net if no end ever arrives
                .build())
        }
    }

    override fun onUnknown(peerId: String) {
        val name = peer(peerId)?.name ?: "A paired device"
        notify(("c$peerId").hashCode(), "$name no longer knows this phone", "Unpair it in Settings, then pair again")
    }

    override fun onUnpaired(p: Peer) = notify(("c" + p.id).hashCode(), "${p.name} unpaired this phone")

    override fun onVersion(name: String) = notify(ID_CLIP, "Update Ctrl Link on $name", "It uses a newer protocol version")

    // ---- notifications ----

    private fun builder(channel: String) = NotificationCompat.Builder(this, channel(channel)).setSmallIcon(R.drawable.ic_stat)

    /** Events and file prompts go to the silent twin channel when Notification sound is off. */
    private fun channel(c: String) = if (store.settings.value.sound) c else when (c) {
        CH_EVENTS -> CH_EVENTS_QUIET
        CH_ASK -> CH_ASK_QUIET
        else -> c
    }

    private fun notify(id: Int, title: String, text: String? = null) = nm.notify(id, builder(CH_EVENTS)
        .setContentTitle(title).setContentText(text).setAutoCancel(true)
        .setContentIntent(activityPi(Intent(this, MainActivity::class.java)))
        .build())

    private fun status(online: Set<String>) = builder(CH_STATUS)
        .setContentTitle(when (online.size) {
            0 -> "No devices connected"
            1 -> "Connected to ${peer(online.first())?.name ?: "1 device"}"
            else -> "Connected to ${online.size} devices"
        })
        .setOngoing(true)
        .setDeleteIntent(servicePi(ACT_REPOST, ""))
        .setShowWhen(false)
        .setContentIntent(activityPi(Intent(this, MainActivity::class.java)))
        .addAction(0, "Send clipboard", activityPi(Intent(this, SendActivity::class.java).setAction(SendActivity.ACT_CLIPBOARD)))
        .build()

    private fun activityPi(i: Intent) = PendingIntent.getActivity(this, i.component.hashCode(), i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)

    private fun servicePi(action: String, id: String) = PendingIntent.getService(this, (action + id).hashCode(),
        Intent(this, ClipService::class.java).setAction(action).putExtra("id", id), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)

    companion object {
        const val TAG = "Ctrl Link"

        fun canReadLogs(c: android.content.Context) =
            c.checkSelfPermission(android.Manifest.permission.READ_LOGS) == android.content.pm.PackageManager.PERMISSION_GRANTED
        const val TYPE = "_ctrllink._tcp"
        const val CH_STATUS = "status"
        const val CH_EVENTS = "events"
        const val CH_ASK = "ask"
        const val CH_EVENTS_QUIET = "events_quiet"
        const val CH_ASK_QUIET = "ask_quiet"
        const val ACT_CANCEL = "cancel_transfer"
        const val ID_STATUS = 1
        const val ID_CLIP = 2
        const val ACT_ACCEPT = "accept"
        const val ACT_DECLINE = "decline"
        const val ACT_REPOST = "repost_status"
    }
}
