import json, numpy as np, cv2, pymupdf as fitz
r=json.load(open('work/rooms_clean.json'))
d=fitz.open('src/arch.pdf'); p=d[6]
X0,Y0,X1,Y1=795,745,1510,1685; S=12/18
W,H=int((X1-X0)*S),int((Y1-Y0)*S)
# hatch layer (thin 0.24 strokes) for CMU detection
hat=np.zeros((H,W),np.uint8)
for x in p.get_drawings():
    rr=x['rect']
    if not(rr.x0>=X0 and rr.x1<=X1 and rr.y0>=Y0 and rr.y1<=Y1): continue
    if x['type']=='s' and abs((x.get('width') or 0)-0.24)<0.01:
        for it in x['items']:
            if it[0]=='l': cv2.line(hat,(int((it[1].x-X0)*S),int((it[1].y-Y0)*S)),(int((it[2].x-X0)*S),int((it[2].y-Y0)*S)),1,1)
# rotate everything to display orientation (north up, east right): clockwise 90
def rot(pt): x,y=pt; return (H-1-y, x)
hatR=cv2.rotate(hat,cv2.ROTATE_90_CLOCKWISE)
rooms={}
union=np.zeros(hatR.shape,np.uint8)
for k in ['101','102']:
    poly=np.array([rot(q) for q in r['rooms'][k]['poly']])
    rooms[k]=poly; cv2.fillPoly(union,[poly.astype(np.int32)],1)
xs=np.nonzero(union.any(0))[0]; xmin,xmax=xs.min(),xs.max()
# bays: split at partition (102 east edge) and at the D/E jog: find column ranges by N edge changes
cols=[(np.nonzero(union[:,c])[0].min(),np.nonzero(union[:,c])[0].max()) if union[:,c].any() else None for c in range(union.shape[1])]
# group columns by north edge (±6in)
bays=[];cur=None
for c in range(xmin,xmax+1):
    if cols[c] is None: continue
    n,s=cols[c]
    if cur and abs(n-cur['n'])<=8: cur['x1']=c; cur['s']=max(cur['s'],s)
    else:
        cur={'x0':c,'x1':c,'n':n,'s':s}; bays.append(cur)
bays=[b for b in bays if b['x1']-b['x0']>24]
EAVE=9.83; PITCH=4/12
for b in bays: b.update(span=(b['s']-b['n'])/12, width=(b['x1']-b['x0'])/12)
print('bays',[(round(b['width'],1),round(b['span'],1),b['n'],b['s']) for b in bays])
def bay_at(x):
    for b in bays:
        if b['x0']-6<=x<=b['x1']+6: return b
    return min(bays,key=lambda b:min(abs(x-b['x0']),abs(x-b['x1'])))
def h(x,y,b=None):
    b=b or bay_at(x); t=min(y-b['n'],b['s']-y)/12
    return EAVE+PITCH*max(0,t)
out={}
for k,poly in rooms.items():
    cen=poly.mean(0); res={'gyp_lo':0,'gyp_hi':0,'cmu_lo':0,'cmu_hi':0,'gyp_lf':0,'cmu_lf':0,'edges':[]}
    for i in range(len(poly)):
        a,bq=poly[i].astype(float),poly[(i+1)%len(poly)].astype(float)
        L=np.hypot(*(bq-a)); 
        if L<1: continue
        n=np.array([-(bq-a)[1],(bq-a)[0]])/L
        mid=(a+bq)/2
        if np.dot(cen-mid,n)<0: n=-n   # n points into room
        # cmu test: hatch density in band 2..9in outside
        band=[mid-n*t+ (bq-a)/L*s for t in range(2,10) for s in np.linspace(-L/2*0.8,L/2*0.8,20)]
        dens=np.mean([hatR[int(np.clip(q[1],0,hatR.shape[0]-1)),int(np.clip(q[0],0,hatR.shape[1]-1))] for q in band])
        cmu=dens>0.16
        # integrate height along edge (inside room)
        sa=0;sh=0
        for s in np.linspace(0,1,60):
            q=a+(bq-a)*s+n*6; hh=h(q[0],q[1])
            sa+=min(hh,12)*L/12/60; sh+=max(0,hh-12)*L/12/60
        t='cmu' if cmu else 'gyp'
        res[t+'_lo']+=sa; res[t+'_hi']+=sh; res[t+'_lf']+=L/12
        res['edges'].append(dict(a=(a/12).round(2).tolist(),b=(bq/12).round(2).tolist(),type=t,dens=round(float(dens),3),lf=round(L/12,2),lo=round(sa,1),hi=round(sh,1)))
    area=cv2.fillPoly(np.zeros(union.shape,np.uint8),[poly.astype(np.int32)],1).sum()/144
    res['floor_sf']=round(area,1); res['clg_sf']=round(area*np.hypot(1,PITCH),1)
    out[k]=res
    print(k,{kk:(round(v,1) if isinstance(v,float) else '') for kk,v in res.items() if kk!='edges'})
# step walls between bays (ceiling offsets) inside union
step=0
for b1,b2 in zip(bays,bays[1:]):
    xb=(b1['x1']+b2['x0'])/2; lo=max(b1['n'],b2['n']); hi=min(b1['s'],b2['s'])
    ys=np.arange(lo,hi); 
    dif=[abs(h(xb,y,b1)-h(xb,y,b2)) for y in ys]
    step+=sum(dif)/12
    print('step at x',round(xb/12,1),'area',round(sum(dif)/12,1))
json.dump(dict(bays=bays,rooms={k:{**v,'poly':(rooms[k]/12).round(2).tolist()} for k,v in out.items()},step_sf=step,eave=EAVE,pitch=PITCH),open('work/measure.json','w'),default=float)
