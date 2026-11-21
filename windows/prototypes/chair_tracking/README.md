# Moving-chair tracking qualification

The user requires chair movement, not a fixed-chair workaround. The unreleased static
mask integration is not a qualified solution and must not replace the installed 1.1.7 app.

MediaPipe MagicTouch supplies the initial chair mask. Its independent CPU/GPU still-image
selection is verified on the A16 and the actual empty chair. A separate video-object
tracker must propagate the object through motion and person occlusion. Only union with
the existing person alpha is permitted; do not reintroduce colour/reference subtraction.

Candidate: Meta EdgeTAM, official code/checkpoint, Apache-2.0. First evaluate locally on
saved frames; then a continuous, locally recorded camera clip. Accuracy precedes runtime
or production integration claims. No user footage is uploaded to model providers.

Runtime/dependencies and model weights belong in ignored local directories. Do not alter
the global Python/PyTorch installation while evaluating GPU support.

## Saved-frame evaluation, 2026-11-18

`evaluate_frames.py --device cpu --size 512` successfully propagated the actual
MediaPipe chair selection through the saved seated pose and the large chair movement.
The moved-chair overlay follows its new outline rather than retaining the old position.
The seated overlay excludes the person, but still has small missing chair areas near
occlusion boundaries. This is promising evidence, not a complete-chair quality pass.

Tracking took 298.5 ms and 270.1 ms for the two subsequent frames on this PC CPU
(PyTorch 2.13.0+cpu). These are individual still-jump timings, not continuous video FPS.
The checkpoint SHA-256 matches the pinned official model. Reports, masks and overlays
are local under `artifacts/stills-cpu-512/`.

Remaining qualification: continuous motion and occlusion, chair edge completeness,
GPU/portable acceleration, end-to-end latency, and person-mask union in live output.
No moving-chair candidate has been installed in the main Android or Windows app.

## Continuous motion evaluation, 2026-11-19

Captured a new MediaPipe empty-chair selection and a 30-second local Chair Lab
screen recording on the A16. The fixed phone captured seating, leaning, swivelling
in both directions, and lifting/drinking from a mug. All footage remains local.

`prepare_motion_clip.py` crops the actual preview and uses decoded presentation
timestamps. Android screen recordings are variable-frame-rate: using the container's
nominal 59.94 FPS would incorrectly shorten this clip to 12.85 seconds. The corrected
sequence samples 450 frames across 29.97 seconds, plus the separate fresh reference.
The seed and recording use matching preview orientation, mirroring and dimensions.

`evaluate_frames.py --device cpu --size 512` completed all 451 frames. The upright
review contact sheet shows the chair mask following both swivel directions, the
visible chair around the seated person, and occlusion during mug use. This is a
visual tracking result, not an annotated boundary-accuracy score or a full background
compositing test. Fine boundary completeness still needs review at production resolution.

Median tracker step: 307.08 ms; p95: 386.94 ms. These exclude decoding, rendering,
transport and person segmentation. CPU execution is unsuitable for a live release.
No GPU acceleration, Android deployment, or production person-mask union was tested
by this run. The main app remains unchanged.

Private outputs: `artifacts/motion-20261119/tracked-cpu-512/` contains `review.mp4`,
`contact.jpg`, per-frame masks/overlays, `report.json` and `summary.json`.
`render_motion_review.py` generates those review artifacts from a completed run.

## Combined person/chair boundary review, 2026-11-19

The user's neck-area examples require inspecting the final union, not only the green
chair overlay. Added an opt-in `OfflineMotionTest` in the separate Chair Lab test APK.
It processes the 451 local saved frames with the existing ML Kit selfie STREAM_MODE,
rotates the sideways preview upright for inference and returns masks to source coordinates.
The real-device test passed (174.895 s including decoding and PNG export). It never
opens the camera/network, and direct `am instrument` avoids Gradle target uninstall.

Re-evaluated EdgeTAM at 512 and native 1024 on the same 150 timestamp-matched frames
sampled at 5 Hz throughout the recording. This is an offline boundary comparison;
it does not qualify tracking at live input cadence. The interrupted 15 Hz 1024 attempt
under `tracked-cpu-1024/` is incomplete and must not be used for acceptance.

`compose_motion_review.py` combines actual person and chair masks with max(), checks
that baseline person coverage never decreases, and renders an office replacement.
Its default person confidence curve matches GlPipeline's default edge=.85 and bias=.05;
it does not reproduce GL joint bilateral refinement, beauty, light wrapping or frame
transport. `--raw-person-confidence` is only a diagnostic, not the app's final matte.
Earlier outputs `composite-baseline-full/` and `composite-quality/` used raw confidence.
The current review is `composite-default-matte/comparison.mp4`, with 512 left / 1024 right.

Close-ups at approximately 10.61, 16.61 and 25.82 s retain the visible narrow red chair
strip and hair/collar region after person/chair union, even where the chair-only overlay
omitted pixels. The reviewed mug-use frames also retain the visible mug. This is visual
evidence on this clip, not proof that every chair boundary or future pose is correct.
Table/background leakage remains visible; no full output-quality acceptance is claimed.

Higher resolution did not establish a sufficient reason to accept its runtime cost.
On the 5 Hz comparison, CPU tracker-step medians were 391 ms (512) and 2241 ms (1024),
with concurrent offline rendering during portions of the run. These are not isolated
GPU/mobile/live performance measurements. Acceleration and actual renderer integration
remain unqualified. Production Android 1.1.7 and Windows app have not been updated.

## Rejected boundary cases and cleanup experiment, 2026-11-19

The user's subsequent screenshots at 9.83, 10.23, 13.23, 13.43 and 17.43 s are
additional failures of the previous candidate. The earlier three selected poses were
insufficient for acceptance. Comparison with original frames and separate masks shows
original-wall leakage along the body/chair boundary as well as outline defects. A max()
union retains false foreground already present in either input; resolution alone did
not resolve these cases.

`refine_boundary_review.py` evaluates an image-guided GrabCut trimap after the union.
Only the boundary band can change; foreground/background interiors outside the band
are explicitly restored and checked. This is a separate experiment that can adjust
body-outline pixels. It does not guarantee preservation of every hair or finger, and
must not be silently substituted for the prior additive-only chair path in production.
No saved-room subtraction, hard-coded chair colour or per-timestamp correction is used.
Algorithm API reference: https://docs.opencv.org/4.13.0/d7/d1b/group__imgproc__misc.html

The 800-pixel / 3-iteration version reduced the reported wall patches but cost roughly
856–1438 ms on the six selected cases. The 400-pixel / 2-iteration version retains a
similar physical boundary-band width and ran around 133–151 ms on those selected cases.
A full 150-frame 5 Hz offline run completed, with median refinement 223 ms during video
generation. All protected pixels were unchanged. Frames 31, 32, 34, 36 and 38 lacked
reliable foreground/background classes; refinement was explicitly skipped and their
original alpha was retained. These skips are recorded, not counted as cleanup successes.

Reviewed before/after crops show reduced wall patches at the reported shoulders/neck.
Table artifacts, residual soft edges, temporal stability at live cadence and total
runtime remain unresolved. This candidate still FAILS release qualification.
Artifacts: `boundary-fast-motion/review.mp4`, `reported-cases.jpg`, per-frame alpha and
`report.json` under the ignored `artifacts/motion-20261119/` directory. No production
APK/Windows app was changed or installed during this boundary experiment.

## Moving mug regression, 2026-11-19

The user's 18.83 s screenshot identifies another failing case. Original source frame
95 already contains pronounced spatial blur in the moving mug/hand; the boundary cleanup
also trims some soft coverage. `mug-motion-diagnosis.jpg` compares original, baseline
composite, cleanup and cleanup with the new motion guard. This is not a sharp-mug fix.

The experimental `--motion-guard` compares each frame to its immediately preceding
timestamped 15 Hz saved frame. Temporal change identifies regions where boundary
cleanup may not reduce the baseline alpha. It does not infer a mug class, restore an
object absent from the baseline, remove source blur, or recover missing source detail.
It can retain unwanted background near moving boundaries and needs live testing.

The guarded 150-frame 5 Hz run completed. All protected interiors were unchanged and
all detected-motion baseline-coverage checks passed. At frame 95, 12,695 pixels regain
more than .05 alpha relative to the unguarded candidate across the whole frame (not a
hand-annotated mug-only count). The CPU median refinement+guard step was 244.58 ms,
excluding EdgeTAM and rendering. This remains unsuitable for a live release.
Current video: `motion-guard-full/review.mp4`, local under the motion artifacts directory.

Added opt-in `CameraMotionCapabilitiesTest` to the separate Chair Lab test APK. It
reads manual-exposure, ISO, EV and FPS capabilities without opening a camera or changing
settings. `:chairlab:assembleDebugAndroidTest` passed; the device capability test has not
run because USB currently detects no phone. The main app already gates its manual
shutter controls on actual sensor support. Do not promise fast shutter on the A16 front
camera until its capabilities and a fresh captured-motion test are checked.
No production app changes or installation occurred in this motion-guard stage.

## Saved-clip-only held-object evaluation, 2026-11-19

The user selected "Continue with the saved clip only". No further USB/camera actions
were taken; do not request another capture as a prerequisite for this offline review.

Added `--soft-motion-edges` as an opt-in diagnostic. It retains weak person confidence
within eight source pixels of the silhouette in detected moving regions. It can retain
old-room halos, does not identify a mug and has not passed acceptance. Default behaviour
is unchanged; do not deploy this option as a motion-blur fix.

`evaluate_mug.py` instead evaluates an explicit EdgeTAM mug selection on the existing
18.03–24.03 s clip (31 frames at 5 Hz). The clear 19.23 s frame is manually seeded with
a box and positive mug/rim/base/handle prompts plus a negative shirt prompt. Selection
and tracking overlays are under `mug-object-multipoint/`. Earlier single-point trials
under `mug-object/` and `mug-object-selected/` are not acceptance outputs.
Reverse propagation before the 19.23 s seed uses future information: this is an offline
object-preservation experiment, not causal/automatic live held-object detection.

The reviewed multipoint seed covers the visible mug body/rim/base, and tracked overlays
follow it through the blurred lift and drinking poses. At 18.03 and 18.23 s, the object
mask is empty before the mug appears in view. `compose_mug_review.py` adds the object
mask with max(existing guarded person/chair, mug). All 31 frames preserve the existing
mask coverage. At the reported 18.83 s frame this adds only 873 pixels: most of the visible
blur already exists in the original captured mug/hand and is not repaired by this mask.

Current focused comparison: `mug-combined-review/review.mp4`, showing original,
motion-guard output and explicit mug union side by side. `contact.jpg` and `report.json`
accompany it. The separately evaluated mug tracker median was 305.75 ms CPU per step;
this is not a shared multi-object live-runtime benchmark. Automatic initiation, capture
blur, table leakage, reliable soft-edge matting and live performance remain unresolved.
No production application updates were made. This is not a release-qualified fix.
