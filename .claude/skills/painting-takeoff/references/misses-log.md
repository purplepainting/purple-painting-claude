# Takeoff misses log

Every difference between Claude's blind takeoff, Hakim's takeoff (TakeoffExperts.ca), and what the field actually found after award. This file is the training data. Rules in SKILL.md come from patterns here.

Verdicts: **Claude miss** · **Hakim miss** · **Both missed** (field found it) · **Judgment** (defensible either way) · **Doc conflict** (the documents disagree; needs RFI or qualification).

---

## Job 1 · Cottage Primary Care – Carpinteria (JT 371)

- Date tested: 2026-10-15
- Type: medical office TI, 1,921 SF gross, 16 rooms, existing T&G redwood vaults
- GC: McGillivray Construction · Architect: Studio Design Group · Bid due 8/18/26
- Docs: permit set 4/17, Rev 3 (Add 1, 8/6), Rev 4 (Add 2, 8/11), spec book 7/17 (294 pp), RFIs 01–05
- Hakim sets: Rev 0 7/28, Rev B 8/19
- Post-award result: _unknown, fill in_

| Item | Claude | Hakim | Verdict | Lesson |
|---|---|---|---|---|
| Wall LF traced | 719 | 710 | Match (+1%) | Vector room tracing works at 1/4" scale |
| T&G ceiling SF | 940 | 901 | Match (+4%) | Use measured roof slope (20.56° here), not a guess |
| Wall SF (paint + prime-only) | 7,041 | 5,035 | **Claude miss** (+40%) | Don't average low/high vault height across all walls. Side walls run at the eave height; gable ends get triangles. Deduct exterior storefront glazing. |
| Prime-only behind WP/FRP/tile | 0 | 1,080 SF | **Claude miss** | Panels, FRP and tile go over primed gyp. Carry prime-only lines, don't just deduct. |
| Walls over 12' | not split | 136 SF | **Claude miss** | Break out walls above 12' as their own line |
| Gyp ceiling SF | 478 | 571 | **Claude miss** | Include soffit undersides and small hard-lid pockets inside vaults |
| Interior doors | 1 + 11 flagged | 12 painted | **Doc conflict / Hakim miss** | A5.0 Rev 4 says paint on 11 doors (P-05–P-10 "pending designer"); spec 081416 says Type B factory laminate and 099000 only defines P-01–P-04. Qualify or RFI. |
| Door frames | 0 | 6 | **Hakim miss** | Frames are factory aluminum (Western Integrated 300) or stainless ES206. Don't paint. |
| Entry door 101 | flagged | counted | **Hakim miss (risk)** | 20-min fire-rated MDF door: door maker's written OK, full-face mockup, no paint on labels. Sheen conflict: schedule high-gloss lacquer vs spec semi-gloss. |
| Exposed beams | flagged GLB + struts | 20 LF strut | **Judgment** | A4.0 note 14 "paint existing beam to match ceiling" may add GLB LF beyond the 2 struts |
| Exterior stucco (separate price) | none | 1,191 SF | **Claude miss** | Always open exterior elevations even on an "interior" TI |
| Exposed gas pipe | none | 15 LF (noted "not called to paint") | **Claude miss** | Check plumbing roof/floor plans for exposed pipe |
| Mockups, test patches, attic stock, lead-safe prep | flagged | none | **Hakim miss (cost)** | Spec cost items that aren't quantities. Price them. |
| Revision re-measure | n/a | walls identical 7/28 → 8/19 | Note | He checked Rev 3/4 (some marks touched 8/19) but no wall changes |

---

## Job 2 · Weinberg Garage (Solvang)

- Date tested: 2026-10-15
- Type: residential 3-car garage + shop, 1,533 SF, three stepped 4:12 gable bays, vaulted gyp, 12" CMU retaining walls on two sides
- Docs: GeorgeArchitecture arch set 5/13/26 (12 sh, keynotes only, no finish schedule or specs), structural 5/12/26 (14 sh)
- Hakim: Rev 0 BoQ 6/9/26 + measured set 6/10/26 (2 hrs). **Not fully blind**: Claude saw his BoQ totals before measuring; method was independent. Markups compared after.
- Status: awarded / active. Field results: _fill in_
- Kareem's framing: residential sets like this need RFIs to know what to bid; measure everything that might be painted and tag it.

| Item | Claude | Hakim | Verdict | Lesson |
|---|---|---|---|---|
| Gyp walls (≤12' + >12') | 1,092 + 106 = 1,198 | 1,051 + 213 = 1,264 | Match (−5%) | Vault rule fixed: integrate face by face along gable profile; deduct garage doors (240 SF here) |
| Eave wall height | 9'-10" | 10'-0" | **Claude miss** (−2%) | Take plate height from the level markers (plate − curb/slab = 10'-0"), don't back it out from the ridge |
| Walls >12' split | 106 | 213 | Judgment | He approximates gable triangles with 1'/2'/1.94' strips (runs high); Claude integrates the true triangle |
| Vaulted gyp ceiling | 1,434 | 1,474 | Match (−3%) | 4:12 factor 1.054 |
| Interior CMU faces | 968 | 959 | Match (+1%) | Wall-type hatch + wall schedule identify CMU |
| Fascia / painted flashing | 283 / 283 | 277 / 277 | Match (+2%) | Eave + exposed rakes, hide rakes under neighbor bay |
| Soffit | 676 SF | 528 SF (sloped, roof-plan outline) | **Claude miss** (+28%) | Measure the overhang outline from the roof plan and apply slope; don't multiply rake LF × overhang |
| Window/door casing | 60 LF | 18 LF window only | Judgment | He prices door casing inside "door and frame" units. Don't double count. |
| Skylight wells | 3 EA | none | **Hakim miss** | Velux skylights in vaulted gyp ceiling = gyp wells |
| Ceiling step walls between bays | 57 SF | 72 SF (2' strips, 36 LF) | Match | Both caught the vertical faces where stepped bays meet |
| Gyp wall run after openings | 87.6 LF | 86.8 LF | Match (+1%) | Same walls, same deducts |
| Vault vs trusses | RFI | measured vault, no note | **Hakim miss (doc conflict)** | Arch section vaulted, structural shows prefab/girder trusses. Always cross-check arch sections against structural roof framing. |
| Rafter tail spacing | RFI (16" arch vs 30" struct) | not noted | **Hakim miss (doc conflict)** | Read structural eave details; counts change 2× |
| Overhang 1x6 vs 2x6 T&G, exposed rake rafters | RFI | not noted | **Hakim miss (doc conflict)** | Same: structural details |
| Hardie integral color | Excluded, RFI to confirm | not listed | Agree | "Integral color" = pre-finished |
| Floor datum conflict | RFI | not noted | Claude only | Level markers disagree; affects wall heights |
| Exterior CMU, exterior slab, interior sealer | sealer flagged; ext CMU and ext slab not measured | 101 / 1,857 / 1,389 SF | **Claude miss** | Measure site flatwork and exposed CMU even when not called; carry as RFI lines |

Overall: geometry agrees within 1–3% on every measured category except soffit. Claude's edge is scope and doc conflicts (5 items he didn't flag).

Tooling lessons (fixed in scripts):
- Rotated sheets (page.rotation = 90): PyMuPDF drawing coords are in the unrotated space. rooms.py works but output is rotated; rotate before reading N/S/E/W.
- Wall linework weight varies by CAD export: Vectorworks clinic 1.22 pt, this set 0.48 pt. Always `--profile`.
- Envelope rectangle can cut a building in half when long site walls exist; use `--env-min-ft 0` and bridge wide openings instead (`--bridge-max 10.6` for 10' garage doors).
- Door leaves drawn at wall weight cut into room contours; close them with a 9 px morphological close before measuring perimeter.

---

## Job 3 · _name_

(copy a block above)
