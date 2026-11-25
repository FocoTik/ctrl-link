# Ctrl Link test peer

Reference implementation of [`../PROTOCOL.md`](../PROTOCOL.md) and a scriptable peer for testing the Mac and Android apps.
Node 24, built-ins only. mDNS uses macOS `dns-sd`. The pairing QR uses `qrcode-terminal`.

```sh
cd protocol/test-peer && npm install      # only installs qrcode-terminal
npm test                                  # node --test: vectors, in-process peers, dns-sd smoke test
```

## Commands

```sh
node ctrllink-peer.mjs host [--port 47823] [--auto-accept]   # pairing window: prints URI, QR, 6-digit code; then session peer
node ctrllink-peer.mjs join 'ctrllink://pair?...'            # QR/URI pairing: dials the hosts in h=, falls back to mDNS
node ctrllink-peer.mjs join 123456                           # code pairing: browses for pair=1 hosts, prints the SAS
node ctrllink-peer.mjs run                                   # advertise + browse + auto-connect to paired peers (§5.1)
```

Common options: `--state <dir>` (default `./.peer-state`: `identity.json`, `pairings.json`, `received/`, `tmp/`),
`--name`, `--pf mac|android|windows` (default `mac`), `--accept-limit-mb N` (default 100), `--json`,
`--connect host:port` (dial this address and skip mDNS; with `run`/`join` it also redials every 3 s), `--no-mdns`.
Use a separate `--state` for each peer you run on the same machine.

In code mode the host asks `Accept <name> SAS 1234? [y/N]`. Answer on stdin, or pass `--auto-accept`.
Offers over `--accept-limit-mb` also ask on stdin.

Stdin while running:

| input | effect |
|---|---|
| any line | text clip (`url` if it is a single http(s) URL; more than 192 KiB goes as kind `files`, `clipboard.txt`) |
| `/file <path...>` | kind `files` (quote paths with spaces) |
| `/image <file.png>` | kind `image` |
| `/peers` | list pairings and whether they are online |
| `/unpair <id>` | send `unpair` and forget the peer |
| `/cancel <clipId>` | cancel a transfer in either direction |
| `y` / `n` | answer the pending prompt |

Received files go to `<state>/received/` after the SHA-256 check (name clashes get ` (1)`). Partial files live in `<state>/tmp/` and are deleted on cancel or disconnect.

## `--json` events

One JSON object per line on stdout, always with `ev`:

| ev | fields |
|---|---|
| `listening` | `id`, `name`, `port` |
| `pairing` | `uri`, `code`, `port` |
| `peer` | mDNS sighting: `id`, `name`, `host`, `port`, `pf`, `pair`, `paired` |
| `sas` | `sas`, `id`, `name`, `mode` |
| `prompt` | `kind` (`pair`\|`accept`), `question`, details. Answer `y`/`n` on stdin |
| `paired` / `pair_failed` | `id`, `name`, `pf`, `role` / `reason` (`bad_code`, `rejected`, `timeout`, `closed`, `key_mismatch`, `bad_mac`, …) |
| `window_closed` | pairing window closed |
| `connected` / `disconnected` | `id`, `name`, `pf`, `app`, `role` (`client`\|`server`) / `reason` (`closed`, `timeout`, `unpaired`, `decrypt`, …) |
| `unknown` | the server said `{"t":"unknown"}`, or we said it to a client |
| `clip` | `from`, `name`, `id`, `kind` (`text`\|`url`), `text` |
| `offer` | `from`, `id`, `kind`, `size`, `files[]`, `auto` (true if under the limit) |
| `file` | `from`, `id`, `kind`, `fileId`, `name`, `path`, `size`, `sha256` (already verified) |
| `received` | all files of clip `id` arrived |
| `sent` / `offered` | `id`, `to`, `kind` |
| `rejected` / `declined` / `cancelled` | `id` (peer rejected ours / we declined theirs / `by` `me`\|`peer`) |
| `unpaired` | `id`, `by` |
| `error` | `message` |

Example: `node ctrllink-peer.mjs run --json | jq -c 'select(.ev=="clip")'`.

## Pair with the Mac app

Both on the same LAN (or the same Mac, using a separate `--state`):

```sh
node ctrllink-peer.mjs host --name "Test Peer"   # scan or paste the URI in the Mac app, or type the code there
# or, when the Mac app shows a code/QR:
node ctrllink-peer.mjs join 123456               # compare the SAS on both screens
node ctrllink-peer.mjs join 'ctrllink://pair?...'
node ctrllink-peer.mjs run                       # later: reconnects through mDNS
```

## Pair with the Android emulator

Emulators can't see host mDNS, so use adb port mapping and `--connect`.

The test peer as host, with the emulator joining. The app dials `127.0.0.1:47823` on the device:

```sh
adb reverse tcp:47823 tcp:47823
node ctrllink-peer.mjs host --port 47823 --no-mdns   # the URI's h= has LAN IPs; the app should dial 127.0.0.1:47823
```

The emulator app as host, with the test peer joining. The app listens on 47823 inside the emulator:

```sh
adb forward tcp:47823 tcp:47823
node ctrllink-peer.mjs join 123456 --connect 127.0.0.1:47823 --port 47824
node ctrllink-peer.mjs run --connect 127.0.0.1:47823 --port 47824     # later sessions
```

For sessions the emulator dials, keep `adb reverse` pointing at the test peer's port.
