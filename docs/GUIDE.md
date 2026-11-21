# CamStudio Pro user & developer guide

The full manual: how each feature works, how to build and package the apps, and how to test them.
Back to the [README](../README.md).

## How it works

1. The phone encodes its camera as low-latency H.264 and sends it, plus 48 kHz mic audio, over
   one TCP connection (port 47801). It finds PCs with a UDP broadcast (port 47800), a QR code
   shown by the PC, a typed IP address, or USB (`adb reverse`).
2. The PC client decodes the video with the Media Foundation H.264 decoder.
3. The PC shows the live preview, records MP4 files, and plays the phone audio into a playback
   device you pick.

Nothing leaves the local network.

## Microsoft Store installer (MSI)

The selected distribution route is one self-contained x64 MSI that installs the app and native
virtual camera together. No OBS, separate camera installer, or MSIX custom capability is used.
Build a local candidate with `windows\build-msi.ps1 -Version 1.2.1`.
See `windows/packaging/msi/STORE.md` for install/test, publisher signing, and Store hosting.
The older MSIX route below is retained for reference; its camera activation failed in testing.

## Windows client

Requirements: Windows 11, the .NET 10 SDK to build, and the .NET 10 Desktop Runtime x64 for
the developer build (the Store package is self-contained).

```powershell
windows\build-msix.ps1 -Version 1.1.0.0   # Microsoft Store package -> windows\dist\msix\*.msix
windows\build.ps1                         # developer build -> windows\dist\CamStudioPro\CamStudioPro.exe
```

The Store package is self-contained, so users don't need to install .NET for the app.
`windows/packaging/STORE.md` covers the Partner Center setup, local test installs, certification
notes, and FFmpeg licensing for a Store build.

The app has three pages: **Live** (preview, recording, microphone, beauty), **Editor** and
**Settings** (theme, startup, phones). The theme follows Windows by default.
Choose Light or Dark in **Settings › Appearance**.

### Share your screen

While recording or live streaming, click **Share screen** (or Ctrl+Shift+S). The Windows picker lets
you choose a whole screen or one window; **Entire screen** shares the main display straight away.

- **What viewers see:** the shared screen fills the picture, and your phone camera sits in a round
  bubble at the bottom right (white ring, soft shadow, size Small / Medium / Large). Click **Stop
  sharing** to go back to the full-size camera.
- **Applies everywhere:** the preview, recordings and the live stream all show the same picture.
- **Switch any time:** sharing can be turned on and off mid-recording or mid-stream. The video size
  stays the phone's size, so a recording never has to stop for it.
- **Privacy:** Windows draws its yellow border around whatever is being shared. If the shared window
  closes, sharing stops on its own.
- **No phone needed:** without a phone, the screen is shared without the bubble.

Check: `dotnet run --project windows/tests/ShareSmoke -c Release -p:Platform=x64`. It verifies the
layout on synthetic images, compose speed, real capture of the main screen (nothing is saved), and a
recording that switches sharing on and off.

### Go live on YouTube

The Windows app can be the "streaming software" (encoder) YouTube asks for:
1. In YouTube Studio, choose **Go live › Stream** and copy the **stream key**.
2. In CamStudio Pro, open **Live › Live stream › Set up**, paste the key and click **Save**.
   The server is YouTube's (`rtmp://a.rtmp.youtube.com/live2`); any RTMP/RTMPS service works.
3. Connect the phone and click **Go live**. YouTube Studio shows the preview within a few seconds.

- **What goes out:** the phone picture with its beauty and background effects, and the phone mic
  with voice enhancement. The stream is H.264 + AAC with a keyframe every 2 s; quality is Auto
  (from the phone's resolution) or a fixed 1080p, 720p or 480p.
- **Adapts to your upload (Auto):** starts at 720p and moves between 240p and 1080p
  (0.3–4.5 Mbps). It steps down within seconds when the upload can't keep up, jumping straight to a
  step that fits about 80% of the measured upload. It steps up slowly: after 60 s without lag it
  tries one step higher, and after a failed try it waits 2, 4, 8… minutes before trying that step
  again. Each change reconnects, giving viewers about a 2 s gap; that also drops any backlog, so
  the stream stays within a few seconds of real time.
- **Interruptions:** a short network dropout doesn't lower the quality; the stream catches up
  once data flows again. If the phone disconnects, the stream keeps running with a black picture
  and silence until it comes back. A broken connection reconnects automatically.
- **Health logs:** every broadcast writes a per-second CSV (upload, lag, quality, decisions) to
  `%APPDATA%\CamStudioPro\stream-health\` (the **Logs** link on the Live stream card).
- **Key storage:** the stream key is encrypted for your Windows account.
- **Requirements:** FFmpeg, as for the editor.

Checks (`windows/tests/LiveStreamSmoke`, each publishes to a local ffmpeg RTMP server unless noted):
`dotnet run --project windows/tests/LiveStreamSmoke -c Release -p:Platform=x64 -- <mode>` with
- no mode: basic stream (H.264 1280×720 and AAC 48 kHz arrive);
- `--judder [--encoder h264_nvenc]`: every frame arrives once, in order;
- `--policy`: rate-policy simulation of steady, variable, outage, slow and fast links (no ffmpeg);
- `--trace variable` / `--trace outage`: real publishing through a bandwidth-scheduled proxy;
- `--stress --uplink 3`: 1080p start on a 3 Mbps uplink stays near real time.

### Video editor

**Editor** is a timeline editor built on FFmpeg:
- **Media:** import videos, images and music, or **Add recordings**. After a recording,
  **Edit** opens it straight in the editor.
- **Clips:** trim by dragging the edges, reorder, split (S), delete (Del), change speed and
  volume, and add fades and colour (brightness, contrast, saturation).
- **Titles and music:** a titles track (T) and a music track.
- **Projects:** undo/redo, and projects save as `*.camproj`.
- **Export:** MP4 at 1080p, 720p, 4K, vertical 1080×1920 or square. The Automatic encoder
  uses x264, NVENC, Quick Sync, AMF or the Windows Media Foundation encoder.

The editor looks for `ffmpeg.exe`/`ffprobe.exe` in `ffmpeg\` next to the app, a folder you
choose, or `PATH`. Check: `dotnet run --project windows/tests/EditorExportSmoke -c Release -p:Platform=x64`.

Phones reach the app on ports 47801/TCP and 47800/UDP. The Store package declares these firewall
rules itself. For the developer build, allow CamStudio Pro when Windows Firewall asks.

USB mode needs `adb.exe`. The client finds it in Android platform-tools (`ANDROID_HOME`, the
default SDK path, `PATH`, or next to the exe), and USB debugging must be on for the phone.

To record the connected phone, click **Start recording** in the Windows app, then **Stop
recording** to save an MP4 with video and phone microphone audio. Files are saved automatically
in your Windows **Videos\CamStudio Pro** folder (including a relocated Videos folder).
Use **Open recordings folder** to find them. Recording continues while the window is hidden
in the notification area, and finishes when the phone disconnects or you choose **Quit**.
Changing the phone's video resolution ends the recording; start another recording afterwards.
Recordings use the original phone image, without the mirror setting applied.

### Beauty effects on Android and Windows

Update **both apps** to use synchronized beauty controls. On Android open **Effects → Beauty**;
on Windows use the **BEAUTY EFFECTS** card in the right panel. Effects are off by default,
all sliders start at zero, and settings are saved on the phone. Windows becomes editable
after the connected phone reports beauty support. Sliders in either app update the same
phone settings without restarting the video encoder.

Available controls: face/jaw slimming, chin length, nose width, nose bridge slimming (the
upper nose between the eyes) and alar narrowing, nose length, nose root/radix lift, nose tip
lift, nose contour shading (darker bridge sides, brighter ridge, like makeup), smile lift, eye opening, lip fullness, skin
smoothing, and skin brightness. Positive width values narrow; positive length/lift values
lengthen/lift. **Reset beauty** resets just beauty controls and switches the feature off.
**Hold original** temporarily bypasses beauty on the preview and outgoing stream, including
recordings; other effects are retained. Android's top-level **Compare** remains a separate,
preview-only comparison of all image effects.

Processing runs locally on Android using the existing bundled ML Kit face detector's facial
contours and OpenGL shaders. The processed image feeds the phone preview, H.264 stream,
Windows preview and MP4 recorder. The detector uses unwarped frames so
reshaping does not feed back into tracking. Tracking is smoothed, expires within 750 ms,
and resets for camera switches, mirroring, output geometry, or rotation changes. Effects
fade at large head angles and bypass if tracking is lost or the GPU cannot run the shader.
The status is visible in both apps. These are localized 2D image effects, best with a single
well-lit, front-facing subject; nose root/tip changes approximate appearance rather than
reconstructing 3D geometry. Strong settings can distort nearby edges/background, so begin
with small values. Physical-device frame rate, heat, and tracking quality still need testing
on the target phone.

Checks:

```powershell
android\gradlew.bat -p android :app:testDebugUnitTest
dotnet run --project windows/tests/BeautyProtocolSmoke -c Release
python windows/tools/beauty_shader_smoke.py
```

### Realistic background replacement

On Android open **Effects › Background**. The result goes into the stream and recordings.

- **My room** (most convincing): sit where you stream, tap it, and step out of view during the
  5 s countdown. The phone averages 8 frames of the empty room into a clean photo with this
  camera's exact lens, perspective and light. Recapture after moving the phone or if the light
  changes a lot. Shoot a nicer part of the room by pointing the phone there, then put it back.
- **Photo** / **Video**: a real photo or a short, steady looping clip of a room (played muted).
  Daylight office and Reading room use photographic room assets.
- Realism controls: **Edge detail around hair**, **Light wrap** (background light spilling onto
  your outline), **Camera grain** (follows the sensor ISO; never a frozen pattern), **Depth of
  field** (lens blur) and **Match room lighting** (follows the real room's brightness and white
  balance, including auto-exposure changes).

How it works: person segmentation runs on one ordered streaming worker, up to 30 Hz
(15 Hz under thermal protection). Android 1.1.2 pairs each accepted mask with its exact
retained camera frame at resolutions up to 1080p; expired pairs hold the previous composite
instead of applying a stale outline to a moving body. The model's native mask avoids an
extra rescale, and uncertain edges receive colour-aware refinement. Confident pixels skip
that expensive refinement. The model's streaming stabilization replaces the extra temporal
mask averaging, and subtle light wrap reduces bright halos around hair and shoulders.
Actual mask throughput depends on the phone; matching adds a short inference delay and
does not guarantee 30 fps. Thermal protection remains enabled.
Android 1.1.7 removes the experimental chair subtraction from 1.1.3–1.1.6.
Live validation showed that chair colour/reference matching could cut into the face,
clothing and held objects. The working frame/mask synchronization from 1.1.2 remains.
There is no automatic chair-removal control in 1.1.7. The selfie model can still include
chair fragments in the person mask; complete chair removal remains unresolved. Further
segmentation changes need evaluation against real moving-person/chair/held-object footage
before they are enabled in the live pipeline. Synthetic unit tests alone did not establish
visual correctness for the failed chair methods.
Limits: very fast hand movement, objects held in front of you, flyaway hair against a busy wall
and people walking behind you are still the hardest cases. Best results come with light facing
you, at least 1 m between you and the real wall, and clothes that differ from the wall colour.

Checks: `android\gradlew.bat -p android :app:testDebugUnitTest` and
`python windows/tools/background_shader_smoke.py`.

### Real-time voice enhancement

The Windows x64 app enables **Voice enhancement** by default under **PHONE MICROPHONE**.
RNNoise suppresses background noise, with rumble/hum filtering, gentle presence EQ,
compression, and a peak limiter. The processed audio feeds both live playback (including a
virtual cable) and MP4 recording, even when playback is **Off**. Processing runs locally;
no cloud service or GPU is required.

- **Low / Balanced / Strong** controls suppression strength; start with **Balanced**.
- **Natural / Broadcast** controls voice presence and compression.
- **Automatic volume** gently levels speech without boosting silence.
- Turn **Voice enhancement** off to send the original phone audio through unchanged.

Settings persist across restarts. Enhancement expects the phone protocol's 48 kHz mono
16-bit PCM. Other formats or an unavailable native library bypass enhancement and show a
status message. RNNoise adds about one 10 ms frame of algorithmic delay, plus up to 10 ms
to assemble fragmented input. Changing enhancement settings resets the processor and can
discard a partial frame. The final partial frame is also discarded when a phone disconnects.
This processing does not include acoustic echo cancellation or room reverberation removal.
If voices sound overprocessed, lower suppression in Windows or disable Android's separate
noise suppression. Place the phone close to the speaker for the best result.

Verification (requires the .NET 10 SDK and Windows x64):

```powershell
dotnet run --project windows/tests/VoiceEnhancementSmoke -c Release
dotnet run --project windows/tests/RecordingSmoke -c Release -p:Platform=x64 -- windows/tests/RecordingSmoke/output
```

### Start automatically and reconnect without asking

- **Windows:** turn on **Start with Windows** (bottom of the right panel). CamStudio Pro then
  opens in the notification area when you sign in. It is a
  per-user startup entry (no admin prompt) and can also be turned off in Task Manager › Startup apps.
- **Android:** **Settings › Connection › Auto-connect to last computer** is on by default. When the
  app opens, or after the link drops, it keeps looking for the last computer (current Wi-Fi address
  via discovery, last known address, and USB) and connects as soon as the desktop app is running.
  While it waits or streams, a notification keeps it working in the background and with the screen
  off; tap **Stop** there, or on the connect screen, to pause it until you connect again.
- Auto-connect never triggers the PC's approval dialog: it only gets in if the PC already trusts
  the phone. If the PC forgot the phone, connect once by hand (or scan the QR code) to trust it again.
- Android doesn't let apps turn on the camera in the background on their own (for example right
  after the phone restarts), so open CamStudio on the phone once; after that it finds the computer
  by itself. On battery, retries can slow down while the phone sleeps; on a charger it keeps checking.
- For USB, tick **Always allow from this computer** on the phone's "Allow USB debugging?" prompt,
  otherwise Android asks again each time the cable is plugged in.

Phones that are new to this PC are asked about in an approval dialog. Phones that scan the QR
code are trusted automatically, because the QR code carries a pairing key. Trusted phones can be
forgotten from the main window. **New pairing code** invalidates old QR codes.

Testing without a phone: `python windows\tools\fake_phone.py --pair-key <PairKey from
%APPDATA%\CamStudioPro\settings.json>` streams an ffmpeg test pattern and a tone over the real
protocol.

## Android app

Build: `android\gradlew.bat :app:assembleDebug` (JDK 17, Android SDK 35+). See
`android/chairlab/README.md` for the chair-segmentation lab.

## Production builds

**Android** (signed, R8-minified, resources shrunk):

```powershell
cd android
$env:JAVA_HOME = "C:\Program Files\Microsoft\jdk-17.0.18.8-hotspot"
.\gradlew.bat :app:assembleRelease :app:bundleRelease
```

- Output: `app/build/outputs/apk/release/app-release.apk` (for sideloading) and
  `app/build/outputs/bundle/release/app-release.aab` (for Google Play).
- Signing reads `android/keystore.properties` (storeFile, storePassword, keyAlias, keyPassword).
  In CI, set `CSP_KEYSTORE_FILE`, `CSP_KEYSTORE_PASSWORD`, `CSP_KEY_ALIAS` and `CSP_KEY_PASSWORD`
  instead. Without a key, the release APK is built unsigned.
- `android/keystore/` and `keystore.properties` are git-ignored. **Back them up.** Every update
  must be signed with the same key. With Play App Signing, this key is your upload key.
- Keep `mapping/release/mapping.txt` from each release so crash stack traces can be deobfuscated.
  Upload it to Play Console with the AAB.
- Bump `versionCode` and `versionName` in `android/app/build.gradle.kts` for every release.
- Set `CSP_BUILD_ROOT=<folder>` to put Gradle output on another drive.

**Windows**: `windows\build.ps1`, then zip `windows\dist\CamStudioPro` (leave out `*.pdb`).
The exe is not code-signed. Signing it with an Authenticode certificate avoids SmartScreen
warnings.

## Credits

The editor uses FFmpeg (not bundled; see `windows/packaging/STORE.md`).

### Use the phone camera in Zoom (Windows developer build)

Open **Settings > About > Enable camera for Zoom** and accept Windows' administrator
prompt for the one-time COM camera registration. Keep CamStudio Pro running, connect
the phone, fully exit and reopen Zoom, then select **CamStudio Pro Windows Virtual Camera**.
The virtual camera uses the composed preview, including screen sharing. Paused, missing,
or stale phone frames become black. The camera disappears when CamStudio Pro exits.
Re-enable the camera if you move the application folder. Windows 11 is required. The camera source is native and requires no .NET runtime. This registration flow targets unpackaged
desktop builds; The Store-only camera candidate and its approval and activation requirements are documented in `windows/packaging/STORE.md`.

Protocol check (no administrator rights needed):
`dotnet run --project windows/tests/VirtualCameraSmoke -c Debug -p:Platform=x64`.

### HD quality and natural backgrounds

In Windows **Live > Video & background**, choose **HD 720p** or **Full HD 1080p**,
select Original room, soft blur, Daylight office, Reading room, a saved phone photo,
or My captured room, then **Apply video settings**. This requires the updated Android
app (1.1.0 / versionCode 2); older phone apps leave desktop controls disabled.
Changes use 30fps and may briefly restart the encoder. Acknowledgement and errors come
from the phone; merely connecting never overwrites its current settings.

Natural blending uses lighting matching, modest lens blur, camera grain, and light wrap
around the subject with the existing edge-aware person mask. The built-in photographic
style room images are generated assets, replacing the former code-drawn scenes. A real
empty-room capture matches the phone's perspective best; keep the phone fixed and match
face illumination to the selected room. Hair, fast movement, poor lighting and segmentation
limitations can still reveal the replacement. No software can guarantee an indistinguishable
background in every scene. Preview, recording, stream and virtual camera receive the same
phone composite; Zoom independently negotiates the virtual-camera output resolution.

### Sharing a broadcast and changing your appearance live

In Live stream > Edit, paste the broadcast's public viewer/watch URL into **Viewer link**.
For YouTube, copy it from that broadcast's Share action in Studio. RTMP server/key settings
cannot identify a unique broadcast watch URL; the application does not fabricate one.
When streaming or reconnecting, the live card shows the saved viewer link with Copy/Open.
Verify it matches the current broadcast, especially after creating a new YouTube event.
Ingest URLs, embedded credentials and URLs containing the stream key are rejected.
Publishing settings are locked while live; the viewer link can still be edited.

With Android 1.1.1+, background/natural-blend changes apply immediately during a broadcast,
without restarting the encoder or changing capture quality. Apply video quality remains
explicit and can briefly pause phone video. **Touch up my appearance** and its strength
slider apply gentle skin smoothing and subtle brightness through the existing phone beauty
pipeline, preserving other configured beauty controls. All effects feed the live output as
well as preview/recording/virtual camera. Background and appearance quality still need
on-device visual verification under the user's lighting.
