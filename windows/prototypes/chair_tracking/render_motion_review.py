"""Create a local upright tracker-overlay review and measured latency summary."""
from argparse import ArgumentParser
import json
from pathlib import Path
import cv2
import numpy as np
cv2.setNumThreads(1)

p = ArgumentParser()
p.add_argument('capture', type=Path, help='Prepared sequence directory')
p.add_argument('tracking', type=Path, help='Evaluator output directory')
a = p.parse_args()
capture = json.loads((a.capture / 'capture.json').read_text())
report = json.loads((a.tracking / 'report.json').read_text())
items = report['frames']
times = capture['recording_times_seconds']
if len(items) != len(times) + 1:
    raise SystemExit('Incomplete tracking run; review video not generated')
writer = cv2.VideoWriter(str(a.tracking / 'review.mp4'), cv2.VideoWriter_fourcc(*'mp4v'),
                        capture['requested_sample_fps'], (800, 692))
if not writer.isOpened():
    raise SystemExit('Review video encoder unavailable')
sample_indices = set(np.linspace(1, len(items)-1, 12).astype(int))
cells = []
for item in items[1:]:
    idx = item['frame']
    image = cv2.imread(str(a.tracking / f'{idx:05}-overlay.png'))
    if image is None:
        raise SystemExit(f'Missing overlay {idx}')
    image = cv2.rotate(image, cv2.ROTATE_90_CLOCKWISE)
    image = cv2.resize(image, (800, 692))
    label = f"{times[idx-1]:.2f}s | OFFLINE chair tracking"
    cv2.putText(image, label, (12, 30), cv2.FONT_HERSHEY_SIMPLEX, .7, (0,0,0), 4)
    cv2.putText(image, label, (12, 30), cv2.FONT_HERSHEY_SIMPLEX, .7, (255,255,255), 1)
    writer.write(image)
    if idx in sample_indices:
        cells.append(cv2.resize(image, (400,346)))
writer.release()
cv2.imwrite(str(a.tracking / 'contact.jpg'), np.vstack([
    np.hstack(cells[i:i+4]) for i in range(0,12,4)]))
latencies = np.array([x['tracking_ms'] for x in items[1:]])
summary = dict(frames=len(latencies), median_tracking_ms=float(np.median(latencies)),
               p95_tracking_ms=float(np.percentile(latencies,95)),
               scope='Offline tracker step only; excludes rendering, decoding and transport',
               quality='Requires visual review; no ground truth chair annotations')
(a.tracking / 'summary.json').write_text(json.dumps(summary,indent=2), encoding='utf-8')
print(json.dumps(summary))
