#!/usr/bin/env python3
"""Trace room perimeters and floor areas from a vector floor plan.

Usage:
  python3 rooms.py PLAN.pdf PAGE --region X0 Y0 X1 Y1 --scale 0.25 [--minw 1.2]
         [--bridge-min 2.33 --bridge-max 4.67] [--patch x1,y1,x2,y2 ...] [--seed NAME,x,y ...]
         --out work/

  PAGE        1-based page number of the floor plan sheet
  --region    PDF-point box around ONE plan view (not the whole sheet). Find it by rendering
              the page at 50 dpi: pixel * 1.44 = PDF points.
  --scale     drawing scale in inches per foot (1/4"=1'-0" -> 0.25; 1/8" -> 0.125)
  --minw      minimum stroke width treated as wall linework (profile with --profile first)
  --patch     extra wall segments in output-pixel coords (1 px = 1 inch) to close gaps the
              auto-bridge missed (interior windows, wide openings). Check rooms_vis.png.
  --seed      extra flood seeds for rooms with no room-number text (NAME,x,y in px)
  --profile   print stroke-width / fill histogram for the region and exit

Method:
  1. Draw only heavy strokes (walls) into a raster at 1 px = 1 inch.
  2. Close the building envelope with a rectangle through the outermost long wall lines.
     This also closes exterior window gaps.
  3. Bridge door openings: pair wall endpoints that are collinear and 2'-4" to 4'-8" apart
     with nothing between them.
  4. Flood fill from each room-number label (101..999 text found inside the region).
  5. Perimeter = traced contour length; area = pixel count / 144.

Rotated sheets: coordinates are in the UNROTATED page space (page.derotation_matrix);
rooms_vis.png comes out rotated the same way. Check page.rotation.

Outputs rooms.json (per room: area_sf, perim_lf, poly in px) and rooms_vis.png.
Perimeter INCLUDES door and window openings (bridged). Deduct openings separately.
"""
import argparse, json, os, collections
import numpy as np, cv2
import pymupdf as fitz

ap = argparse.ArgumentParser()
ap.add_argument('pdf'); ap.add_argument('page', type=int)
ap.add_argument('--region', nargs=4, type=float, required=True)
ap.add_argument('--scale', type=float, default=0.25)
ap.add_argument('--minw', type=float, default=1.2)
ap.add_argument('--bridge-min', type=float, default=2.33)
ap.add_argument('--bridge-max', type=float, default=4.67)
ap.add_argument('--patch', nargs='*', default=[])
ap.add_argument('--seed', nargs='*', default=[])
ap.add_argument('--profile', action='store_true')
ap.add_argument('--env-min-ft', type=float, default=10, help='min straight wall run (ft) used to find the envelope; 0 disables envelope')
ap.add_argument('--out', default='work')
a = ap.parse_args()
os.makedirs(a.out, exist_ok=True)

d = fitz.open(a.pdf); p = d[a.page - 1]
X0, Y0, X1, Y1 = a.region
pt_per_ft = 72 * a.scale            # 1/4" scale -> 18 pt per foot
S = 12.0 / pt_per_ft                # px per pt so that 1 px = 1 inch
W, H = int((X1 - X0) * S), int((Y1 - Y0) * S)
inside = lambda r: r.x0 >= X0 and r.x1 <= X1 and r.y0 >= Y0 and r.y1 <= Y1
drw = [x for x in p.get_drawings() if inside(x['rect'])]

if a.profile:
    c = collections.Counter((x['type'], round(x.get('width') or 0, 2), x.get('fill')) for x in drw)
    for k, v in c.most_common(25): print(v, k)
    raise SystemExit

P = lambda pt: (int(round((pt.x - X0) * S)), int(round((pt.y - Y0) * S)))
m = np.zeros((H, W), np.uint8); segs = []
for x in drw:
    if x['type'] not in ('s', 'fs') or (x.get('width') or 0) < a.minw: continue
    for it in x['items']:
        if it[0] == 'l':
            u, v = P(it[1]), P(it[2]); cv2.line(m, u, v, 255, 2); segs.append((u, v))
        elif it[0] == 're':
            q = it[1]; u, v = P(q.tl), P(q.br); cv2.rectangle(m, u, v, 255, 2)
            segs += [(u, (v[0], u[1])), ((v[0], u[1]), v), (v, (u[0], v[1])), ((u[0], v[1]), u)]

colsum, rowsum = (m > 0).sum(0), (m > 0).sum(1)
thr = a.env_min_ft * 12
vx = [x for x in range(W) if colsum[x] > thr]; hy = [y for y in range(H) if rowsum[y] > thr]
if a.env_min_ft > 0 and vx and hy:
    L, R, T, B = min(vx), max(vx), min(hy), max(hy)
    cv2.rectangle(m, (L, T), (R, B), 255, 3)
else:
    ys, xs = np.nonzero(m); L, R, T, B = int(xs.min()), int(xs.max()), int(ys.min()), int(ys.max())

lo, hi = int(a.bridge_min * 12), int(a.bridge_max * 12)
pts = [q for s in segs for q in s]; nb = 0
for i in range(len(pts)):
    for j in range(i + 1, len(pts)):
        (x1, y1), (x2, y2) = pts[i], pts[j]
        dx, dy = abs(x1 - x2), abs(y1 - y2)
        if dy <= 2 and lo <= dx <= hi:
            s = m[min(y1, y2):max(y1, y2) + 1, min(x1, x2) + 3:max(x1, x2) - 2]
        elif dx <= 2 and lo <= dy <= hi:
            s = m[min(y1, y2) + 3:max(y1, y2) - 2, min(x1, x2):max(x1, x2) + 1]
        else: continue
        if s.size and s.max() == 0: cv2.line(m, (x1, y1), (x2, y2), 255, 2); nb += 1
for ptxt in a.patch:
    x1, y1, x2, y2 = map(int, ptxt.split(',')); cv2.line(m, (x1, y1), (x2, y2), 255, 3)

labels = {}
for w in p.get_text('words'):
    t = w[4]
    if t.isdigit() and 100 <= int(t) <= 999 and X0 < w[0] < X1 and Y0 < w[1] < Y1:
        labels.setdefault(t, (int(((w[0] + w[2]) / 2 - X0) * S), int(((w[1] + w[3]) / 2 - Y0) * S)))
for s in a.seed:
    n, x, y = s.split(','); labels[n] = (int(x), int(y))

free = (m == 0).astype(np.uint8); owner = np.zeros(m.shape, np.int32)
vis = cv2.cvtColor(m, cv2.COLOR_GRAY2BGR); rng = np.random.default_rng(1); res = {}
for idx, (k, (x, y)) in enumerate(sorted(labels.items()), 1):
    for r_ in range(40):
        hit = next(((x + dx, y + dy) for dx, dy in [(r_, 0), (-r_, 0), (0, r_), (0, -r_)]
                    if 0 <= y + dy < H and 0 <= x + dx < W and free[y + dy, x + dx]), None)
        if hit: x, y = hit; break
    ff = free.copy(); mk = np.zeros((H + 2, W + 2), np.uint8)
    cv2.floodFill(ff, mk, (x, y), 2); reg = ff == 2
    if owner[reg].max() > 0:
        prev = [n for n, v in res.items() if v['_id'] == owner[reg].max()]
        print(f'WARNING {k} merged with {prev} - open plan or a gap the bridge missed')
    owner[reg] = idx
    cs, _ = cv2.findContours(reg.astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    poly = cv2.approxPolyDP(max(cs, key=cv2.contourArea), 3, True)[:, 0, :]
    res[k] = dict(_id=idx, area_sf=round(float(reg.sum() / 144), 1),
                  perim_lf=round(float(cv2.arcLength(poly, True) / 12), 1), poly=poly.tolist())
    vis[reg] = [int(v) for v in rng.integers(80, 230, 3)]
    cv2.putText(vis, k, (x - 15, y), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 0, 0), 2)

json.dump(dict(envelope_px=[L, T, R, B], envelope_ft=[round((R - L) / 12, 2), round((B - T) / 12, 2)],
               bridges=nb, rooms=res), open(f'{a.out}/rooms.json', 'w'), indent=1)
cv2.imwrite(f'{a.out}/rooms_vis.png', vis)
print(f'envelope {(R-L)/12:.1f} x {(B-T)/12:.1f} ft, {nb} door bridges, {len(res)} rooms')
for k, v in sorted(res.items()): print(f"  {k}: {v['area_sf']} SF, {v['perim_lf']} LF")
print(f'CHECK {a.out}/rooms_vis.png before using the numbers.')
