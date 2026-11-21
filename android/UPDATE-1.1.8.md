# Android 1.1.8 (version code 10)

Adds a GPU motion-edge safeguard during background replacement up to 1080p. It compares
the exact mask source frame with the nearest older retained frame. When local motion
is detected, spatial edge refinement cannot reduce the original person matte coverage.
It reuses existing frame textures, with no new CPU model, camera readback or object tracker.
Geometry/background resets and missing history disable the guard. Static output stays
identical to the previous shader. Confident background pixels remain background.

This can retain existing background leakage near moving edges. It does not remove source
motion blur, recognize arbitrary held objects, preserve a complete chair, or implement
the separate Python/MediaPipe/EdgeTAM/GrabCut prototypes. Those remain unqualified for
live deployment. There is no claim that the chair/mug issue is completely resolved.

Validation: release build and all 26 JVM tests passed; the actual GLSL shader passed
headless WebGL checks for foreground/background, static/missing history, motion protection,
lighting and grain. Installed successfully over 1.1.7 on the connected A16 on 2026-11-19;
package query confirms version 1.1.8 / code 10. Device capture performance and visible
background-replacement quality still need verification; host shader checks do not establish
phone FPS or resolve the separate tracking work.

APK: `app/build/outputs/apk/release/CamStudioPro_1.1.8.apk` (existing release signing key).
Previous `CamStudioPro_1.1.7.apk` is retained. Windows taskbar fix is a separate MSI 1.3.2.
