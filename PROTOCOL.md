# Continuity wire protocol (v1)

Everything stays on the local network. There is no cloud relay, account, or telemetry.

## Roles

| Role | Runs on | Listens on | Identity |
|---|---|---|---|
| **Hub** | Windows 11 | `wss://<ip>:47800/ws` (control plane) | Self-signed ECDSA P-256 cert, created on first launch |
| **Device** | Android 10+ | `https://<ip>:<fetch_port>/shot/<id>` (data plane) | Self-signed ECDSA P-256 cert in AndroidKeyStore |

`fingerprint` everywhere means **lowercase hex SHA-256 of the DER certificate** (64 chars, no colons).

## 1. Pairing (trust bootstrap)

The Hub shows a QR code containing:

```json
{"v":1,"ip":"192.168.1.20","port":47800,"fingerprint":"<hub cert sha256>",
 "hub_id":"<uuid>","name":"DESKTOP-1","pair_code":"<one-time secret>"}
```

* The device pins `fingerprint`. Every later TLS connection to the Hub must present that exact cert.
* `pair_code` is single-use and expires after 10 minutes (or when the pairing window closes).
  It answers *"is this device allowed to talk to the hub?"*, which the fingerprint alone does not:
  without it, any device on the Wi-Fi could connect and read the PC clipboard.
* After the first `hello`, the Hub issues a long-lived `device_token`. The Hub stores only its SHA-256.

## 2. Discovery

The Hub registers `<name>._continuity._tcp.local` through the Windows DNS-SD API (`DnsServiceRegister`)
with TXT records `v=1`, `id=<hub_id>`, `fp=<first 16 hex of fingerprint>`.
The device browses with `NsdManager`, picks the record whose `id` matches its paired hub, and
uses the resolved IP and port. The IP in the QR code is only the first hint. The pinned
fingerprint is still checked, so a spoofed mDNS answer can't redirect the device.

## 3. Control plane (WSS, JSON text frames)

Every sync payload carries `id` (uuid), `origin_device_id`, and `timestamp` (unix ms, origin clock).

### Device → Hub, first frame (within 10 s)
```json
{"type":"hello","device_id":"<uuid>","device_name":"Pixel 8",
 "fetch_port":47801,"fetch_fingerprint":"<device cert sha256>",
 "device_token":"<token>",            // or "pair_code":"<code>" on first pairing
 "features":["clip_text","screenshot"],"app_version":"0.2.0"}
```
Optional `"palette"` (Android 12+): the phone's Material You colours, so the hub can match the wallpaper:
`{"light":{"primary":"#745380","on_primary":"#FFF7FC","primary_container":"…","on_primary_container":"…","tertiary":"…","surface_container":"…"},"dark":{…}}`.
The hub keeps the last one it received and re-tints its accent with the half that matches Windows light/dark mode
(Settings → *Match my phone's colours*).

Optional `"clip_directions": {"to_pc": true, "from_pc": false}`: the phone's clipboard switches (Features →
Clipboard text). With `from_pc` false the hub sends that phone no `clip_text` at all, neither the PC's own copies
nor text relayed from other phones. Flipping a switch makes the phone reconnect so the new value is announced.
The hub has its own pair of switches (send this PC's copies / accept text from phones) that apply to every phone.

### Hub → Device
```json
{"type":"welcome","hub_id":"<uuid>","hub_name":"DESKTOP-1","features":["clip_text","screenshot"],
 "device_token":"<only when just paired>"}
{"type":"error","code":"unauthorized|bad_hello|pairing_closed","message":"..."}   // then close
```

### Feature negotiation
Each side lists the feature ids it has **enabled** (`features`). A feature only runs when both
sides list it. If a peer sends no `features` field, it is treated as v1 and assumed to support
`["clip_text","screenshot"]`. When a user flips a feature switch, the phone reconnects so the new
list is announced. Unknown message types are ignored, so a newer peer never breaks an older one.

| Feature id | Messages | Relayed between phones |
|---|---|---|
| `clip_text` | `clip_text` | yes |
| `screenshot` | `screenshot_ready` (plus the HTTPS data plane) | no |
| `file_drop` | `file_offer`, `file_status` (plus `/drop/` on both HTTPS servers), see §6 | no |

New features add their own id and message types. Pairing, TLS pinning and the loop guard are shared
by every feature: any message carrying `id` + `origin_device_id` + `timestamp` passes through the
loop guard automatically.
### Text clipboard (either direction; the Hub relays device→device, keeping the origin)
```json
{"type":"clip_text","id":"<uuid>","origin_device_id":"<uuid>","timestamp":1732100000000,"text":"hello"}
```
Max text size: 1 MiB (UTF-8).

### Screenshot pointer (Device → Hub)
```json
{"type":"screenshot_ready","id":"<uuid>","origin_device_id":"<uuid>","timestamp":1732100000000,
 "mime":"image/png","size":482113,"width":1080,"height":2400,"token":"<one-shot bearer>"}
```

## 4. Data plane (lazy fetch)

On `screenshot_ready`, the Hub takes clipboard ownership with **delayed rendering**:
`SetClipboardData(CF_DIB, NULL)` and `SetClipboardData("PNG", NULL)`. No bytes move yet.

When any app pastes (Ctrl + V), Windows sends `WM_RENDERFORMAT` to the Hub. The Hub then requests:

```
GET https://<device ip>:<fetch_port>/shot/<id>
Authorization: Bearer <token>
```

The Hub accepts the device's TLS cert only if its SHA-256 equals the `fetch_fingerprint` from `hello`.
That value reached the Hub over the already-pinned WSS channel, so trust is transitive from the QR scan.
The PNG/JPEG is decoded and placed as `CF_DIB` (32bpp, opaque) plus a `PNG` format.
Tokens expire after 10 minutes. The device keeps only the last 5 offers.

Lazy offers also set `CanIncludeInClipboardHistory=0` and `CanUploadToCloudClipboard=0`. That stops
Win+V history from rendering the image early, and keeps it out of Microsoft's cloud clipboard.
Synced text sets `CanUploadToCloudClipboard=0`.

## 5. Loop prevention

Both sides run the same `LoopGuard`:

1. Drop any incoming payload whose `origin_device_id` is our own id.
2. Drop a payload `id` we've already seen (bounded LRU of 512).
3. Drop a payload whose `timestamp` is older than the newest one seen **from the same origin**.
   Comparisons are only made within one origin, so clock skew between devices doesn't matter.
4. When we apply a remote payload to the local clipboard, remember its content hash. The OS then
   fires its own "clipboard changed" event, and if the local content hash equals that remembered
   hash, we don't re-broadcast. Any real local copy clears the remembered hash.
5. Windows also ignores `WM_CLIPBOARDUPDATE` when the Hub itself is the clipboard owner. Android tags
   the clips it writes with `ClipDescription` extras `continuity.origin` / `continuity.ts`.

Clipboard content that the source app marks private (`ExcludeClipboardContentFromMonitorProcessing`,
or `CanIncludeInClipboardHistory=0`, which password managers use) is never sent.

## 6. QuickDrop (feature `file_drop`)

Files and folders, in either direction. The sender announces; the receiver pulls. That way the same
pinned TLS identities protect the bytes, and the receiver controls the pace and the resume point.

### Offer (sender → receiver, over WSS)
```json
{"type":"file_offer","id":"<uuid>","origin_device_id":"<uuid>","timestamp":1732100000000,
 "token":"<random bearer, 32 bytes base64url>","label":"Photos","total_size":42250262,
 "files":[{"path":"Photos/a.jpg","size":300000},{"path":"Photos/sub/b.txt","size":1234}]}
```
* `path` is relative and `/`-separated; a folder keeps its top-level name. Receivers sanitise every
  segment (no roots, `.` or `..`, invalid characters replaced), so a peer can never write outside the
  QuickDrop folder. At most 10,000 files per offer.
* The hub sends a PC offer to every connected phone that announced `file_drop`. It does not relay phone offers.
* Offers and tokens stay valid for 24 hours, but only while the sending app keeps running.

### Download (receiver → sender, HTTPS)
```
GET https://<sender>/drop/<offer id>/<file index>
Authorization: Bearer <token>
Range: bytes=<bytes already saved>-        (only when resuming)
```
* **Phone → PC:** the hub pulls from the phone's fetch server, pinned to `fetch_fingerprint` (as in §4).
* **PC → phone:** the phone pulls from the hub's own port (47800), pinned to the hub fingerprint from the QR.
* Answers: `200` for the whole file, `206` with `Content-Range` when resuming, `416` if the range starts at
  the end, and `401` for a bad or expired token.
* **Resume:** partial data is kept (PC: `Downloads\QuickDrop\.partial\<id>_<i>.part`; phone: a pending MediaStore
  item). After a network error the receiver retries with backoff (1 s, doubling to 30 s), asks for the
  missing range, and uses the sender's *current* address. It gives up after 15 minutes without progress.
* Received files land in `Downloads\QuickDrop` on the PC and `Download/QuickDrop` on the phone. Name
  clashes become `name (1).ext`; existing files are never overwritten.

### Status (receiver → sender, over WSS)
```json
{"type":"file_status","offer_id":"<uuid>","state":"progress|done|failed","done_bytes":12345,
 "total_bytes":42250262,"files_done":1,"file_count":3,"message":"12.4 MB/s"}
```
Sent about every 0.7 s while downloading, then once with `done` or `failed`. It has no `id`/`origin_device_id`
envelope on purpose, so the loop guard (§5) never drops the repeated updates.

## 7. Screen share (feature `screen_share`)

The phone mirrors its screen to a viewer window on the PC. It's view-only, with no audio. The user starts
it on the phone, and Android asks for MediaProjection consent every time.

### Offer (phone → hub, over WSS)
```json
{"type":"screen_offer","id":"<session uuid>","origin_device_id":"<uuid>","timestamp":1732100000000,
 "token":"<random bearer>","width":576,"height":1280}
```
`width`/`height` are the stream size: the screen scaled so its longer side is 1280 px, or 1920 px in High quality.

### Stream (hub → phone, HTTPS, pinned to `fetch_fingerprint`)
```
GET https://<phone>:<fetch_port>/screen/<session id>
Authorization: Bearer <token>
```
The response body never ends. It's a sequence of frames, each a **4-byte big-endian length** followed by that
many bytes of **JPEG**. A length of `0` is a keep-alive, sent every 2 s when the picture hasn't changed. The
hub treats 8 s of silence as a dropped connection and reconnects. Frames are capped at about 24 fps, and the phone
encodes only while a viewer is connected. A viewer that falls behind gets the newest frame, not a backlog.
After rotation, frames simply change size.

### Stop (either direction, over WSS)
```json
{"type":"screen_stop","session_id":"<session uuid>"}
```
* The phone sends it when sharing ends (Stop in the app or notification, Android's own stop, or the feature switched off).
  The hub closes the viewer.
* The hub sends it when the user closes the viewer window, and the phone stops capturing.
* Once stopped, the token is dead: a new `GET /screen/` gets `401`.
