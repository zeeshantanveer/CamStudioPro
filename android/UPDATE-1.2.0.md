# Android 1.2.0 / Windows 1.4.0: connected standby

Phone connections and capture now have separate lifetimes. New connections and reconnects
start with capture off. The Windows header button says Open camera while off and Close
camera while enabled. The phone also offers an explicit Open camera action.

Closing releases CameraX capture, H.264 encoding, microphone capture, orientation updates,
background-video playback, delayed/queued media and streaming Wi-Fi/CPU wake locks. The
foreground connection service and low-rate control/health traffic remain, so the PC can
request capture later. Explicit QR scanning can still open a camera to pair a computer.
The app does not promise zero phone power use or eliminate heat from charging/other apps.

Every connection negotiates cameraControlVersion=1; older peers receive an update-both-apps
message instead of starting a camera automatically. Camera-control errors are acknowledged,
and stale acknowledgements/decoded frames cannot revive closed capture on Windows.

The Windows screen-sharing card has independent bubble switches for preview/meetings,
recordings and live streams. All default off, including upgrades with existing settings.
Saved explicit choices persist. Enabling a bubble does not start capture. Camera-only
video remains full-frame; these bubble controls apply to shared-screen composition.

Validation on 2026-11-19:
- Release APK and Windows MSI built, signed with the existing local test/release keys.
- 30 Android JVM tests passed, including connection/preview/background/QR capture policies.
- StandbySmoke passed all bubble combinations, independent output pixels, immutable
  screen-only frames, late frames after close, and camera control framing.
- RecordingSmoke passed audio/no-audio recording and changing source sizes; ffprobe
  confirms all generated MP4s keep the original 320x240 output size.
- Installed Windows 1.4.0 (installer exit 0) and Android 1.2.0 / versionCode 11 on the A16.
- Reconnection showed Open camera, Camera is off, and all bubble toggles off.
- Real-device Open/Close toggles started/released camera and microphone. Phone diagnostics
  reported capture/encoder/mic/Wi-Fi lock/wake lock all false in standby.
- Background-phone test: Windows Open camera worked after the phone app moved to the
  background; Android app-ops showed camera/microphone running. Windows Close camera
  released them again. The test returned the phone app to the foreground in standby.

Screen-only work can continue without phone capture or a connected phone. Closing camera
finishes a camera-only recording, while shared-screen recordings continue. Closing capture
does not end a live broadcast; use End stream for that. Chair/mug object tracking remains
the separate offline prototype; this update does not deploy it.

Artifacts:
- `app/build/outputs/apk/release/CamStudioPro_1.2.0.apk`
- `../windows/dist/msi/CamStudioPro_1.4.0_x64_TestSigned.msi`
