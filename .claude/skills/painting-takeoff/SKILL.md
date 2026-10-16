---
name: painting-takeoff
description: Run a Purple Painting paint and wallcovering takeoff from plans, specs, addenda and RFIs, with a scope audit, 3D model page, and optional comparison to Hakim's measured set.
---

# Painting takeoff (v1.2)

Purpose: produce a bid-ready painting and wallcovering takeoff that does not miss anything required when Purple Painting bids "per plans and specifications". Quantities come from the drawings' vector linework. Scope comes from reading every sheet note, schedule and spec section that could put paint on a surface. Every run ends with a page Kareem can open, share, and check.

Version 1 is built from one test job (Cottage Primary Care, Oct 2026). Read `references/misses-log.md` before starting. It holds every known miss and the rule it produced. When a new job teaches something, add it there first, then update the rules here.

Two jobs in one: **scope audit** (what gets painted, what conflicts) and **quantities** (how much). Scope is where money is lost. Do it first and do it fully.

---

## 0. Inputs and where to find them

Ask for the job name, then gather everything yourself before asking Kareem for anything.

1. **SharePoint**, Purple Painting site → `Shared Documents/02 Estimates/Bids - Active/<Job>/`
   - `Plans & Docs/Bid Documents/` — plan sets, addenda
   - `Estimates & Proposals/` — Hakim's measured PDFs and spreadsheets (`Takeoff - Measured Plans*.pdf`, `Takeoff Spreadsheet*.xlsx`)
   - Also check `02 Estimates/Deal Documents/<Job>/` (specs often live in `Specs/`) and `TAKEOFF BACKUPS/` (Hakim's zips)
2. **Outlook.** Addendum PDFs are often only a cover page that says "see attached". The real Rev drawings and RFI answers are attachments on emails: search `"<job> Addendum"`, the SmartBidNet notices, and threads with info@takeoffexperts.ca. Kareem's emails forwarding "all associated files" to Hakim usually have the full set.
3. **Size limits.** The SharePoint/Outlook connectors return text only, which is fine for notes, schedules and specs but not for measuring. Measuring needs the PDF bytes: the user uploads them (under 30 MB each), or the session is linked to the user's computer and the OneDrive-synced folder is attached. Ask for exactly the files you need, by name and size.

Make a document register before any work: every file, date, revision, sheet list, and which sheets each revision reissued (`scripts/inventory.py`). **Revision sets often reissue only a few sheets.** The governing set is sheet-by-sheet: the latest issue of each sheet wins. Note which sheets you could not get.

## 1. Read the governing documents for scope

Read these in full, in this order. Log every paint-relevant line with its source (sheet + note number, or spec section + paragraph).

1. **G-sheets:** scope of work, general notes, sheet index, deferred submittals, "documents not part of plan set" (that's where a separate spec book gets mentioned).
2. **Finish schedule / materials list** (A5.x or A6.0): every P-xx paint code, sheen, substrate, and every finish that is *not* paint (wall panels, FRP, tile, ACT, factory finishes).
3. **Door and frame schedule:** door finish column, frame material and finish, door types, fire ratings, glazing/infill. Count doors by finish, and frames that actually take paint.
4. **Floor plan, RCP, interior elevations, exterior elevations, details:** general notes and keynotes. Look specifically for:
   - "paint to match", "paint existing ...", "prep and prime", "skim coat", "new gyp at existing walls"
   - exposed structure: beams, GLBs, struts, columns, decking, tube steel posts
   - soffits, valances, bulkheads, clerestories, vault heights (low and high)
   - electrical panels, access panels, exposed conduit, pipe, ducts, roof equipment
   - wainscots, FRP heights, wall panels one side only, feature walls
   - exterior: stucco patch/infill, doors, trim, railings, gas pipe, bollards, even on an "interior" TI
5. **Spec book, every section that touches paint:** Div 09 painting (09 90 00 / 09 91 xx), plus 05 50 00 misc metals, 08 11 13 steel doors/frames, 08 14 xx wood doors, 08 31 13 access doors, 09 29 00 drywall (finish level), 09 77/78 wall panels and FRP, 10 xx specialties. Pull out: primer and finish products per system, number of coats, mockups, test patches, field samples, lead or hazardous coating rules, fire-door coating restrictions, attic stock, warranty, VOC rules (Santa Barbara APCD 323.1).
6. **RFI answers and addendum narratives.** Scanned RFIs may not extract; list them as unread.

### Scope checklist (lessons so far — see misses log)

- [ ] Door finish in the schedule matches the spec's door-face finish (factory laminate vs field paint). Job 1: schedule said paint on 11 doors, spec said factory laminate. **Doc conflict → qualify or RFI.**
- [ ] Frames: factory aluminum / stainless / storefront are **not** paint. Only shop-primed hollow metal is.
- [ ] Fire-rated doors: coating must be approved by the door maker; labels never painted. Sheen in schedule vs spec.
- [ ] Every paint code in the schedule has a coating system in the spec. Codes with no system (e.g. "pending designer selection") are a conflict.
- [ ] Existing substrates (T&G, old paint, stain): primer type (alkyd stain blocker vs latex), test patches, tannin bleed responsibility.
- [ ] Exposed beams, struts, GLBs called out only in RCP or elevation notes.
- [ ] Soffit undersides and small hard-lid pockets inside vaults.
- [ ] Prime-only surfaces behind wall panels, FRP and tile (a line item, not just a deduct).
- [ ] Walls above 12 ft broken out (access/lift pricing).
- [ ] Exterior elevations opened, even on interior TIs. Separate-price anything not clearly in scope.
- [ ] Plumbing and mechanical plans checked for exposed pipe and duct.
- [ ] Architectural sections vs structural roof framing: vaulted ceiling vs trusses, rafter tail spacing, exposed sheathing material. Conflicts here change quantities by 2×.
- [ ] Skylights in finished ceilings: count the gyp wells.
- [ ] Stepped or offset roof bays: vertical ceiling step faces.
- [ ] Level markers agree (FF, curb, plate). If not, RFI before trusting wall heights.
- [ ] Under-detailed sets (no finish schedule, keynotes only — typical residential): measure everything that could be painted and tag each line **Called out / Assumed / RFI / Excluded**. Write the RFI list as part of the deliverable.
- [ ] Spec cost items priced: mockups per color/sheen, field samples, attic stock, lead-safe prep, warranty.

## 2. Measure from vector linework

Check each sheet with `scripts/inventory.py`. Vector sheets (thousands of drawing paths) can be measured. Scanned/raster sheets cannot be measured reliably; say so and fall back to dimension strings or flag for Hakim/Bluebeam.

Install once: `pip install pymupdf opencv-python-headless numpy` (add `--break-system-packages` if pip refuses).

**Floor plan → room perimeters and areas** (`scripts/rooms.py`):
1. Render the floor plan page at 50 dpi and look at it. Find the box of ONE plan view in PDF points (pixel × 1.44). Use the dimension plan if the sheet has two views (less furniture clutter).
2. `--profile` first to see stroke widths; walls are usually the heaviest strokes (1.2 pt+ on Vectorworks exports).
3. Run it, then **look at `rooms_vis.png`**. Fix leaks with `--patch` (interior windows, wide openings) and add `--seed` for rooms with no number. Merges between rooms that really are open to each other (waiting/hall/M.A.) are fine; split them by area and say the split is estimated.
4. Sanity check: total wall LF from rooms should land near the sum of all room perimeters; net floor area should be ~85% of gross SF on G1.0.

**Heights** come from interior elevations and the RCP ceiling-height tags, never assumed.
- Flat rooms: wall SF = perimeter × ceiling height.
- **Vaulted / sloped rooms (rule from job 1):** do NOT multiply the whole perimeter by the average of low and high. Side walls run at their own height (usually eave height); gable or shed end walls are a rectangle plus a triangle. Measure each wall face from the elevation. Put area above 12 ft on its own line.
- Sloped ceilings: plan area ÷ cos(slope). Get the slope from the section or from rise/run on the elevation.

**Deducts and splits:**
- Deduct exterior storefront/window glazing on exterior walls (the envelope rectangle in `rooms.py` closes those gaps, so perimeters include them).
- Doors: count separately, don't deduct from wall SF unless the job's convention says so (match Hakim's convention when comparing).
- Wall panels, FRP, tile, wainscot: deduct from finish paint, **add to prime-only**.

**Ceilings by type** from the RCP legend: gyp (paint), existing decking (paint system per spec), ACT (no paint), open structure (paint per spec).

**Doors and frames:** count from the door schedule by type, size, finish code, and both faces. Frames only where material is shop-primed steel.

## 3. Compare with Hakim (when his set exists)

Run `scripts/bluebeam_markups.py` on his measured PDF(s). It pulls every markup's subject, length, height, wall SF, area, slope, count and modified date. Totals by subject + height show his method (e.g. 9'-8" walls plus gable strips).

- Compare category by category: wall LF, wall SF, prime-only, over-12-ft, ceilings by type, T&G/decking, doors, frames, misc (beams, pipe), exterior.
- If his revised set has identical quantities and old mod dates, he did not re-measure the revision; check whether the revision changed walls.
- Score each difference: **Claude miss / Hakim miss / Both missed / Judgment / Doc conflict**, with the source sheet or spec paragraph.
- Write every scored line into `references/misses-log.md` under a new job block.

## 4. Deliverables: the takeoff package (required, every job)

Kareem's standard package has four required parts. The 3D page is a bonus on top, never a replacement. Name files `<Job> - <Part> - Claude - <date>` and save them to the job's `Estimates & Proposals/` folder (or hand them to Kareem to drop there when the connector can't upload the size).

**1. Measured plans: marked-up PDF takeoff set** (the Schwab 10/14/26 set and `scripts/markup_set_example.py` are the models)
- Page 1: cover with the job block, the legend, the quantity summary (bid basis) and the RFI / field-verify list.
- Then the measured sheets with PDF annotations Bluebeam can read: every markup has a Subject and Contents, so the Markups List filters by scope.
- One color per contract status, the same on every job:
  - GREEN line: in contract, called out to paint
  - YELLOW line: base bid, assumed or implied (wall faces)
  - MAGENTA line: high work above 12'
  - CYAN fill: ceilings and soffits
  - ORANGE: alternate or RFI surfaces
  - BLUE circle: counted items, with the count inside (doors, windows, skylights, wallcovering tags)
  - GRAY circle or highlight: NOT in contract (factory finished, by others)
  - RED cloud: RFI, conflict, or verify
- Annotation coordinates on rotated sheets go in the UNROTATED page space (no rotation_matrix). Freetext labels on rotated pages need a tall, narrow rect.

**2. Spreadsheet: every item** (`.xlsx` in Hakim's BoQ layout, so it drops into pricing and JobTread)
- **BoQ tab**: #, section, description, proposed paint system, unit, qty, status (Contract / Base – assumed / Alt / RFI / Not in contract, each with its own fill), rate (blue on yellow, blank for Kareem), amount = qty × rate, drawing ref, RFI #, note. SUMIFS totals by status, plus a base-bid total.
- **Measurements tab**: wall-by-wall detail (room, from/to coordinates, type, LF, SF up to 12', SF above 12'), the ceiling and step-face areas, and a method note.
- **RFIs tab**: number, subject, question, reference, plus blank answer and date columns.
- Quantities go in as values or formulas with the derivation in a note or comment. Run recalc; zero formula errors.

**3. Spec highlighted, with observations**
- Highlight annotations on the spec book pages (or on the keynote legend / general notes when there is no spec book), the schedules, and the structural notes that touch finish scope. Same color code as part 1; each highlight's Contents says why.
- A **Spec Review tab** in the workbook: source, section or keynote, exact text, status, observation, RFI #. Include conflicts between spec, schedules and drawings, and between architectural and structural.

**4. Materials: what's specified and what we propose**
- A **Materials tab**: surface, specified material as drawn (substrate, manufacturer, product), source, primer, finish, sheen, color, status.
- When the documents name coating products (e.g. Cottage 099000: SW ProMar 200, Extreme Block, Emerald Urethane, BEHR for the fire-rated door), list them exactly with section references.
- When they don't (most residential sets), say "not specified – Purple proposal for pricing" and propose the standard system for each substrate.

**Bonus: 3D model page** (three.js r128 + OrbitControls): rooms extruded from the traced polygons, walls colored by finish, ceilings by type, sloped vaults from the elevation heights, eaves and fascia, and numbered pins tied to the flags and RFIs (`scripts/model_data.py`, `templates/`). Publish it as an artifact and link it from the cover sheet.

Also on the page or the cover: the comparison table vs Hakim (when his set exists), and open items (documents not obtained, sheets not reviewed, deducts not taken).

## 5. Rules for honesty

- Never present a quantity you didn't measure or derive from a stated dimension. Mark estimates as estimates.
- State what you couldn't read (scanned RFIs, missing sheets, links not opened).
- "Per plans and specs" can't be made zero-risk by any takeoff. Every conflict found becomes a qualification, exclusion, or RFI in the proposal. Draft those lines.
- Hakim (or Bluebeam) stays the quantity check until a run lands within ±5% of him on walls and ceilings across several jobs. Track that in the misses log.

## Residential and under-detailed sets

Most residential sets show what gets built, not what gets painted. Don't guess silently. Measure every surface that could reasonably be painted, tag the confidence, price RFI lines as adds or alternates, and hand over a numbered RFI list the GC can send as-is (the Weinberg page has a copy button for this).

## Version history

- v1.2 (2026-10-15): deliverable package made explicit: measured-plan PDF set, BoQ workbook (BoQ / Measurements / Spec Review / Materials / RFIs), highlighted spec, materials list; 3D page is a bonus.
- v1.1 (2026-10-15): job 2 Weinberg Garage. Vault rule fixed (walls −5% vs Hakim, ceiling −3%, CMU +1%, fascia +2%). Added confidence tags, RFI list, arch-vs-structural checks, rotated-sheet handling.
- v1 (2026-10-15): from job 1, Cottage Primary Care. Wall LF and T&G matched Hakim within 4%; wall SF 40% high (vault height method, glazing). Scope audit found door-finish and fire-door conflicts he missed.
