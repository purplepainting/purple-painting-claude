#!/usr/bin/env python3
"""Turn rooms.json into geometry for the 3D takeoff page (feet, origin at envelope corner).

Usage: python3 model_data.py work/rooms.json --out work/geo.json
Each room gets its polygon in feet, its bbox, and 1-ft floor cells used to draw ceilings with
per-corner heights (sloped vaults render correctly without triangulation problems).
Height functions per room live in the page script (H(room,x,z)); set them from the
interior elevations: low wall, high wall, and which side is the exterior.
"""
import json, argparse
import numpy as np, cv2
ap = argparse.ArgumentParser(); ap.add_argument('rooms'); ap.add_argument('--out', default='geo.json')
a = ap.parse_args()
r = json.load(open(a.rooms)); L, T, R, B = r['envelope_px']
H, W = B + 50, R + 50
ft = lambda x, y: [round((x - L) / 12, 2), round((y - T) / 12, 2)]
out = {'env': r['envelope_ft'], 'rooms': {}}
for k, v in r['rooms'].items():
    poly = np.array(v['poly']); mask = np.zeros((H, W), np.uint8)
    cv2.fillPoly(mask, [poly.astype(np.int32)], 1)
    cells = [[round((gx - L) / 12), round((gy - T) / 12)] for gy in range(0, H, 12) for gx in range(0, W, 12)
             if mask[gy:gy + 12, gx:gx + 12].mean() > 0.5]
    out['rooms'][k] = {'poly': [ft(x, y) for x, y in poly], 'cells': cells,
                       'bbox': [*ft(*poly.min(0)), *ft(*poly.max(0))]}
json.dump(out, open(a.out, 'w'), separators=(',', ':'))
print('wrote', a.out, len(out['rooms']), 'rooms')
