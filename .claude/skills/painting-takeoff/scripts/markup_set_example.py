import json, pymupdf as fitz, numpy as np
m=json.load(open('work/measure.json'))
src=fitz.open('src/arch.pdf')
X0,Y0=795,745; S=12/18
H=int((1685-745)*S)
def to_unrot(xft,yft):            # display ft -> unrotated pdf pt
    dx,dy=xft*12,yft*12
    mx,my=dy,H-1-dx
    return fitz.Point(X0+mx/S, Y0+my/S)
def P(page,xft,yft):              # -> page (rotated) coords for annots
    return to_unrot(xft,yft)
C=dict(yellow=(1,.85,0),green=(0,.65,.25),cyan=(0,.75,.85),orange=(1,.5,0),magenta=(.85,0,.7),blue=(0,.3,1),gray=(.5,.5,.5),red=(.9,0,0))
def line(page,pts,col,subj,txt,w=6,op=.55):
    a=page.add_polyline_annot([P(page,*q) for q in pts]); a.set_colors(stroke=C[col]); a.set_border(width=w); a.set_opacity(op)
    a.set_info(subject=subj,content=txt,title='Purple Painting takeoff'); a.update(); return a
def poly(page,pts,col,subj,txt,op=.25):
    a=page.add_polygon_annot([P(page,*q) for q in pts]); a.set_colors(stroke=C[col],fill=C[col]); a.set_border(width=1.5); a.set_opacity(op)
    a.set_info(subject=subj,content=txt,title='Purple Painting takeoff'); a.update(); return a
def circ(page,pt,col,subj,txt,label,r=14,raw=False):
    c=pt if raw else P(page,*pt)
    rect=fitz.Rect(c.x-r,c.y-r,c.x+r,c.y+r)
    a=page.add_circle_annot(rect); a.set_colors(stroke=C[col]); a.set_border(width=3); a.set_info(subject=subj,content=txt,title='Purple Painting takeoff'); a.update()
    t=page.add_freetext_annot((fitz.Rect(c.x-7,c.y-r,c.x+7,c.y+r) if page.rotation else fitz.Rect(c.x-r,c.y-7,c.x+r,c.y+7)),label,fontsize=10,text_color=C[col],align=1, rotate=page.rotation)
    t.update()
def cloud(page,rect_pts,txt,subj='RFI'):
    pts=[P(page,*q) for q in rect_pts]
    a=page.add_polygon_annot(pts+[pts[0]]); a.set_colors(stroke=C['red']); a.set_border(width=2.5,clouds=2); a.set_info(subject=subj,content=txt,title='Purple Painting takeoff'); a.update()
def note(page,xft,yft,txt,col,w=150,h=40,fs=8):
    c=P(page,xft,yft); r=fitz.Rect(c.x,c.y-w,c.x+h,c.y) if page.rotation else fitz.Rect(c.x,c.y,c.x+w,c.y+h)
    t=page.add_freetext_annot(r,txt,fontsize=fs,text_color=C[col],fill_color=(1,1,1),rotate=page.rotation); t.update()

out=fitz.open()
# ---------- cover page ----------
cov=out.new_page(width=1728,height=1152)
y=50
def T(s,size=11,bold=False,col=(0,0,0),x=50):
    global y
    cov.insert_text((x,y),s,fontsize=size,fontname='hebo' if bold else 'helv',color=col); y+=size*1.45
T('Weinberg Garage – Painting Takeoff Set',24,True)
T('2934 Quail Valley Road, Solvang CA 93463  |  Prepared by Purple Painting (CSLB #1096227)  |  10/15/2026',11)
T('Source: Architectural_Sheets_5.13.26.pdf (GeorgeArchitecture) + Structural_Sheets_5.12.26.pdf. Markups are PDF annotations – open in Bluebeam Revu, Markups List, filter by Subject.',10)
y+=10; T('LEGEND – what is contract and what is not',14,True)
leg=[('green','GREEN line','IN CONTRACT – called out to paint on drawings (2x fascia WD-tr-01, 26 ga flashing A-4.11)'),
('yellow','YELLOW line','BASE BID, ASSUMED – gyp wall face, prime + 2 coats (FN-gwb-01 "prep for paint", no schedule). Confirm per RFI 1'),
('magenta','MAGENTA line','High work above 12\' – gable ends and ceiling step faces (lift)'),
('cyan','CYAN fill','BASE BID, RFI – vaulted gyp ceiling 4:12. Truss vs vault conflict, RFI 2'),
('orange','ORANGE line','ALTERNATE / RFI – interior face of 12" CMU (e4). Finish not stated, RFI 3'),
('blue','BLUE circle','COUNTED item – doors, windows, skylight wells (number = count)'),
('gray','GRAY circle','NOT IN CONTRACT – pre-finished or by others (Hardie integral color, gutters, stone veneer, galv. hardware)'),
('red','RED cloud','RFI / conflict – price as add or alternate until answered')]
for col,k,v in leg:
    cov.draw_rect(fitz.Rect(50,y-9,80,y+2),color=C[col],fill=C[col],fill_opacity=.6); cov.insert_text((90,y),k,fontsize=10,fontname='hebo',color=C[col]); cov.insert_text((200,y),v,fontsize=10,fontname='helv'); y+=16
y+=10; T('QUANTITY SUMMARY (bid basis)',14,True)
rows=[('Status','Scope','Basis','Qty'),
('Contract','2x wood fascia, painted','123 LF eave + ~160 LF exposed rake','283 LF'),
('Contract','26 ga galv. flashing at fascia, painted','A-4.11','283 LF'),
('Base, assumed','Gyp walls up to 12\'','1,395 gross – 303 openings (+~2% if eave 10\'-0")','~1,092 SF'),
('Base, assumed','Gyp walls 12\' to 14\'-10" (gables, step faces)','high work','~106 SF'),
('Base, RFI 2','Vaulted gyp ceiling 4:12','910 + 450 SF floor x 1.054','~1,434 SF'),
('Base, assumed','Skylight wells, Velux VS A06','~13 SF gyp each','3 EA'),
('Base, assumed','Interior window casing','casement windows D, E','~18 LF'),
('Alt / RFI 3','Interior face 12" CMU walls (e4)','~97 LF x 10\'','~968 SF'),
('Alt / RFI 4','Custom wood segmental garage doors 10x8, both faces','A-4.1 finish blank','3 EA'),
('Alt / RFI 4','Lincoln clad doors 3x8, interior face + casing','A-4.1 finish blank','2 EA'),
('Alt / RFI 5','4x6 false rafter tails','16" o.c. arch / 30" o.c. struct','49–93 EA'),
('Alt / RFI 6','Overhang T&G soffit + exposed rake rafters','roof-plan outline, sloped (est.)','~530 SF'),
('Alt / RFI 10','Garage slab sealer','not called out','~1,390 SF'),
('Alt / RFI 10','Exterior slab sealer, exterior CMU strip','not called out','by Hakim: 1,857 SF / 101 SF'),
('NOT in contract','Hardie board & batt, gutters, stone veneer, galv. hardware','integral color / not paint','—')]
xs=[50,190,560,1000]
for i,r in enumerate(rows):
    for j,c in enumerate(r): cov.insert_text((xs[j],y),c,fontsize=10,fontname='hebo' if i==0 else 'helv')
    y+=15
y+=10; T('RFIs (full text on the Weinberg takeoff page)',14,True)
rf=['1 Paint scope, colors, sheens – no finish schedule','2 Vaulted gyp ceiling (A-5.1) vs prefab/girder trusses (S-sheets)','3 Interior face of 12" CMU e4 – paint, seal, or natural?','4 Door/window finishes – schedule finish columns blank','5 Rafter tail spacing 16" vs 30", finish','6 Overhang 1x6 vs 2x6 T&G, exposed rake rafters, finish','7 Fascia 2x vs 2x8, color; flashing color','8 Hardie integral color – confirm no field paint','9 Fence finish','10 Concrete sealer interior/exterior','11 Galvanized hardware – leave unpainted?','12 Floor datum: FF 360\'-0" vs curb 357\'-4 11/16" vs plate 367\'-4 11/16"']
for s in rf: T('•  '+s,10)
# ---------- copy sheets ----------
for pg in [6,7,11,10]: out.insert_pdf(src,from_page=pg,to_page=pg)
fp,rp,sp,dp=out[1],out[2],out[3],out[4]
# floor plan walls
for k,r in m['rooms'].items():
    for e in r['edges']:
        a,b=e['a'],e['b']
        for typ,f in e.get('split',[[e['type'],1]]):
            pass
        sp_=e.get('split',[[e['type'],1]])
        t0=0
        for typ,f in sp_:
            q0=[a[0]+(b[0]-a[0])*t0,a[1]+(b[1]-a[1])*t0]; q1=[a[0]+(b[0]-a[0])*(t0+f),a[1]+(b[1]-a[1])*(t0+f)]; t0+=f
            L=((q1[0]-q0[0])**2+(q1[1]-q0[1])**2)**.5
            if typ=='cmu': line(fp,[q0,q1],'orange','CMU interior face – RFI 3',f'{L:.1f} LF x 10\' = {L*10:.0f} SF')
            else: line(fp,[q0,q1],'yellow','Gyp wall paint – base',f'{L:.1f} LF')
# high-work: gable ends (vertical edges = E/W walls & partition)
for k,r in m['rooms'].items():
    for e in r['edges']:
        a,b=e['a'],e['b']
        if abs(a[0]-b[0])<0.3 and abs(a[1]-b[1])>15:
            off=0.5 if k=='101' else -0.5
            line(fp,[[a[0]+off,a[1]],[b[0]+off,b[1]]],'magenta','Gable wall above 12\' – high work','gable triangle to 14\'-10"',w=3,op=.8)
# ceiling fill per room
for k,r in m['rooms'].items():
    poly(fp,r['poly'],'cyan','Vaulted gyp ceiling – RFI 2',f"room {k}: {r['clg_sf']:.0f} SF sloped")
# garage doors (north wall gaps, one per bay) & clad doors
for b in m['ext']['bays']:
    cx=(b['x0']+b['x1'])/2; circ(fp,[cx,b['n']+1.2],'blue','Garage door 10x8 – RFI 4','custom wood segmental, both faces','1')
circ(fp,[49.8,4.8],'blue','Clad door 3x8 – RFI 4','Lincoln clad, interior face','1')
circ(fp,[2.3,24],'blue','Clad door 3x8 + windows D,E – RFI 4','west shop wall: door 5, casement windows D & E','3')
cloud(fp,[[19,8],[33,8],[33,30],[19,30]],'RFI 2: arch shows vaulted gyp ceiling; structural shows prefab/girder trusses')
cloud(fp,[[36,28],[51,28],[51,32],[36,32]],'RFI 3: CMU e4 interior finish not stated')
note(fp,21,24,'VAULTED GYP CLG 4:12\n~1,434 SF (RFI 2)','cyan',w=120,h=28)
# roof plan: fascia/eaves
EO,RO=2.67,2.17
for b in m['ext']['bays']:
    x0,x1,zN,zS=b['x0']-RO,b['x1']+RO,b['n']-EO,b['s']+EO
    line(rp,[[x0,zN],[x1,zN]],'green','Wood fascia + flashing – CONTRACT','eave, WD-tr-01 painted',w=7,op=.7)
    line(rp,[[x0,zS],[x1,zS]],'green','Wood fascia + flashing – CONTRACT','eave, WD-tr-01 painted',w=7,op=.7)
    for x in (x0,x1): line(rp,[[x,zN],[x,zS]],'green','Rake fascia + flashing – CONTRACT','rake (plan length; slope 1.054)',w=4,op=.5)
    poly(rp,[[x0,zN],[x1,zN],[x1,b['n']],[x0,b['n']]],'orange','Overhang soffit + rafter tails – RFI 5/6','eave overhang 2\'-8"',op=.2)
    poly(rp,[[x0,b['s']],[x1,b['s']],[x1,zS],[x0,zS]],'orange','Overhang soffit + rafter tails – RFI 5/6','eave overhang 2\'-8"',op=.2)
# skylights (tag positions found on sheet, unrotated coords)
for lab,(ux,uy) in {'A':(1300,1525),'B':(1227,1218),'C':(1154,914)}.items():
    c=fitz.Point(ux+5,uy+30)
    circ(rp,c,'blue',f'Skylight well {lab}','Velux VS A06 – gyp well ~13 SF','1',r=18,raw=True)
cloud(rp,[[52,0],[56,0],[56,32],[52,32]],'RFI 5/6/7: rafter tails 16" vs 30"; 1x6 vs 2x6 T&G; fascia 2x vs 2x8')
note(rp,4,-1,'NOT IN CONTRACT: gutters (TMP-rf-01 integral color), asphalt roofing','gray',w=230,h=22)
# sections & elevations: gray not-in-contract notes
for pg in (sp,dp):
    r=pg.rect; t=pg.add_freetext_annot(fitz.Rect(60,60,520,130),'NOT IN CONTRACT: Hardie board & batt siding and batts (TMP-rsp-01/02 integral color), gutters, stone veneer (MA-st-01), galvanized Simpson hardware.\nRFI 12: FF 360\'-0" vs curb 357\'-4 11/16" vs plate 367\'-4 11/16". Bid eave height 10\'-0".',fontsize=9,text_color=C['gray'],fill_color=(1,1,1))
    t.update()
out.save('Weinberg Garage - Takeoff Set - painting markups - Claude - 2026-10-15.pdf',garbage=3,deflate=True)
print('saved',out.page_count)

# ---------- highlighted spec/keynote + schedule + structural sheets ----------
HL=[ # (phrase, color, note)
 ('(N) 2x Wood Facia, painted.','green','IN CONTRACT: paint fascia'),
 ('Prep as required for paint','yellow','BASE ASSUMED: gyp walls/ceiling painted; no colors/system given (RFI 1)'),
 ('Level 5 Finish','yellow','Level 5 – confirm primer-surfacer system (RFI 1)'),
 ('Hardi-Plank Fiber-Cement sheathing, w/ vertical batts, integral color','gray','NOT IN CONTRACT: integral color (RFI 8 to confirm)'),
 ('3 x Vertical batts, color 1','gray','NOT IN CONTRACT: integral color'),
 ('Provide integral color perforated horizontal','gray','NOT IN CONTRACT: gutter integral color'),
 ('(N) Stone veneer on Concrete wall.','gray','NOT IN CONTRACT: stone veneer'),
 ('(N) 4x6 false rafter tails at 16" o.c. max.','red','RFI 5: structural says 30" o.c.; finish not stated'),
 ('(N) Post and Rail Fence with 4" x 4" wire mesh','red','RFI 9: fence finish?'),
 ('with stone veneer at visible wall area','orange','CMU above/inside – interior face finish not stated (RFI 3)'),
 ('(N) 4" Concrete Garage slab, sloped 3" to drain.','red','RFI 10: sealer not called'),
 ('26 Ga. Gal.','green','IN CONTRACT: flashing painted'),
 ('Flashing, painted.','green','IN CONTRACT: flashing painted'),
 ('1x6 T&G Sheathing @','red','RFI 6: structural says 2x6 T&G; finish not stated'),
 ('Clad Glazed Panel Door','red','RFI 4: interior face finish blank'),
 ('Wood-Framed Segmental Door','red','RFI 4: custom wood garage doors, finish blank'),
 ('Clad Casement Window','red','RFI 4: interior finish'),
 ('Self-Curbing Skylight','yellow','Count 3 skylight wells (gyp)'),
 ('4X6 FALSE RAFTER TAILS @ 30" O.C. MAX','red','RFI 5: conflicts with arch 16" o.c.'),
 ('2X6 T&G EXPOSED RAKE RAFTER','red','RFI 6: exposed rake rafters – paint/stain?'),
 ('2x6 T&G STARTER BRD','red','RFI 6: conflicts with arch 1x6 T&G'),
 ('MIN. FASCIA SIZE = 2X8','red','RFI 7: arch says 2x'),
 ('GIRDER TRUSS','red','RFI 2: trusses vs arch vaulted gyp ceiling'),
 ('LAYOUT TRUSS','red','RFI 2: trusses vs arch vaulted gyp ceiling'),
 ('GALVANIZING: ALL EXPOSED STEEL TIMBER HARDWARE','gray','NOT IN CONTRACT unless told (RFI 11)'),
 ('STRUCTURAL STEEL WORK DOES NOT REQUIRE SHOP PAINTING','gray','No steel paint scope found'),
]
strs=fitz.open('src/str.pdf')
start=out.page_count
out.insert_pdf(src,from_page=1,to_page=1)    # KN-1.0
out.insert_pdf(src,from_page=10,to_page=10)  # A-4.1
out.insert_pdf(strs,from_page=1,to_page=1)   # S0.2 notes
out.insert_pdf(strs,from_page=7,to_page=7)
out.insert_pdf(strs,from_page=9,to_page=9)
hits={}
for pi in range(start,out.page_count):
    pg=out[pi]
    for phrase,col,nt in HL:
        for q in pg.search_for(phrase, quads=True):
            a=pg.add_highlight_annot(q); a.set_colors(stroke=C[col]); a.set_opacity(.45)
            a.set_info(subject={'green':'IN CONTRACT','yellow':'BASE – assumed','gray':'NOT IN CONTRACT','red':'RFI / conflict','orange':'ALT / RFI'}[col],content=nt,title='Purple Painting takeoff'); a.update()
            hits[phrase]=hits.get(phrase,0)+1
print('highlights',sum(hits.values()),'missing',[p for p,_,_ in HL if p not in hits])
out.save('Weinberg Garage - Takeoff Set - painting markups - Claude - 2026-10-15.pdf',garbage=3,deflate=True)
print('pages',out.page_count)
