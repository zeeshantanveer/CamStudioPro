# CamStudio Chair Lab

Separate prototype package `com.camstudio.pro.chairlab`. It does not update the production
CamStudio Pro app, connect to the desktop, or publish/record a stream. It has no Internet
permission; photos and review artifacts remain on the device unless explicitly copied.

Build: `android\gradlew.bat -p android :chairseg:testDebugUnitTest :chairlab:assembleDebug`.
APK: `android/chairlab/build/outputs/apk/debug/chairlab-debug.apk` (local debug signature,
arm64 device build for the A16).

1. Stop the camera session in the main phone app before using Camera here. Import can
   instead evaluate an existing original photo without taking the camera.
2. Keep the phone mounted and the chair fixed. Open Camera, leave the empty chair in view,
   then Freeze chair. The frozen photo should show the complete visible chair.
3. Pick chair, then tap its upholstery. Exclude adds negative model prompts on room pixels.
   Use Brush + / Brush − to correct the final chair outline. New AI prompts replace brush
   corrections, so apply manual corrections last.
4. Composite previews preserved source pixels against the photographic office. Original
   and Outline allow inspection of leakage/holes before testing poses.
5. Open Camera again without moving it, sit down and lean/turn/hold a mug, then Freeze pose.
   Review Composite versus Original. Only the chair mask is added to the baseline person
   mask: alpha = max(person, chair). Chair correction erasers cannot reduce person alpha.
6. CPU / GPU chooses the delegate for the next inference. Benchmark runs five selections
   on the pinned chair image, reporting model latency separately from image setup. This is
   a selection benchmark, not a live-video FPS measurement.
7. Save test set writes reference, mask, pose, cutout and timing JSON to the app's external
   files directory, `Android/data/com.camstudio.pro.chairlab/files/review/`.

The fixed mask is not a tracker. It can reveal original-room pixels if the chair moves or
the selection overreaches. Source photos use the phone preview's orientation and mirroring;
they are not interchangeable with a production GL camera mask. Integration must capture
and apply masks in the production renderer's own source coordinates.

Integration gate: review actual chair boundaries and poses on the A16, benchmark cold/warm
CPU/GPU selection, verify no production camera regression, then implement an explicit
capture/select/review/apply workflow in the main app. The existing app is not changed merely
because these isolated mask-invariant tests pass.
