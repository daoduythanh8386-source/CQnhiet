import cv2, numpy as np, subprocess, sys, os
def ocr(img, psm=7, wl="0123456789.HRC"):
    cv2.imwrite('/tmp/_o.png', img)
    r=subprocess.run(['tesseract','/tmp/_o.png','-','--tessdata-dir',os.path.join(os.path.dirname(os.path.abspath(__file__)),'tess'),'-l','eng','--psm',str(psm),'-c',f'tessedit_char_whitelist={wl}'],capture_output=True,text=True)
    return r.stdout.strip()
def mitutoyo_roi(bgr):
    hsv=cv2.cvtColor(bgr,cv2.COLOR_BGR2HSV)
    m=cv2.inRange(hsv,(25,60,120),(50,255,255))
    m=cv2.morphologyEx(m,cv2.MORPH_CLOSE,np.ones((15,15),np.uint8))
    cs,_=cv2.findContours(m,cv2.RETR_EXTERNAL,cv2.CHAIN_APPROX_SIMPLE)
    best=None
    for c in cs:
        x,y,w,h=cv2.boundingRect(c); a=w*h
        if w>2.5*h and w<8*h and a>0.002*bgr.shape[0]*bgr.shape[1]:
            if best is None or a>best[2]*best[3]: best=(x,y,w,h)
    return best
def dyna_roi(bgr):
    hsv=cv2.cvtColor(bgr,cv2.COLOR_BGR2HSV)
    m=cv2.inRange(hsv,(95,120,120),(115,255,255))  # bright blue digits
    n,lab,st,_=cv2.connectedComponentsWithStats(m)
    H=bgr.shape[0]
    comps=[s for s in st[1:] if s[3]>0.04*H and s[3]<0.2*H and s[4]>50]
    if not comps: return None
    hmax=max(s[3] for s in comps)
    big=[s for s in comps if s[3]>0.6*hmax]
    x0=min(s[0] for s in big); y0=min(s[1] for s in big)
    x1=max(s[0]+s[2] for s in big); y1=max(s[1]+s[3] for s in big)
    # include decimal point: extend nothing (dot is small blue component on baseline)
    dots=[s for s in st[1:] if s[3]<0.3*hmax and s[4]>10 and s[1]+s[3]>=y1-0.25*hmax and s[1]>=y0+0.5*hmax and s[0]>x0 and s[0]<x1]
    pad=int(0.15*hmax)
    return (max(0,x0-pad),max(0,y0-pad),x1-x0+2*pad,y1-y0+2*pad)
def prep(crop, invert):
    g=cv2.cvtColor(crop,cv2.COLOR_BGR2GRAY)
    g=cv2.resize(g,None,fx=max(1,200/g.shape[0]),fy=max(1,200/g.shape[0]),interpolation=cv2.INTER_CUBIC)
    _,b=cv2.threshold(g,0,255,cv2.THRESH_BINARY+cv2.THRESH_OTSU)
    if invert: b=255-b
    # ensure dark text on white
    if (b==0).mean()>0.5: b=255-b
    return b
def _main():
  for f in sys.argv[1:]:
        bgr=cv2.imread(f)
        r=mitutoyo_roi(bgr); kind='mitutoyo'
        if r is None: r=dyna_roi(bgr); kind='dyna'
        if r is None: print(f,'NO ROI'); continue
        x,y,w,h=r; crop=bgr[y:y+h,x:x+w]
        b=prep(crop,False)
        if kind=='mitutoyo':
            b=cv2.morphologyEx(b,cv2.MORPH_OPEN,np.ones((3,3),np.uint8))  # dot-matrix: merge dots
            b=cv2.erode(b,np.ones((7,7),np.uint8))
        cv2.imwrite('ocrlab/'+os.path.basename(f)+'.roi.png',b)
        print(f,kind,r,'->',repr(ocr(b)))

if __name__=='__main__': _main()
