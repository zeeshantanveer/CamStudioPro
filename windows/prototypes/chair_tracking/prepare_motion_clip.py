"""Crop a local Chair Lab screen recording into source-aligned tracking frames.

The freshly saved MediaPipe chair photo/mask seed frame zero. Recording frames
follow it. This tests a recorded sequence offline, not live processing throughput.
"""
from argparse import ArgumentParser
import json
from pathlib import Path

import cv2

p = ArgumentParser()
p.add_argument('video', type=Path)
p.add_argument('--reference', required=True, type=Path)
p.add_argument('--seed-mask', required=True, type=Path)
p.add_argument('--output', required=True, type=Path)
p.add_argument('--fps', type=float, default=15)
p.add_argument('--crop', nargs=4, type=int, default=[22, 408, 1036, 1199],
               metavar=('X', 'Y', 'WIDTH', 'HEIGHT'))
p.add_argument('--screen-size', nargs=2, type=int, default=[1080, 2340])
a = p.parse_args()
if not 0 < a.fps <= 60:
    p.error('fps must be between zero and 60')
reference = cv2.imread(str(a.reference))
mask = cv2.imread(str(a.seed_mask), cv2.IMREAD_GRAYSCALE)
if reference is None or mask is None or reference.shape[:2] != mask.shape:
    p.error('Reference and seed mask must exist and share coordinates')
frames = a.output / 'frames'
if frames.exists():
    p.error('Choose a new output directory to avoid mixing recording frames')
frames.mkdir(parents=True)
cv2.imwrite(str(frames / '00000.jpg'), reference, [cv2.IMWRITE_JPEG_QUALITY, 100])
cv2.imwrite(str(a.output / 'seed-mask.png'), mask)
cap = cv2.VideoCapture(str(a.video))
source_fps = cap.get(cv2.CAP_PROP_FPS)
if not cap.isOpened() or source_fps <= 0:
    p.error('Video cannot be decoded or has no valid frame rate')
times = []
decoded = 0
next_time = 0.0
previous_time = -1.0
while True:
    ok, image = cap.read()
    if not ok:
        break
    time = cap.get(cv2.CAP_PROP_POS_MSEC) / 1000.0
    if time < previous_time or (decoded > 0 and time <= 0):
        p.error('Decoder did not supply valid monotonic presentation timestamps')
    previous_time = time
    decoded += 1
    if time + 1e-6 < next_time:
        continue
    h, w = image.shape[:2]
    sx, sy = w / a.screen_size[0], h / a.screen_size[1]
    x, y, cw, ch = a.crop
    x0, y0, x1, y1 = round(x*sx), round(y*sy), round((x+cw)*sx), round((y+ch)*sy)
    if not (0 <= x0 < x1 <= w and 0 <= y0 < y1 <= h):
        p.error('Preview crop is outside video coordinates')
    cropped = cv2.resize(image[y0:y1, x0:x1], (reference.shape[1], reference.shape[0]))
    cv2.imwrite(str(frames / f'{len(times)+1:05}.jpg'), cropped,
                [cv2.IMWRITE_JPEG_QUALITY, 100])
    times.append(time)
    next_time = len(times) / a.fps
cap.release()
if not times:
    p.error('Recording contains no decodable frames')
report = dict(video=str(a.video.resolve()), reference=str(a.reference.resolve()),
              source_fps=source_fps, requested_sample_fps=a.fps,
              decoded_frames=decoded, sampled_frames=len(times),
              seed_is_separate_reference=True, crop=a.crop,
              screen_size=a.screen_size, recording_times_seconds=times,
              note='Times use decoded presentation timestamps; not a live latency benchmark')
(a.output / 'capture.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print(f'Prepared {len(times)} recording frames plus reference in {frames}')
