package com.ctrllink.android.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.ctrllink.android.R

/** The merged monogram (design/logo/options/6-merged-monogram), the same glyph as the status-bar icon. */
@Composable
private fun Monogram(size: Int) = Icon(painterResource(R.drawable.ic_stat), null, Modifier.size(size.dp), tint = T.text)

/**
 * In-app splash while the service starts (Figma 80:2449): the 160 dp merged mark (dotted C and loop) over serif
 * "Ctrl Link", centred. On Android 12+ the system splash before it is plain background (themes v31), so nothing shows twice.
 */
@Composable
fun Splash() {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
            Icon(painterResource(R.drawable.ic_mark), null, Modifier.size(160.dp), tint = T.text)
            Text("Ctrl Link", color = T.text, fontFamily = Serif, fontSize = 34.sp, lineHeight = 40.sp)
        }
    }
}

/** First run only (Figma 80:2483). */
@Composable
fun Welcome(onPair: () -> Unit, onSkip: () -> Unit) {
    Column(Modifier.fillMaxSize()) {
        Row(Modifier.padding(start = 24.dp, end = 24.dp, top = 16.dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Monogram(20)
            Text("Ctrl Link", color = T.text, fontSize = 15.sp, lineHeight = 20.sp)
        }
        Column(
            Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(start = 24.dp, end = 24.dp, top = 40.dp, bottom = 16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Label("Welcome to Ctrl Link")
            Headline("Copy on one device, paste on another", Modifier.semantics { heading() }, align = TextAlign.Start)
            Text("Everything stays on your local network and is encrypted. Nothing is sent to the internet.",
                color = T.secondary, fontSize = 15.sp, lineHeight = 20.sp)
            Spacer(Modifier.height(12.dp))
            Column {
                listOf(
                    "Open Ctrl Link on another device on this network.",
                    "Pair the two with a code or a QR code.",
                    "Copy as usual. It shows up on the other device.",
                ).forEachIndexed { i, step ->
                    Hairline()
                    Row(Modifier.padding(vertical = 14.dp), horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                        Text("${i + 1}", color = T.text, fontFamily = Serif, fontSize = 26.sp, lineHeight = 32.sp)
                        Text(step, color = T.text, fontSize = 15.sp, lineHeight = 20.sp, modifier = Modifier.weight(1f).padding(top = 6.dp))
                    }
                }
                Hairline()
            }
        }
        Column(Modifier.padding(start = 24.dp, end = 24.dp, bottom = 16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text("Ctrl Link keeps running in the background and shows a notification while it syncs.",
                color = T.secondary, fontSize = 13.sp, lineHeight = 18.sp)
            PillButton("Pair a device", filled = true, onClick = onPair, modifier = Modifier.fillMaxWidth())
            PillButton("Skip for now", filled = false, onClick = onSkip, modifier = Modifier.fillMaxWidth())
        }
    }
}
