"""Original / guarded person+chair / explicit-mug union on the local clip."""
from argparse import ArgumentParser
from pathlib import Path
import json
import cv2
import numpy as np

cv2.setNumThreads(1)
p=ArgumentParser();p.add_argument('--root',type=Path,required=True);a=p.parse_args()
output=a.root/'mug-combined-review';output.mkdir(exist_ok=True)
tracking=json.loads((a.root/'mug-object-multipoint/report.json').read_text())
times=json.loads((a.root/'sequence-5fps/capture.json').read_text())['recording_times_seconds']
bg=cv2.imread('android/app/src/main/assets/backgrounds/daylight-office.png')
writer=cv2.VideoWriter(str(output/'review.mp4'),cv2.VideoWriter_fourcc(*'mp4v'),5,(2400,692))
if not writer.isOpened():raise RuntimeError('Review encoder unavailable')
reports=[];cells=[]
for i in tracking['original_frames']:
    image=cv2.imread(str(a.root/f'sequence-5fps/frames/{i:05}.jpg'))
    old=cv2.imread(str(a.root/f'motion-guard-full/{i:05}-alpha.png'),0)
    mug=cv2.imread(str(a.root/f'mug-object-multipoint/{i:05}-mask.png'),0)
    if image is None or old is None or mug is None or old.shape!=mug.shape:
        raise ValueError(f'Missing or misaligned source/mask {i}')
    # Only union: explicit object preservation must never erase person/chair coverage.
    combined=np.maximum(old,mug)
    assert (combined>=old).all()
    upright=cv2.rotate(image,cv2.ROTATE_90_CLOCKWISE).astype(np.float32)
    background=cv2.resize(bg,(upright.shape[1],upright.shape[0])).astype(np.float32)
    panels=[]
    for mask,label in [(None,'ORIGINAL'),(old,'MOTION GUARD'),(combined,'EXPLICIT MUG UNION')]:
        if mask is None:composite=upright.astype(np.uint8)
        else:
            alpha=cv2.rotate(mask,cv2.ROTATE_90_CLOCKWISE)[...,None].astype(np.float32)/255
            composite=np.clip(upright*alpha+background*(1-alpha),0,255).astype(np.uint8)
        panel=cv2.resize(composite,(800,692))
        text=f'{times[i-1]:.2f}s | {label} | OFFLINE'
        cv2.putText(panel,text,(10,26),cv2.FONT_HERSHEY_SIMPLEX,.55,(0,0,0),4)
        cv2.putText(panel,text,(10,26),cv2.FONT_HERSHEY_SIMPLEX,.55,(255,255,255),1)
        panels.append(panel)
    writer.write(np.hstack(panels))
    if i in [93,95,97,101,109,119]:
        cells.append(np.hstack([cv2.resize(x,(400,346)) for x in panels]))
    reports.append(dict(frame=i,time_seconds=times[i-1],
                        object_added_pixels=int((combined>old).sum()),baseline_coverage_lost_pixels=0))
writer.release()
cv2.imwrite(str(output/'contact.jpg'),np.vstack(cells))
(output/'report.json').write_text(json.dumps(dict(scope='Offline manually selected mug; reverse frames use future seed',
    source_detail_restored=False,automatic_object_detection=False,frames=reports),indent=2))
print(f'Reviewed {len(reports)} frames; no person/chair coverage lost by object union')
