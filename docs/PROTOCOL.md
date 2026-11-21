# CamStudio Pro wire protocol (v1)

All traffic stays on the local network. Ports: 47800 UDP discovery, 47801 TCP phone
session. A phone session accepts one phone at a time.

## TCP session framing

Each message is a one-byte type, four-byte unsigned big-endian payload length, then payload.
Maximum payload is 8 MiB. JSON payloads are UTF-8; unknown optional fields/commands are ignored.

| Type | Name | Payload |
|---|---|---|
| 0x01 | HELLO | JSON with `v`, `deviceId`, `deviceName`, `model`, optional `transport`, `pairKey` and `auto` |
| 0x02 | HELLO_ACK | JSON with `ok`; on success `pcId`, `pcName`, `os`; otherwise `reason` |
| 0x03 | VIDEO_CONFIG | `{"codec":"h264","width":1920,"height":1080,"fps":30,"bitrate":8000000}` |
| 0x04 | VIDEO_FRAME | `u64 ptsUs`, `u8 flags`, H.264 Annex-B bytes |
| 0x05 | AUDIO_CONFIG | `{"sampleRate":48000,"channels":1,"format":"pcm_s16le"}` |
| 0x06 | AUDIO_FRAME | `u64 ptsUs`, PCM bytes |
| 0x07 | PING | `u64 nonce` |
| 0x08 | PONG | Echo of the PING nonce |
| 0x09 | STATE | Phone status JSON |
| 0x0A | STATS | PC statistics JSON |
| 0x0B | CONTROL | JSON with `cmd` and optional command fields |

### Handshake

1. Phone connects and sends HELLO.
2. PC answers HELLO_ACK. An unknown device without a valid pairing key requires user approval;
   the phone waits up to 60 seconds. Rejections include `version`, `busy`, and `denied`.
   A HELLO with `"auto":true` is an unattended reconnect: the PC never asks its user and instead
   rejects an untrusted phone with `untrusted`. Older PCs ignore the field and ask as usual.
3. On success the phone sends VIDEO_CONFIG, AUDIO_CONFIG if enabled, then frames.

### Video

H.264 Annex-B, one access unit per VIDEO_FRAME. Flag bit 0 is IDR/keyframe; bit 1 is codec
configuration (SPS/PPS). The phone sends SPS/PPS before the first IDR and repeats them before
each IDR. VIDEO_CONFIG may change during the session; an IDR follows a configuration change.
PC sends CONTROL `keyframe` when its decoder needs to resynchronize. CONTROL `pause`/`resume`
from the phone pauses video while audio continues; Windows shows a paused card.

### Audio

48 kHz mono signed 16-bit little-endian PCM, 20 ms per AUDIO_FRAME. Phone muting stops audio
frames. Timestamps are in microseconds; integers in message headers/timestamps are big-endian.

### STATE (phone to PC)

```json
{"battery":78,"charging":true,"tempC":39.0,"wifiRssi":-48,"wifiBand":"5 GHz",
 "muted":false,"paused":false,"sentFps":30,"bitrateBps":6200000,"rttMs":4,"encodeMs":12}
```

### STATS (PC to phone)

```json
{"recvFps":30,"bitrateBps":6200000,"droppedFrames":3,"totalFrames":75600,
 "pipelineMs":9}
```

`pipelineMs` measures receive to decoded frame on the PC. The phone estimates
delay as `encodeMs + rttMs/2 + pipelineMs`.

### Beauty effects (optional extension, version 1)

The phone applies effects before encoding. Periodic STATE and immediate CONTROL
`{"cmd":"beautyState",...}` reports include:

```json
{"beautyVersion":1,"beauty":{"enabled":false,"faceSlim":0,"jawSlim":0,"chinLength":0,
 "noseWidth":0,"noseBridge":0,"noseAlar":0,"noseLength":0,"noseRadix":0,"noseTip":0,
 "noseContour":0,"smile":0,"eyeSize":0,"lipFullness":0,"skinSmooth":0,"skinBrightness":0},
 "beautyAck":"","beautyOriginal":false,"beautyTracking":"Off"}
```

The phone owns/persists settings. Windows enables editing after receiving `beautyVersion:1`
and never overwrites phone settings merely on connection. Both UIs can edit. Windows sends
partial patches, at most once per 50 ms while dragging:

```json
{"cmd":"beauty","changes":{"enabled":true,"noseWidth":25},"requestId":"unique-edit-id"}
```

Android merges recognized, correctly typed fields into its current settings on the main
thread, clamps values, persists them, and immediately echoes the request ID in `beautyAck`.
Edits made on the phone itself are reported at most every 100 ms while a slider is dragged
(the phone's own picture still updates every frame); persistence is deferred until edits settle.
Unknown fields and invalid types are ignored. Windows waits for its latest acknowledgement
before synchronizing sliders. A three-second timeout restores the phone's reported settings.
`chinLength`, `noseWidth`, `noseBridge`, `noseAlar`, `noseLength`, `noseRadix`, `noseTip`,
`lipFullness` range from -100 to 100; all other sliders range from 0 to 100. `noseBridge` and
`noseContour` were added later within version 1: older apps ignore them, and a missing value is 0. Positive widths narrow, lengths
lengthen, and lifts lift. Zero is neutral. Effects default to disabled.

Windows sends CONTROL `{"cmd":"beautyCompare","original":true}` every 200 ms while holding
Original, then `original:false` on release, capture loss, hiding, or window deactivation.
Each true message grants a 600 ms lease; losing the PC or release message cannot leave
comparison enabled. It bypasses beauty on the preview and stream, retaining other effects.
Comparison is never persisted. Older clients ignore the optional fields/commands.

### Closing

Either side sends CONTROL `bye` and closes. Five seconds with no incoming traffic counts
as a lost connection.

### Video quality and backgrounds (optional v1)

STATE advertises `videoOptionsVersion:1`, `videoResolution` (requested capture short side),
`videoBackground` (Bg enum), `videoNatural`, `videoOptionsAck`, and `videoOptionsError`.
Older phones omit this capability, so desktop controls remain disabled.
Windows sends CONTROL `{"cmd":"videoOptions","changes":{"resolution":1080,"background":2,"natural":true},"requestId":"unique-id"}`.
The phone validates all requested changes before mutating state. It accepts 720/1080 at 30fps;
raises a too-low bitrate target to 4/8Mbps respectively without lowering higher settings;
preserves unrelated beauty, exposure and microphone settings. Backgrounds 0/1/2/3 are original,
blur, photographic office and photographic library; 4/6 require a photo/captured room already
selected on the phone. Missing assets return an error without changing capture quality.
Natural blending applies depth35, lighting65, grain30, light-wrap20 and edge85. Disabling it
zeros depth/lighting/grain/wrap. Confirmation is CONTROL `videoOptionsState`, including the
same report fields. Desktop times out after 5 seconds; frame dimensions remain authoritative
for actual transmitted quality. Meeting apps negotiate virtual-camera output independently.

Background-only edits omit `resolution`. Android 1.1.1+ treats these as effect-only changes:
it does not call setSettings or restart the encoder and preserves even 4K/60fps settings.
The optional `videoFps` state field reports the configured capture frame rate. Desktop
background and natural-blend selections send these patches immediately, including while live.

## Standby and explicit capture (Android 1.2 / Windows 1.4)

Both HELLO and HELLO_ACK require `cameraControlVersion:1`. A peer without that capability
is rejected with `reason:"version"`, rather than accepting a phone that captures automatically.
The socket remains connected in standby. Every new connection/reconnection starts with
camera, encoder and microphone off; opening the phone preview alone does not activate capture.

PC CONTROL: `{"cmd":"camera","cameraControlVersion":1,"enabled":true,"requestId":"unique-id"}`.
Phone CONTROL: `{"cmd":"cameraState","cameraControlVersion":1,"enabled":true,"requestId":"unique-id","error":""}`.
`enabled:false` releases capture, encoder, microphone, orientation listener, background
video playback and streaming Wi-Fi/wake locks. It flushes queued media/delayed audio but
keeps control messages and the low-traffic connection alive. Permission/encoder errors
return disabled state with a message. Phone-local explicit camera actions use an empty
requestId; periodic STATE also includes cameraControlVersion and cameraEnabled.

Windows ignores stale request acknowledgements, late decoded frames from before the latest
capture start, and media received while capture is disabled. A new VIDEO_CONFIG and keyframe
start each enabled capture. Camera-only video may stop while screen composition continues.
Screen recording/streaming does not require an active phone camera. Bubble visibility is
local to Windows and independent for preview/meetings, recordings and streams (defaults off);
enabling a bubble never implicitly opens the phone camera.
