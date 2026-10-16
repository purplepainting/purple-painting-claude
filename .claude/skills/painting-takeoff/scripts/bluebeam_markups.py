#!/usr/bin/env python3
"""Extract every Bluebeam measurement markup from a measured PDF (Hakim's sets, or our own).

Usage: python3 bluebeam_markups.py measured.pdf [--out markups.json] [--csv markups.csv]

Pulls subject, label, length (L), wall height (Bluebeam /Depth), wall area (WA), area (A),
slope, count, page, vertices, and modified date. Prints totals by page and subject, and by
subject + height so you can see how the estimator split walls (e.g. 9'-8" walls + gable strips).

Comparing two revisions: run on both. If quantities are identical and the mod dates are
all from the first set, the estimator did not re-measure the revision.
"""
import argparse, re, json, csv, collections
import pymupdf as fitz

def ftin(s):
    m = re.match(r"\s*(\d+)'-(\d+)(?: (\d+)/(\d+))?\"", s or '')
    if not m: return None
    f, i, n, dn = m.groups(); return int(f) + (int(i) + (int(n) / int(dn) if n else 0)) / 12

def num(rx, s):
    m = re.search(rx, s); return float(m.group(1).replace(',', '')) if m else None

ap = argparse.ArgumentParser(); ap.add_argument('pdf')
ap.add_argument('--out', default='markups.json'); ap.add_argument('--csv')
a = ap.parse_args()
d = fitz.open(a.pdf); rows = []
for pi, p in enumerate(d):
    for an in p.annots() or []:
        info = an.info; c = info.get('content', '').replace('\r', '\n')
        obj = d.xref_object(an.xref, compressed=False)
        dep = re.search(r'/Depth ([\d.]+)', obj)
        vm = re.search(r'/Vertices \[([^\]]+)\]', obj)
        Lm = re.search(r'L = ([^\n]+)', c)
        rows.append(dict(page=pi + 1, subject=info.get('subject', ''), label=c.split('\n')[0] if c else '',
            L_ft=ftin(Lm.group(1)) if Lm else None, height_ft=float(dep.group(1)) if dep else None,
            wall_sf=num(r'WA = ([\d.,]+)', c), area_sf=num(r'\bA = ([\d.,]+)', c) if 'A = ' in c else (None if 'WA = ' in c else num(r'([\d.,]+) sf', c)),
            slope_deg=num(r'Slope = ([\d.]+)', c), count=num(r'\|\s*(\d+)\s*$', c.replace('\n', ' | ')),
            mod=info.get('modDate', '')[2:10], content=c.replace('\n', ' | '),
            vertices=[float(v) for v in re.findall(r'[-\d.]+', vm.group(1))] if vm else []))
json.dump(rows, open(a.out, 'w'), indent=1)
if a.csv:
    with open(a.csv, 'w', newline='') as f:
        w = csv.DictWriter(f, fieldnames=[k for k in rows[0] if k != 'vertices']) if rows else None
        if w:
            w.writeheader(); [w.writerow({k: v for k, v in r.items() if k != 'vertices'}) for r in rows]

tot = collections.defaultdict(lambda: [0, 0.0, 0.0, 0.0])
for r in rows:
    g = tot[(r['page'], r['subject'])]; g[0] += 1
    g[1] += r['L_ft'] or 0; g[2] += r['wall_sf'] or 0; g[3] += r['area_sf'] or 0
print('page | subject | n | LF | wall SF | area SF')
for (pg, s), (n, L, wa, ar) in sorted(tot.items()):
    if s in ('Rectangle', 'Legend', ''): continue
    print(f'{pg:>3} | {s:<34} | {n:>3} | {L:8.1f} | {wa:8.1f} | {ar:8.1f}')
byh = collections.defaultdict(lambda: [0, 0.0, 0.0])
for r in rows:
    if r['height_ft'] is not None and r['L_ft']:
        g = byh[(r['subject'], r['label'], round(r['height_ft'], 2))]; g[0] += 1; g[1] += r['L_ft']; g[2] += r['wall_sf'] or 0
print('\nwalls by height: subject | label | height | n | LF | SF')
for k, (n, L, s) in sorted(byh.items()): print(' ', k, n, round(L, 1), round(s, 1))
print('\nmod dates:', sorted(collections.Counter(r['mod'] for r in rows).items()))
