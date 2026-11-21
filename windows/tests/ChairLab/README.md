# MediaPipe chair-preservation evaluation

This folder records evaluation only. The production Android app remains 1.1.7; it does
not depend on `:chairseg` or `:chairlab` and was not rebuilt for this prototype.

Prototype source/workflow: `android/chairlab/README.md`.
Pinned SDK/model and licence provenance: `android/chairseg/src/main/assets/MODEL-PROVENANCE.txt`.

Verified on the connected SM-A165F:

- Three JVM checks pass: union never reduces person alpha, chair erasing cannot erase the
  underlying person mask, FIT_CENTER letterbox taps map/reject correctly.
- Native CPU and GPU segmentation both pass on an existing generated-room fixture. The
  selected object is a plant, not the user's chair. This validates API/runtime/delegate
  operation and yields timing data; it does not validate chair outlines.
- The prototype's three device tests pass with Internet permission removed, including
  the bundled baseline selfie model working offline.
- `benchmark.json` records native timings from that non-personal test fixture. GPU cold
  compilation and warm selection are distinct costs. These timings are not video FPS.

Outstanding integration gate:

1. Capture an original empty-chair image in Chair Lab, with the main camera session stopped.
2. Select/refine the whole visible chair and inspect against the original and composite.
3. Capture seated leaning/turning/held-mug poses with phone/chair fixed, review leakage and
   completeness, and save the local review set.
4. Only after those results pass, implement capture/select/review/apply in the production
   renderer's own coordinates and benchmark its actual live output.

Android's connected-test runner can remove the test target after running. Do not run
connected app tests while a user is capturing review photos. Reinstall Chair Lab after
the automated tests, and leave it installed for manual evaluation.

User review photos, if explicitly exported from the lab, belong in ignored `review/`.
