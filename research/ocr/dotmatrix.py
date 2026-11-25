import cv2, numpy as np, sys
sys.path.insert(0,'ocrlab')
from roi import mitutoyo_roi
F={'0':"01110 10001 10011 10101 11001 10001 01110",'1':"00100 01100 00100 00100 00100 00100 01110",
'2':"01110 10001 00001 00010 00100 01000 11111",'3':"11111 00010 00100 00010 00001 10001 01110",
'4':"00010 00110 01010 10010 11111 00010 00010",'5':"11111 10000 11110 00001 00001 10001 01110",
'6':"00110 01000 10000 11110 10001 10001 01110",'7':"11111 00001 00010 00100 01000 01000 01000",
'8':"01110 10001 10001 01110 10001 10001 01110",'9':"01110 10001 10001 01111 00001 00010 01100",
'H':"10001 10001 10001 11111 10001 10001 10001",'R':"11110 10001 10001 11110 10100 10010 10001",
'C':"01110 10001 10000 10000 10000 10001 01110",'-':"00000 00000 00000 11111 00000 00000 00000"}
T={k:np.array([[int(c) for c in r] for r in v.split()],float) for k,v in F.items()}
def read(crop):
    g=cv2.cvtColor(crop,cv2.COLOR_BGR2GRAY)
    g=cv2.resize(g,None,fx=400/g.shape[0],fy=400/g.shape[0],interpolation=cv2.INTER_CUBIC)
    b=cv2.adaptiveThreshold(g,255,cv2.ADAPTIVE_THRESH_GAUSSIAN_C,cv2.THRESH_BINARY_INV,151,15)
    # remove border-touching blobs
    n,lab,st,_=cv2.connectedComponentsWithStats(b)
    H,W=b.shape; keep=np.zeros_like(b)
    for i in range(1,n):
        x,y,w,h,a=st[i]
        if x<=2 or y<=2 or x+w>=W-2 or y+h>=H-2: continue
        if a<20: continue
        keep[lab==i]=255
    ys,xs=np.nonzero(keep)
    if len(ys)==0: return None,0
    # text rows: rows with substantial ink
    rowsum=(keep>0).sum(1); rows=np.nonzero(rowsum>0.02*W)[0]
    y0,y1=rows.min(),rows.max()
    band=keep[y0:y1+1]; ch=y1-y0+1; pitch=ch/7.0
    colsum=(band>0).sum(0); cols=colsum>0
    # merge into char segments: gaps >0.6 pitch split chars
    segs=[];start=None;gap=0
    for x in range(W):
        if cols[x]:
            if start is None: start=x
            last=x;gap=0
        elif start is not None:
            gap+=1
            if gap>0.6*pitch: segs.append((start,last)); start=None
    if start is not None: segs.append((start,last))
    out='';scores=[]
    for (a,bx) in segs:
        w=bx-a+1; seg=band[:,a:bx+1]
        # decimal point: short component in bottom rows only
        ink=np.nonzero(seg.sum(1))[0]
        if ink.min()>4.5*pitch and w<1.6*pitch: out+='.';scores.append(1.0);continue
        wp=max(1,min(5,round(w/pitch)))
        grid=np.zeros((7,wp))
        for r in range(7):
            for c in range(wp):
                cell=seg[int(r*pitch):int((r+1)*pitch), int(c*w/wp):int((c+1)*w/wp)]
                grid[r,c]=(cell>0).mean() if cell.size else 0
        grid=(grid>0.25).astype(float)
        best=None
        for k,t in T.items():
            for off in range(0,5-wp+1):
                sub=t[:,off:off+wp]
                if sub.sum(0).min()==0 and wp<5: continue
                s=1-np.abs(sub-grid).mean()
                # penalize template ink outside window
                outside=t.sum()-sub.sum(); s-=outside/35
                if best is None or s>best[1]: best=(k,s)
        out+=best[0];scores.append(best[1])
    return out,min(scores) if scores else 0
if __name__=='__main__':
    for f in sys.argv[1:]:
        bgr=cv2.imread(f); r=mitutoyo_roi(bgr)
        x,y,w,h=r; print(f, read(bgr[y:y+h,x:x+w]))
