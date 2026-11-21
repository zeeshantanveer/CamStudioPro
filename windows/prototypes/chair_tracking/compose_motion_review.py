"""Offline comparison using real ML Kit masks: alpha=max(person, tracked chair)."""
from argparse import ArgumentParser
import json
from pathlib import Path
import cv2
import numpy as np
cv2.setNumThreads(1)

p = ArgumentParser()
p.add_argument('--sequence', required=True, type=Path)
p.add_argument('--person', required=True, type=Path)
p.add_argument('--before', required=True, type=Path)
p.add_argument('--after', required=True, type=Path)
p.add_argument('--background', required=True, type=Path)
p.add_argument('--output', required=True, type=Path)
p.add_argument('--raw-person-confidence', action='store_true',
               help='Diagnostic only: blend raw confidence without the app matte curve')
a = p.parse_args()
a.output.mkdir(parents=True, exist_ok=True)
capture = json.loads((a.sequence / 'capture.json').read_text())
background = cv2.imread(str(a.background))
if background is None:
    raise SystemExit('Background missing')
before_report = json.loads((a.before / 'report.json').read_text())
after_report = json.loads((a.after / 'report.json').read_text())
count = len(capture['recording_times_seconds'])
if len(before_report['frames']) != count+1 or len(after_report['frames']) != count+1:
    raise SystemExit('Incomplete or mismatched tracking run')
variants = [(a.before,str(before_report['image_size']))]
if a.before.resolve() != a.after.resolve():
    variants.append((a.after,str(after_report['image_size'])))
writer = cv2.VideoWriter(str(a.output / 'comparison.mp4'), cv2.VideoWriter_fourcc(*'mp4v'),
                        capture['requested_sample_fps'], (800*len(variants),692))
if not writer.isOpened():
    raise SystemExit('Cannot open review encoder')
times = capture['recording_times_seconds']
chosen = {int(np.argmin(np.abs(np.asarray(times)-t)))+1 for t in [10.61,16.61,25.82]}
checks = []
for idx in range(1,count+1):
    frame = cv2.imread(str(a.sequence / 'frames' / f'{idx:05}.jpg'))
    person = cv2.imread(str(a.person / f'{idx:05}.png'), cv2.IMREAD_GRAYSCALE)
    if frame is None or person is None or person.shape != frame.shape[:2]:
        raise SystemExit(f'Missing or misaligned source/person mask {idx}')
    if not a.raw_person_confidence:
        # Match GlPipeline defaults: edge=.85, PERSON_BIAS=.05. This CPU review
        # applies the confidence curve, but not the GL joint-bilateral edge refinement.
        half_width = .24 * (1-.85) + .10 * .85
        t = np.clip((person.astype(np.float32)/255 - (.55-half_width))/(2*half_width),0,1)
        person = np.rint(t*t*(3-2*t)*255).astype(np.uint8)
    upright = cv2.rotate(frame, cv2.ROTATE_90_CLOCKWISE)
    small_source = cv2.resize(upright,(800,692)).astype(np.float32)
    small_bg = cv2.resize(background,(800,692)).astype(np.float32)
    composites = []
    for directory, label in variants:
        chair = cv2.imread(str(directory / f'{idx:05}-mask.png'),cv2.IMREAD_GRAYSCALE)
        if chair is None or chair.shape != person.shape:
            raise SystemExit(f'Missing/misaligned chair mask {idx}')
        alpha = np.maximum(person,chair)
        if (alpha < person).any():
            raise AssertionError('Union reduced baseline person coverage')
        alpha = cv2.rotate(alpha,cv2.ROTATE_90_CLOCKWISE).astype(np.float32)[...,None]/255
        if idx in chosen:
            bg = cv2.resize(background,(upright.shape[1],upright.shape[0])).astype(np.float32)
            composite = np.clip(upright.astype(np.float32)*alpha + bg*(1-alpha),0,255).astype(np.uint8)
            cv2.imwrite(str(a.output / f'{idx:05}-{label}-composite.png'),composite)
        small_alpha = cv2.resize(alpha,(800,692))[...,None]
        panel = np.clip(small_source*small_alpha+small_bg*(1-small_alpha),0,255).astype(np.uint8)
        text = f'{times[idx-1]:.2f}s | {label} tracker + ML Kit | OFFLINE'
        cv2.putText(panel,text,(12,30),cv2.FONT_HERSHEY_SIMPLEX,.55,(0,0,0),4)
        cv2.putText(panel,text,(12,30),cv2.FONT_HERSHEY_SIMPLEX,.55,(255,255,255),1)
        composites.append(panel)
    writer.write(np.hstack(composites))
    checks.append(idx)
    if idx % 50 == 0:
        print(f'Composited {idx}/{count}', flush=True)
writer.release()
(a.output / 'report.json').write_text(json.dumps(dict(frames=len(checks),
    union='max(person, chair)',person_coverage_never_reduced=True,
    selected_review_frames=sorted(chosen),
    person_curve='raw diagnostic' if a.raw_person_confidence else 'GlPipeline defaults: center .55, half-width .121',
    scope='Offline full-size ML Kit STREAM_MODE + tracker; excludes GL bilateral edge refinement, beauty and lighting'),indent=2))
print(f'Composited {len(checks)} frames; person coverage invariant passed')
