# Beauty tracking and performance

Face analysis is limited to 10 Hz, or 4 Hz during thermal throttling, independently
of capture frame rate. Only beauty uses the contour detector; face boxes and auto
framing use a separate lightweight detector. There is at most one analysis job.

The detector remembers a successful image rotation. If no face is found, it tries
the next quarter-turn on the next analysis job (never four detections per frame).
AnalysisPolicy maps detections back into the original scene, including rotated
bounding boxes. BeautyGeometry uses the mouth to resolve face-axis direction.

Landmark targets interpolate on rendered frames. Fresh detections hold full effect
strength through 450 ms, fade through 750 ms, then bypass; this avoids strength
pulsing between analysis jobs without keeping a lost face indefinitely.

Beauty limits camera capture and rendering to at most 30 fps. Thermal protection
reduces camera capture/rendering to at most 15 fps and analysis to 4 Hz. User stream
resolution remains unchanged. High-resolution encoding can still generate heat;
these changes reduce work but cannot guarantee a cool device under every workload.

Regression checks: `:app:testDebugUnitTest` covers rotation mapping, face axes in
both landscape orientations, thermal analysis intervals, and freshness limits.
On-device checks still required: front/rear camera, both landscape orientations,
portrait, rotation while streaming, tracking loss, and a sustained thermal test.
