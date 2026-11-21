"""Experimental image-guided boundary refinement. Offline only, never production.

Only a bounded trimap may change. Foreground interiors are frozen. No reference-room
subtraction, chair-colour rules, or timestamp-specific correction is used.
"""
from argparse import ArgumentParser
import json
from pathlib import Path
import time
import cv2
import numpy as np

cv2.setNumThreads(1)

def matte(person):
    t = np.clip((person.astype(np.float32)/255-.429)/.242,0,1)
    return t*t*(3-2*t)

def guard_motion(image, previous, prior, refined, raw_person=None):
    """Keep baseline soft coverage where temporal change makes edge cuts unreliable.

    This does not recover camera blur or objects absent from the baseline mask.
    Uses the immediately preceding saved frame, never an empty-room reference.
    """
    if image.shape != previous.shape:
        raise ValueError('Motion frames must share coordinates')
    h,w=prior.shape
    size=(round(w*400/max(h,w)),round(h*400/max(h,w)))
    current=cv2.resize(image,size).astype(np.float32)
    old=cv2.resize(previous,size).astype(np.float32)
    difference=np.max(np.abs(current-old),axis=2)
    moving=(difference>18).astype(np.uint8)
    moving=cv2.dilate(moving,cv2.getStructuringElement(cv2.MORPH_ELLIPSE,(7,7)))
    protect=cv2.resize(moving,(w,h),interpolation=cv2.INTER_NEAREST)>0
    result=refined.copy()
    result[protect]=np.maximum(result[protect],prior[protect])
    added=0
    if raw_person is not None:
        # Evaluate retaining low-confidence motion softness close to the existing
        # silhouette. This can also retain old-room colour; it is NOT object matting.
        outside=cv2.distanceTransform((prior<.5).astype(np.uint8),cv2.DIST_L2,5)
        edge=protect & (outside<=8) & (raw_person>.02) & (raw_person<.429)
        original=result.copy()
        result[edge]=np.maximum(result[edge],raw_person[edge])
        added=int(((result-original)>.01).sum())
    assert (result[protect]>=prior[protect]).all()
    return result,dict(motion_guard_pixels=int(protect.sum()),
                       motion_coverage_restored_pixels=int(((result-refined)>.05).sum()),
                       soft_motion_pixels_added=added,
                       moving_baseline_coverage_lost_pixels=0)

def refine(image, alpha, band=8, max_side=800, iterations=3):
    h,w=alpha.shape
    scale=min(1,max_side/max(h,w))
    size=(round(w*scale),round(h*scale))
    small=cv2.resize(image,size)
    prior=cv2.resize(alpha,size)
    binary=(prior>=.5).astype(np.uint8)
    inside=cv2.distanceTransform(binary,cv2.DIST_L2,5)
    outside=cv2.distanceTransform(1-binary,cv2.DIST_L2,5)
    scaled_band=band*min(max_side,max(h,w))/800
    fixed_fg=(inside>scaled_band)
    fixed_bg=(outside>scaled_band)
    labels=np.where(binary,cv2.GC_PR_FGD,cv2.GC_PR_BGD).astype(np.uint8)
    labels[fixed_fg]=cv2.GC_FGD
    labels[fixed_bg]=cv2.GC_BGD
    if fixed_fg.sum()<10 or fixed_bg.sum()<10:
        # An empty/transient full-frame mask supplies no reliable colour classes.
        # Preserve the input and expose this as skipped, never fabricate a clean result.
        return alpha.copy(),dict(protected_pixels=int(alpha.size),protected_pixels_changed=0,
                                 changed_pixels=0,skipped=True,
                                 reason='Insufficient confident trimap interior/background')
    cv2.setRNGSeed(0)
    cv2.grabCut(small,labels,None,np.zeros((1,65)),np.zeros((1,65)),iterations,cv2.GC_INIT_WITH_MASK)
    selected=np.isin(labels,[cv2.GC_FGD,cv2.GC_PR_FGD]).astype(np.float32)
    soft=cv2.GaussianBlur(selected,(0,0),.65)
    candidate=cv2.resize(soft,(w,h))
    # Resampling the candidate must not change any pixel outside the trimap either.
    fixed=cv2.resize((fixed_fg|fixed_bg).astype(np.uint8),(w,h),interpolation=cv2.INTER_NEAREST)>0
    candidate[fixed]=alpha[fixed]
    assert np.array_equal(candidate[fixed],alpha[fixed])
    return candidate,dict(skipped=False,protected_pixels=int(fixed.sum()),protected_pixels_changed=0,
                          changed_pixels=int((np.abs(candidate-alpha)>.05).sum()))

def main():
    p=ArgumentParser()
    p.add_argument('--root',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True)
    p.add_argument('--all-frames',action='store_true')
    p.add_argument('--max-side',type=int,default=800)
    p.add_argument('--iterations',type=int,default=3)
    p.add_argument('--motion-guard',action='store_true')
    p.add_argument('--soft-motion-edges',action='store_true')
    a=p.parse_args();a.output.mkdir(parents=True,exist_ok=True)
    if a.soft_motion_edges and not a.motion_guard:
        p.error('--soft-motion-edges requires --motion-guard')
    times=np.asarray(json.loads((a.root/'sequence-5fps/capture.json').read_text())['recording_times_seconds'])
    selected=range(1,len(times)+1) if a.all_frames else [int(np.argmin(abs(times-t)))+1 for t in [9.83,10.23,13.23,13.43,17.43,18.43,18.83,19.23,21.63]]
    previous_times=np.asarray(json.loads((a.root/'sequence-pts/capture.json').read_text())['recording_times_seconds'])
    bg=cv2.imread('android/app/src/main/assets/backgrounds/daylight-office.png')
    rows=[];reports=[]
    writer=None
    if a.all_frames:
        writer=cv2.VideoWriter(str(a.output/'review.mp4'),cv2.VideoWriter_fourcc(*'mp4v'),5,(1600,692))
        if not writer.isOpened():raise RuntimeError('Cannot create video')
    for i in selected:
        image=cv2.imread(str(a.root/f'sequence-5fps/frames/{i:05}.jpg'))
        person=cv2.imread(str(a.root/f'person-5fps/{i:05}.png'),0)
        chair=cv2.imread(str(a.root/f'quality-512/{i:05}-mask.png'),0)
        if image is None or person is None or chair is None:raise ValueError(f'Missing frame {i}')
        prior=np.maximum(matte(person),chair.astype(np.float32)/255)
        start=time.perf_counter();alpha,stats=refine(image,prior,max_side=a.max_side,iterations=a.iterations)
        if a.motion_guard:
            earlier=np.flatnonzero(previous_times<times[i-1]-.001)
            previous=cv2.imread(str(a.root/f'sequence-pts/frames/{earlier[-1]+1:05}.jpg')) if len(earlier) else image
            if previous is None:raise ValueError('Missing preceding motion frame')
            alpha,motion_stats=guard_motion(image,previous,prior,alpha,
                person.astype(np.float32)/255 if a.soft_motion_edges else None)
            stats.update(motion_stats)
        stats.update(frame=i,time_seconds=float(times[i-1]),refinement_ms=(time.perf_counter()-start)*1000)
        reports.append(stats)
        cv2.imwrite(str(a.output/f'{i:05}-alpha.png'),np.rint(alpha*255).astype(np.uint8))
        upright=cv2.rotate(image,cv2.ROTATE_90_CLOCKWISE).astype(np.float32)
        background=cv2.resize(bg,(upright.shape[1],upright.shape[0])).astype(np.float32)
        panels=[]
        for mask,label in [(prior,'BEFORE'),(alpha,'MOTION GUARD' if a.motion_guard else 'BOUNDARY EXPERIMENT')]:
            mask=cv2.rotate(mask,cv2.ROTATE_90_CLOCKWISE)[...,None]
            composite=np.clip(upright*mask+background*(1-mask),0,255).astype(np.uint8)
            panel=cv2.resize(composite,(800,692))
            cv2.putText(panel,f'{times[i-1]:.2f}s | {label} | OFFLINE',(10,25),cv2.FONT_HERSHEY_SIMPLEX,.6,(0,0,0),4)
            cv2.putText(panel,f'{times[i-1]:.2f}s | {label} | OFFLINE',(10,25),cv2.FONT_HERSHEY_SIMPLEX,.6,(255,255,255),1)
            panels.append(panel)
        comparison=np.hstack(panels)
        if writer:writer.write(comparison)
        else:rows.append(cv2.resize(comparison,(1000,432)))
        print(json.dumps(stats),flush=True)
    if writer:writer.release()
    else:cv2.imwrite(str(a.output/'contact.jpg'),np.vstack(rows))
    (a.output/'report.json').write_text(json.dumps(dict(scope='Offline boundary experiment; no live qualification',band_at_800_pixels=8,max_side=a.max_side,iterations=a.iterations,motion_guard=a.motion_guard,soft_motion_edges=a.soft_motion_edges,frames=reports),indent=2))

if __name__=='__main__':main()
