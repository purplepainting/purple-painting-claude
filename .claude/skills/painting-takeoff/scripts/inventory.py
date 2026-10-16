#!/usr/bin/env python3
"""Inventory a plan-set PDF: sheet numbers, titles, revision dates, vector density, annotations.

Usage: python3 inventory.py plans.pdf [more.pdf ...]
Tells you which sheets are vector (measurable) vs raster (scanned), and which sheets a
revision set actually reissues. Revision sets often reissue only a few sheets; the rest
of the set stays on the prior revision.
"""
import sys, re
import pymupdf as fitz

def sheet_id(text):
    k = text.find('Sheet Contents')
    window = text[max(0, k - 120):k] if k > 0 else text[-300:]
    m = re.findall(r'\b([A-Z]{1,2}-?\d{1,2}\.\d{1,2})\b', window) or re.findall(r'\b([A-Z]{1,2}-\d\.\d)\b', text)
    return m[-1] if m else '?'

for path in sys.argv[1:]:
    d = fitz.open(path)
    print(f'== {path}  ({d.page_count} pages)')
    for i, p in enumerate(d):
        t = p.get_text()
        dates = sorted(set(re.findall(r'\b(\d{1,2}/\d{1,2}/\d{2,4})\b', t)))[-3:]
        try:
            ndraw = len(p.get_drawings()) if p.get_drawings else 0
        except Exception:
            ndraw = -1
        nann = len(list(p.annots() or []))
        kind = 'VECTOR' if ndraw > 500 else ('raster?' if len(p.get_images()) and ndraw < 50 else 'light')
        print(f'{i+1:>3} {sheet_id(t):<6} {p.rect.width/72:.0f}x{p.rect.height/72:.0f}in '
              f'chars={len(t):<6} vectors={ndraw:<6} annots={nann:<4} {kind:<8} dates={dates}')
