#!/usr/bin/env python3
# Converte alignedDepth.exr -> alignedDepth_preview.png (grigio normalizzato)
# per ogni keyframe passato come argomento. Uso:
#   py -3 exr2png.py 0 20 30        (specifici)
#   py -3 exr2png.py                (tutti i keyframe numerati)
import os, sys, glob
os.environ["OPENCV_IO_ENABLE_OPENEXR"] = "1"
import cv2, numpy as np

base = os.path.dirname(os.path.abspath(__file__))
kfs = sys.argv[1:]
if not kfs:
    kfs = sorted([os.path.basename(p) for p in glob.glob(f"{base}/*")
                  if os.path.isdir(p) and os.path.basename(p).isdigit()],
                 key=int)

for kf in kfs:
    p = f"{base}/{kf}/alignedDepth.exr"
    if not os.path.exists(p):
        print("skip (manca)", p); continue
    d = cv2.imread(p, cv2.IMREAD_UNCHANGED)
    if d is None:
        print("FAIL lettura", p); continue
    if d.ndim == 3:
        d = d[..., 0]
    valid = d > 1e-4
    if valid.sum() == 0:
        print("nessuna depth valida", kf); continue
    lo, hi = np.percentile(d[valid], [2, 98])           # robusto agli outlier
    n = np.clip((d - lo) / max(hi - lo, 1e-6), 0, 1)     # near=scuro, far=chiaro
    img = (n * 255).astype(np.uint8)
    img[~valid] = 0
    out = f"{base}/{kf}/alignedDepth_preview.png"
    cv2.imwrite(out, img)
    print(f"kf {kf}: valid={valid.mean()*100:.0f}%  depth[m] {lo:.2f}-{hi:.2f}  -> {out}")
