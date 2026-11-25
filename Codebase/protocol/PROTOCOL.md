# Ctrl Link Protocol v1

The single contract every Ctrl Link app (Mac, Android, later Windows) implements.
If an app and this file disagree, this file wins — change the file first, then the apps.

Byte values in examples are hex. `b64` = standard base64 with padding. `b64url` = URL-safe base64, no padding.
`||` = byte concatenation. All integers are big-endian. All JSON is UTF-8.

## 1. Identity

Each install creates once, and stores persistently:

- `deviceId` — random UUID v4, lowercase string form.
- `deviceName` — user-editable, defaults to the host/model name (Mac: `Host.current().localizedName`, Android: `Settings.Global.DEVICE_NAME` or `Build.MODEL`).
- `platform` — `mac` | `android` | `windows` | `other` (test peers, Linux). Treat unknown values as `other`.

## 2. Discovery (mDNS / DNS-SD)

- Service type: `_ctrllink._tcp`, domain `local.`
- Instance name: `deviceName` (the OS may de-duplicate it; never rely on it for identity).
- Port: try TCP `47823`; if busy, any free port. The advertised port is the truth.
- TXT record:

| key | value |
|---|---|
| `v` | `1` |
| `id` | `deviceId` |
| `pf` | `platform` |
| `pair` | `1` while a pairing window (QR or code) is open, else `0` |

Apps advertise and browse whenever they are running and the network is up.
When a browsed service's `id` matches a paired device, connect (see §5 tie-break).

## 3. Framing

A TCP connection carries frames: `len (uint32) || body`. `len` = body length, max `262144 + 64`.

- **Before the session is established** (pairing and session hello): `body` is a UTF-8 JSON object.
- **After** (§5): `body` = AES-256-GCM ciphertext||16-byte tag of a plaintext `kind (1 byte) || payload`:
  - `kind 0x01` — payload is a UTF-8 JSON control message (§6).
  - `kind 0x02` — payload is a file chunk: `fileId (16 bytes, raw UUID) || data` (data ≤ 262144 bytes).
- GCM nonce (12 bytes, never sent): `dir (4 bytes) || counter (uint64)`.
  `dir` = `00000001` for frames sent by the connection's **client** (the side that dialed), `00000002` for the **server**.
  Each direction keeps its own counter starting at 0, incremented per frame. No AAD.
- Any decrypt failure, oversize frame, unknown `kind` byte or malformed JSON → close the socket.

## 4. Pairing

Pairing produces a 32-byte `pairKey` that both sides store with the peer's `deviceId`, `deviceName`, `platform`.
Crypto: X25519 for key agreement, HKDF-SHA256, HMAC-SHA256. A fresh X25519 key pair is made per pairing attempt.

The device showing the QR/code is the **host**; the device scanning/typing is the **joiner**. Any platform can be either.

### 4.1 Opening a pairing window (host)

The host generates an X25519 key pair (`hostPriv`, `hostPub`), a 16-byte random `token`, and a 6-digit `code`
(uniform random `000000`–`999999`). It sets TXT `pair=1`. The window closes after 5 minutes, after one successful
pairing, or after 5 failed attempts. Then `pair=0` and the key pair, token and code are discarded.

QR content (also shown as copyable text):

```
ctrllink://pair?v=1&id=<deviceId>&n=<urlencoded deviceName>&pf=<platform>&h=<ip1>,<ip2>&p=<port>&k=<b64url hostPub>&t=<b64url token>
```

`h` is a comma-separated list of the host's current LAN addresses: IPv4 dotted, IPv6 in brackets (`[fe80::1%en0]`), percent-encoded as a URL query value. The joiner tries them in order, then falls back to mDNS lookup by `id`.

The host handles one pairing attempt at a time. A second `pair_req` while one is in progress gets `pair_fail` with reason `busy`.

### 4.2 Messages

The joiner dials the host and sends:

```json
{"t":"pair_req","v":1,"mode":"qr"|"code","id":"<joinerId>","name":"...","pf":"android","pub":"<b64 joinerPub>"}
```

The host replies:

```json
{"t":"pair_hello","v":1,"id":"<hostId>","name":"...","pf":"mac","pub":"<b64 hostPub>"}
```

Both compute:

```
shared   = X25519(myPriv, peerPub)
secret   = mode=="qr" ? token : UTF8(code)          // 16 bytes / 6 bytes
pairKey  = HKDF(ikm=shared, salt=secret, info="ctrllink pair v1" || hostPub || joinerPub, L=32)
sas      = uint32(HKDF(ikm=shared, salt=empty, info="ctrllink sas v1" || hostPub || joinerPub, L=4)) mod 1000000   // shown as 6 digits, split 3+3
```

QR mode: the joiner **must** check that `pair_hello.pub` equals `k` from the QR, else abort. No SAS screen is needed.

Code mode: both screens show `sas`. The host asks the user to accept "*<joiner name>* wants to pair — **482 193**".
The joiner shows "Confirm **482 193** on *<host name>*". (Without a PAKE, the 6-digit code alone can't stop an active
man-in-the-middle; comparing the SAS can.) The host waits for the user's choice for up to 60 s.

Then, in this order:

```json
joiner → {"t":"pair_confirm","mac":"<b64 HMAC(pairKey, "joiner" || hostPub || joinerPub)>"}
host   → {"t":"pair_ok","mac":"<b64 HMAC(pairKey, "host" || hostPub || joinerPub)>"}
```

The host verifies the joiner's MAC **first**. A bad MAC gets `bad_code` right away, with no user prompt. Only then, in code mode,
does it ask the user to accept. It sends `pair_ok` only after both checks pass. Otherwise it sends
`{"t":"pair_fail","reason":"bad_code"|"rejected"|"timeout"|"busy"|"closed"}` and closes.
The joiner verifies `pair_ok.mac`. Both store the pairing and close the pairing socket, which never carries a session.

Code mode with several `pair=1` hosts: the joiner tries them one at a time and stops at the first one that doesn't answer `bad_code`.
Wrong hosts reject quickly because the MAC check comes first. Each such try counts toward that host's 5 failed attempts; that's
accepted, since several open pairing windows at once is rare.

Right after pairing, the **joiner** dials a session (§5) to the address it paired on, ignoring the §5.1 tie-break this once.

## 5. Session

### 5.1 Who dials

Either side may dial the other when it sees it on mDNS. To avoid two connections, a device dials only if its `deviceId`
is lexicographically **lower** than the peer's. The higher one waits for 10 s and then dials anyway (the peer may not be
able to see it). If two sessions end up open between the same pair of devices, both sides keep the one whose client has the lower
`deviceId` and close the other. If both have the same client (a reconnect), keep the newest one.

### 5.2 Handshake

```json
client → {"t":"hello","v":1,"id":"<clientId>","nonce":"<b64 32 random bytes>"}
server → {"t":"hello","v":1,"id":"<serverId>","nonce":"<b64 32 random bytes>"}
```

The client closes the socket if the server's `id` isn't the peer it meant to dial.
If the server doesn't know `clientId`, it replies `{"t":"unknown"}` and closes (the client may then delete its stale
pairing after the user confirms). Otherwise both derive:

```
keys     = HKDF(ikm=pairKey, salt=clientNonce || serverNonce, info="ctrllink session v1", L=64)
c2sKey   = keys[0:32]      // client → server
s2cKey   = keys[32:64]     // server → client
```

From then on every frame is encrypted (§3). The first encrypted frame each way is a `ready` control message. A peer
that can't decrypt it closes the socket, so a successful `ready` proves both sides hold `pairKey`.

## 6. Control messages (kind 0x01)

| `t` | fields | meaning |
|---|---|---|
| `ready` | `name`, `pf`, `app` (version string) | first message after handshake |
| `ping` / `pong` | — | send `ping` if no frame has been received for 15 s. Close if no frame has been received for 45 s |
| `clip` | see below | new clipboard content |
| `accept` | `id` | receiver wants the files of clip `id` |
| `reject` | `id` | receiver declines |
| `done` | `id`, `fileId`, `sha256` (hex) | all chunks of one file sent |
| `cancel` | `id` | either side aborts a transfer. Delete partial files |
| `unpair` | — | peer forgot us. Delete the pairing, close, notify the user |

### 6.1 `clip`

```json
{"t":"clip","id":"<uuid>","ts":1731050000000,"kind":"text"|"url"|"image"|"files",
 "text":"...",                     // kind text|url only, max 192 KiB UTF-8 (must fit one frame); larger text is sent as kind "files" with one .txt file
 "files":[{"fileId":"<uuid>","name":"photo.heic","mime":"image/heic","size":123456}]}   // kind image|files
```

- `kind:"image"` = exactly one file that the receiver should put on the clipboard **as an image**, not as a file.
  Use it when the source clipboard held image data (screenshot, "Copy Image"). Encode it as PNG unless the original data was JPEG/HEIC.
- `kind:"files"` = one or more files copied in a file manager or shared, videos included.

### 6.2 Transfer flow

1. Sender sends `clip`. For `text`/`url` that's all.
2. For `image`/`files`: the receiver auto-accepts if the total size is ≤ its auto-accept limit (default 100 MB, user setting).
   Otherwise it shows a notification with Accept/Decline. It replies `accept` or `reject`. If the sender hears nothing for 5 minutes, it sends `cancel` and gives up.
3. On `accept`, the sender streams each file in order: chunks (kind 0x02), then `done` with the SHA-256 of the whole file.
4. The receiver writes chunks to a temp file. On `done` it checks the hash and moves the file into place.
   A hash mismatch or more bytes than the declared `size` → `cancel` that clip. A chunk for a `fileId` that belongs to no
   accepted clip is a protocol error → close the session. Exception: after a clip is cancelled (by either side), silently drop
   chunks and `done` for its fileIds that were already in flight.
5. If the session drops mid-transfer, both sides abort it and delete partial files. There is no resume.

### 6.3 Receiving

- `text`/`url`: write to the system clipboard (Mac: `NSPasteboard` string; a `url` is also written as a URL type). Notify "Text from *<name>*".
- `image`: put the image data on the clipboard. Also keep the file in the app's received folder.
- `files`: save to the received folder (Mac: `~/Downloads/Ctrl Link/`; Android: `Download/Ctrl Link/` via MediaStore).
  Mac also puts the file URLs on the pasteboard so ⌘V in Finder pastes them. Notify with an "Open"/"Show" action.
- File names are untrusted. Take the last path component, replace `/ \ :` and control characters with `_`, strip leading dots,
  and cap the name at 200 bytes. An empty result becomes `file`.
- Name clashes: append ` (1)`, ` (2)`, … before the extension.

### 6.4 Sending and loop prevention

- Sources: Mac watches `NSPasteboard.general.changeCount` (every 500 ms). Android reads the clipboard while the app is in the
  foreground and through the Share sheet, a Quick Settings tile and a notification action (Android 10+ blocks background clipboard reads).
- When an app writes received content to its own clipboard, it remembers the content's fingerprint
  (SHA-256 of the text, or of the first file's bytes) and does **not** re-send a local clipboard change with that fingerprint.
- New clipboard content is sent to every connected paired peer while "Auto-send" is on (default on).
  Paired peers that are offline get nothing. There is no queue.

## 7. Versioning

`v` in TXT, `pair_req`, `pair_hello` and `hello` is the protocol version. A peer that receives a higher `v` than it supports
closes the socket with `{"t":"error","reason":"version"}` (pre-session) or an `error` control message, and the UI says "Update Ctrl Link on *<name>*".
Unknown JSON fields are ignored. Unknown control `t` values are ignored.

## 8. Test vectors

`protocol/test-vectors.json` has fixed inputs and expected outputs for X25519, `pairKey`, `sas`, the pairing MACs, the session keys,
and the first two encrypted frames in each direction. Every implementation must have a unit test that reproduces them byte for byte.
The reference implementation is `protocol/test-peer/` (Node.js, built-ins only).
