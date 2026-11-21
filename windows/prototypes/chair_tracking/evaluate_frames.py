"""Local EdgeTAM qualification seeded by the MediaPipe chair mask. No camera/network IO.

Run using the isolated .venv. Outputs are ignored private evaluation artifacts.
The three stills are a deliberately difficult jump/occlusion check, not a video FPS test.
"""
from argparse import ArgumentParser
from contextlib import nullcontext
import hashlib
import json
from pathlib import Path
import sys
import time

ROOT = Path(__file__).resolve().parent
parser = ArgumentParser()
parser.add_argument('--review-dir', type=Path, default=ROOT.parents[1] / 'tests/ChairLab/review')
parser.add_argument('--device', choices=['cpu', 'cuda'], default='cpu')
parser.add_argument('--size', type=int, choices=[512, 1024], default=1024)
parser.add_argument('--frames-dir', type=Path, help='Continuous clip frames, named 00000.jpg onward')
parser.add_argument('--seed-mask', type=Path, help='Mask aligned with the first continuous frame')
parser.add_argument('--output-dir', type=Path)
parser.add_argument('--save-logits', action='store_true', help='Retain confidence values for boundary review')
args = parser.parse_args()

import cv2
import numpy as np
import torch

cv2.setNumThreads(1)
torch.set_num_threads(8)
if args.device == 'cuda' and not torch.cuda.is_available():
    raise SystemExit('CUDA is unavailable; no silent CPU fallback in performance tests.')
sys.path.insert(0, str(ROOT / 'vendor/EdgeTAM'))
from sam2.build_sam import build_sam2_video_predictor
import sam2.modeling.backbones.timm as backbone

# The official checkpoint supplies the complete backbone. Avoid an unnecessary second
# timm/HF pretrained download; build_sam's strict checkpoint load still checks every key.
factory = backbone.create_model
def without_pretrained(*a, **kw):
    return factory(*a, **{**kw, 'pretrained': False})
backbone.create_model = without_pretrained

output = args.output_dir or ROOT / 'artifacts' / f'stills-{args.device}-{args.size}'
output.mkdir(parents=True, exist_ok=True)
if args.frames_dir:
    if not args.seed_mask:
        raise SystemExit('--frames-dir requires an explicitly aligned --seed-mask')
    frames = args.frames_dir
    frame_paths = sorted(frames.glob('*.jpg'), key=lambda p: int(p.stem))
    if not frame_paths or [int(p.stem) for p in frame_paths] != list(range(len(frame_paths))):
        raise SystemExit('Frames must be consecutively numbered from zero')
    names = [p.stem for p in frame_paths]
else:
    frames = output / 'frames'
    frames.mkdir(parents=True, exist_ok=True)
    names = ['chair-source', 'pose-source', 'pose2-source']
    frame_paths = []
    for i, name in enumerate(names):
        image = cv2.imread(str(args.review_dir / f'{name}.png'))
        if image is None:
            raise SystemExit(f'Missing saved capture: {name}')
        path = frames / f'{i:05}.jpg'
        cv2.imwrite(str(path), image, [cv2.IMWRITE_JPEG_QUALITY, 100])
        frame_paths.append(path)
first_image = cv2.imread(str(frame_paths[0]))
mask = cv2.imread(str(args.seed_mask or args.review_dir / 'chair-mask.png'), cv2.IMREAD_GRAYSCALE)
if first_image is None or mask is None or mask.shape != first_image.shape[:2]:
    raise SystemExit('The MediaPipe seed mask must match the chair reference coordinates.')

checkpoint = ROOT / 'models/edgetam.pt'
overrides = []
if args.size == 512:
    overrides = ['++model.image_size=512', '++model.memory_attention.layer.cross_attention.q_sizes=[32,32]']
started = time.perf_counter()
predictor = build_sam2_video_predictor('configs/edgetam.yaml', str(checkpoint),
    device=args.device, apply_postprocessing=False, hydra_overrides_extra=overrides)
init_ms = (time.perf_counter() - started) * 1000
backbone.create_model = factory

def synchronize():
    if args.device == 'cuda':
        torch.cuda.synchronize()

report = {'device': args.device, 'image_size': args.size, 'torch': torch.__version__,
    'model_repo': 'facebook/EdgeTAM', 'model_revision': '14d7ecc48c656b94e5184519f698cd5386c5a2bf',
    'code_revision': '7711e012a30a2402c4eaab637bdb00a521302c91',
    'checkpoint_sha256': hashlib.sha256(checkpoint.read_bytes()).hexdigest(),
    'model_init_ms': init_ms,
    'source': 'local continuous clip frames' if args.frames_dir else 'local saved camera stills; not continuous video',
    'frames': []}

with torch.inference_mode(), (torch.autocast('cuda', dtype=torch.float16) if args.device == 'cuda' else nullcontext()):
    synchronize()
    started = time.perf_counter()
    state = predictor.init_state(str(frames), offload_video_to_cpu=True)
    predictor.add_new_mask(state, frame_idx=0, obj_id=1, mask=mask > 127)
    synchronize()
    report['prepare_and_seed_ms'] = (time.perf_counter() - started) * 1000
    sequence = iter(predictor.propagate_in_video(state))
    while True:
        synchronize()
        started = time.perf_counter()
        try:
            idx, ids, logits = next(sequence)
        except StopIteration:
            break
        synchronize()
        elapsed = (time.perf_counter() - started) * 1000
        values = logits[0, 0].float().cpu().numpy()
        if not np.isfinite(values).all():
            raise RuntimeError('Non-finite tracker output')
        if args.save_logits:
            np.savez_compressed(output / f'{idx:05}-logits.npz', logits=values.astype(np.float16))
        selected = values > 0
        cv2.imwrite(str(output / f'{idx:05}-mask.png'), selected.astype(np.uint8) * 255)
        image = cv2.imread(str(frame_paths[idx]))
        if image is None or image.shape[:2] != mask.shape:
            raise RuntimeError(f'Invalid frame coordinates at {idx}')
        tint = image.copy()
        tint[selected] = (tint[selected].astype(np.float32) * .5 + np.array([0, 255, 0]) * .5).astype(np.uint8)
        cv2.imwrite(str(output / f'{idx:05}-overlay.png'), tint)
        item = {'frame': idx, 'source': names[idx], 'tracking_ms': elapsed,
                'selected_fraction': float(selected.mean())}
        if idx == 0:
            original = mask > 127
            item['seed_iou'] = float((selected & original).sum() / max(1, (selected | original).sum()))
        report['frames'].append(item)
        print(json.dumps(item), flush=True)

(output / 'report.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
print(f'Artifacts: {output}', flush=True)
