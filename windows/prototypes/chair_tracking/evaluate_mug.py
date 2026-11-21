"""Explicit held-object tracking on the local saved clip. Offline, manually seeded.

This evaluates object preservation, not automatic held-object recognition or live speed.
"""
from argparse import ArgumentParser
from pathlib import Path
import json
import sys
import time
import cv2
import numpy as np
import torch

ROOT=Path(__file__).resolve().parent
p=ArgumentParser()
p.add_argument('--root',type=Path,required=True)
p.add_argument('--output',type=Path,required=True)
a=p.parse_args();a.output.mkdir(parents=True,exist_ok=True)
cv2.setNumThreads(1);torch.set_num_threads(8)
sys.path.insert(0,str(ROOT/'vendor/EdgeTAM'))
from sam2.build_sam import build_sam2_video_predictor
import sam2.modeling.backbones.timm as backbone
factory=backbone.create_model
backbone.create_model=lambda *x,**kw:factory(*x,**{**kw,'pretrained':False})
predictor=build_sam2_video_predictor('configs/edgetam.yaml',str(ROOT/'models/edgetam.pt'),
    device='cpu',apply_postprocessing=False,hydra_overrides_extra=[
        '++model.image_size=512','++model.memory_attention.layer.cross_attention.q_sizes=[32,32]'])
backbone.create_model=factory
times=np.asarray(json.loads((a.root/'sequence-5fps/capture.json').read_text())['recording_times_seconds'])
ids=[i+1 for i,t in enumerate(times) if 18<=t<=24.1]
seed_original=int(np.argmin(abs(times-19.23)))+1
seed=ids.index(seed_original)
frames=a.output/'frames';frames.mkdir(exist_ok=True)
for local,original in enumerate(ids):
    image=cv2.imread(str(a.root/f'sequence-5fps/frames/{original:05}.jpg'))
    if image is None:raise RuntimeError(f'Missing original {original}')
    cv2.imwrite(str(frames/f'{local:05}.jpg'),image,[cv2.IMWRITE_JPEG_QUALITY,100])
reports=[]
with torch.inference_mode():
    state=predictor.init_state(str(frames),offload_video_to_cpu=True)
    # A deliberate positive click on the visible mug at 19.23 s, in saved source coords.
    # Keep this explicit: it is not inferred by a person/mug detector.
    # Upright mug centre (740,375) maps to sideways source (375,458).
    # Explicit negative click on the shirt below the mug prevents selecting clothing.
    point=np.array([[375,458],[280,478],[480,458],[385,350],[530,464]],dtype=np.float32)
    labels=np.array([1,1,1,1,0],dtype=np.int32)
    box=np.array([240,300,520,565],dtype=np.float32)
    _,_,seed_logits=predictor.add_new_points_or_box(state,frame_idx=seed,obj_id=1,
        points=point,labels=labels,box=box)
    seed_mask=seed_logits[0,0].float().cpu().numpy()>0
    if not seed_mask[458,375] or seed_mask[464,530] or not .005<float(seed_mask.mean())<.12:
        raise RuntimeError('Seed failed mug-inclusion/shirt-exclusion/area sanity checks')
    for reverse in [True,False]:
        sequence=iter(predictor.propagate_in_video(state,start_frame_idx=seed,reverse=reverse))
        while True:
            started=time.perf_counter()
            try:idx,objects,logits=next(sequence)
            except StopIteration:break
            elapsed=(time.perf_counter()-started)*1000
            original=ids[idx];mask=logits[0,0].float().cpu().numpy()>0
            image=cv2.imread(str(frames/f'{idx:05}.jpg'))
            if mask.shape!=image.shape[:2]:raise ValueError('Coordinate mismatch')
            cv2.imwrite(str(a.output/f'{original:05}-mask.png'),mask.astype(np.uint8)*255)
            overlay=image.copy();overlay[mask]=(overlay[mask]*.5+np.array([0,255,0])*.5).astype(np.uint8)
            cv2.imwrite(str(a.output/f'{original:05}-overlay.png'),cv2.rotate(overlay,cv2.ROTATE_90_CLOCKWISE))
            item=dict(frame=original,time_seconds=float(times[original-1]),reverse=reverse,
                      tracking_ms=elapsed,selected_fraction=float(mask.mean()))
            reports.append(item);print(json.dumps(item),flush=True)
(a.output/'report.json').write_text(json.dumps(dict(scope='Explicit manually seeded mug; offline only',
    seed_frame=seed_original,source_points=point.tolist(),labels=labels.tolist(),source_box=box.tolist(),
    reverse_uses_future_seed=True,original_frames=ids,frames=reports),indent=2))
