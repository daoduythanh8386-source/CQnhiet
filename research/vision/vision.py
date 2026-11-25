"""Module xử lý ảnh + OCR công nghiệp (bản mẫu Python để kiểm thử thuật toán trên ảnh thật; bản C# OpenCvSharp port 1-1 tham số).
Nguyên tắc: ảnh gốc bất biến; chỉ dùng phép biến đổi tuyến tính / cục bộ (khử nhiễu, CLAHE, gamma, unsharp, nắn phối cảnh, phóng to
nội suy). KHÔNG vẽ lại / tái tạo ký tự, KHÔNG dùng AI sinh ảnh. Phóng to = nội suy, không phục hồi chi tiết đã mất."""
import cv2, numpy as np, re, subprocess, tempfile, os

PARAMS = dict(work_max=2400, clahe_clip=2.0, clahe_tile=8, gamma_dark=0.40, gamma_bright=0.72, gamma_min=0.55, gamma_max=1.6,
              unsharp_amount=0.6, unsharp_sigma=1.2, upscale_below=1600, roi_scale=3, noise_denoise=3.0)

# ---------------- chất lượng ảnh ----------------
def quality(bgr):
    g = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
    lap = cv2.Laplacian(g, cv2.CV_64F)
    sharp = float(lap.var())
    # nhiễu: độ lệch trung vị tuyệt đối của ảnh trừ bản lọc trung vị (ước lượng sigma)
    med = cv2.medianBlur(g, 3); noise = float(np.median(np.abs(g.astype(np.int16) - med.astype(np.int16)))) * 1.4826
    mean = float(g.mean()) / 255.0
    sat = float((g >= 250).mean())
    return dict(sharpness=sharp, noise=noise, brightness=mean, saturated=sat)

# ---------------- tăng chất lượng (HD màu) ----------------
def enhance_hd(bgr, q=None, upscale=True):
    q = q or quality(bgr); steps = []
    img = bgr
    if q['noise'] > PARAMS['noise_denoise']:
        h = float(min(10.0, q['noise'] * 1.2))
        img = cv2.fastNlMeansDenoisingColored(img, None, h, h, 7, 21); steps.append(f'khử nhiễu NLMeans h={h:.1f}')
    lab = cv2.cvtColor(img, cv2.COLOR_BGR2LAB); L, A, B = cv2.split(lab)
    L0 = L.copy()
    cl = cv2.createCLAHE(clipLimit=PARAMS['clahe_clip'], tileGridSize=(PARAMS['clahe_tile'],) * 2)
    L = cl.apply(L); steps.append(f'CLAHE clip={PARAMS["clahe_clip"]}')
    m = float(L.mean()) / 255.0
    g = 1.0
    if m < PARAMS['gamma_dark']: g = max(PARAMS['gamma_min'], np.log(0.5) / np.log(max(m, 1e-3)))
    elif m > PARAMS['gamma_bright']: g = min(PARAMS['gamma_max'], np.log(0.5) / np.log(min(m, 0.999)))
    if abs(g - 1.0) > 0.02:
        lut = np.clip(((np.arange(256) / 255.0) ** g) * 255.0 + 0.5, 0, 255).astype(np.uint8)
        L = cv2.LUT(L, lut); steps.append(f'gamma={g:.2f}')
    # chống cháy sáng: điểm sáng mới vượt 250 mà gốc < 235 → kéo về không quá 250
    over = (L > 250) & (L0 < 235)
    if over.any(): L = np.where(over, 250, L).astype(np.uint8)
    img = cv2.cvtColor(cv2.merge([L, A, B]), cv2.COLOR_LAB2BGR)
    blur = cv2.GaussianBlur(img, (0, 0), PARAMS['unsharp_sigma'])
    img = cv2.addWeighted(img, 1 + PARAMS['unsharp_amount'], blur, -PARAMS['unsharp_amount'], 0); steps.append(f'unsharp {PARAMS["unsharp_amount"]}/{PARAMS["unsharp_sigma"]}')
    if upscale and max(img.shape[:2]) < PARAMS['upscale_below']:
        img = cv2.resize(img, None, fx=2, fy=2, interpolation=cv2.INTER_CUBIC); steps.append('phóng to 2x (nội suy bicubic)')
    return img, steps

def print_bw(bgr):
    g = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
    lo, hi = np.percentile(g, 1), np.percentile(g, 99.5)
    if hi - lo > 10: g = np.clip((g.astype(np.float32) - lo) * 255.0 / (hi - lo), 0, 255).astype(np.uint8)
    g = cv2.createCLAHE(clipLimit=2.5, tileGridSize=(8, 8)).apply(g)
    b = cv2.GaussianBlur(g, (0, 0), 1.0); g = cv2.addWeighted(g, 1.5, b, -0.5, 0)
    return g

# ---------------- dò vùng hiển thị ----------------
def _quad_from_mask(mask):
    cs, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not cs: return None
    c = max(cs, key=cv2.contourArea)
    r = cv2.minAreaRect(c)
    return c, r

def detect_regions(bgr):
    H, W = bgr.shape[:2]; A = H * W
    hsv = cv2.cvtColor(bgr, cv2.COLOR_BGR2HSV)
    h, s, v = cv2.split(hsv)
    out = []
    # 1. LCD nền vàng-xanh (Mitutoyo…)
    m = ((h >= 25) & (h <= 50) & (s >= 60) & (v >= 120)).astype(np.uint8) * 255
    m = cv2.morphologyEx(m, cv2.MORPH_CLOSE, np.ones((15, 15), np.uint8))
    for c in cv2.findContours(m, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)[0]:
        x, y, w, hh = cv2.boundingRect(c); ar = w / max(hh, 1)
        if cv2.contourArea(c) > 0.002 * A and 1.8 <= ar <= 8:
            out.append(dict(kind='LCD', rect=(x, y, w, hh), rot=cv2.minAreaRect(c), score=cv2.contourArea(c) / A + 1))
    # 2. LED sáng đỏ / xanh lá
    red = (((h <= 10) | (h >= 170)) & (s >= 90) & (v >= 170)).astype(np.uint8) * 255
    grn = ((h >= 50) & (h <= 90) & (s >= 120) & (v >= 200)).astype(np.uint8) * 255
    for col, mm in (('LED đỏ', red), ('LED xanh', grn)):
        k = max(3, W // 60)
        mm2 = cv2.morphologyEx(mm, cv2.MORPH_CLOSE, np.ones((k // 2 + 1, k), np.uint8))
        for c in cv2.findContours(mm2, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)[0]:
            x, y, w, hh = cv2.boundingRect(c); ar = w / max(hh, 1)
            lit = mm[y:y + hh, x:x + w].mean() / 255
            if w * hh > 0.0008 * A and 1.5 <= ar <= 10 and 0.08 <= lit <= 0.7:
                out.append(dict(kind='LED', rect=(x, y, w, hh), rot=None, score=0.5 + w * hh / A, color=col))
    # 3. Màn hình sáng hình chữ nhật (TFT / LCD xám / tem nhãn trắng): biên Canny → đa giác 4 đỉnh
    g = cv2.GaussianBlur(cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY), (5, 5), 0)
    e = cv2.Canny(g, 40, 120); e = cv2.dilate(e, np.ones((3, 3), np.uint8))
    for c in cv2.findContours(e, cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)[0]:
        ca = cv2.contourArea(c)
        if ca < 0.01 * A or ca > 0.7 * A: continue
        p = cv2.approxPolyDP(c, 0.02 * cv2.arcLength(c, True), True)
        if len(p) != 4 or not cv2.isContourConvex(p): continue
        x, y, w, hh = cv2.boundingRect(p); ar = w / max(hh, 1)
        if not (0.9 <= ar <= 6): continue
        inner = hsv[y + hh // 8:y + hh - hh // 8, x + w // 8:x + w - w // 8]
        if inner.size == 0: continue
        vm, sm = inner[..., 2].mean(), inner[..., 1].mean()
        if vm < 110: continue
        kind = 'Tem nhãn' if sm < 35 and vm > 170 else 'Màn hình'
        out.append(dict(kind=kind, rect=(x, y, w, hh), quad=p.reshape(4, 2), rot=None, score=0.3 + ca / A))
    # 4. Đồng hồ kim
    cs = cv2.HoughCircles(g, cv2.HOUGH_GRADIENT, 1.5, min(H, W) / 3, param1=120, param2=80, minRadius=int(0.12 * min(H, W)), maxRadius=int(0.48 * min(H, W)))
    if cs is not None:
        for cx, cy, r in cs[0][:2]:
            out.append(dict(kind='Đồng hồ kim', rect=(int(cx - r), int(cy - r), int(2 * r), int(2 * r)), rot=None, score=0.2))
    # gộp trùng: giữ vùng điểm cao, bỏ vùng chồng > 60%
    out.sort(key=lambda d: -d['score']); keep = []
    for d in out:
        x, y, w, hh = d['rect']
        if all(_iou(d['rect'], k['rect']) < 0.6 for k in keep): keep.append(d)
    return keep[:6]

def _iou(a, b):
    ax, ay, aw, ah = a; bx, by, bw, bh = b
    ix = max(0, min(ax + aw, bx + bw) - max(ax, bx)); iy = max(0, min(ay + ah, by + bh) - max(ay, by))
    inter = ix * iy; return inter / max(1, min(aw * ah, bw * bh))

# ---------------- cắt + nắn vùng số đo ----------------
def order_quad(p):
    p = np.array(p, dtype=np.float32); s = p.sum(1); d = np.diff(p, axis=1).ravel()
    return np.array([p[np.argmin(s)], p[np.argmin(d)], p[np.argmax(s)], p[np.argmax(d)]], dtype=np.float32)

def rectify(bgr, region, pad=0.04):
    """Trả ảnh vùng đã nắn thẳng (phối cảnh nếu có 4 góc, xoay nếu có minAreaRect) — chỉ biến đổi hình học."""
    if region.get('quad') is not None:
        q = order_quad(region['quad'])
        w = int(max(np.linalg.norm(q[0] - q[1]), np.linalg.norm(q[3] - q[2]))); h = int(max(np.linalg.norm(q[0] - q[3]), np.linalg.norm(q[1] - q[2])))
        M = cv2.getPerspectiveTransform(q, np.array([[0, 0], [w, 0], [w, h], [0, h]], np.float32))
        return cv2.warpPerspective(bgr, M, (w, h), flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_REPLICATE), 'nắn phối cảnh 4 góc'
    x, y, w, h = region['rect']
    px, py = int(w * pad), int(h * pad)
    x0, y0 = max(0, x - px), max(0, y - py); x1, y1 = min(bgr.shape[1], x + w + px), min(bgr.shape[0], y + h + py)
    crop = bgr[y0:y1, x0:x1]
    rot = region.get('rot')
    if rot is not None:
        ang = rot[2]
        if ang > 45: ang -= 90
        if 0.5 <= abs(ang) <= 20:
            c = (crop.shape[1] / 2, crop.shape[0] / 2)
            M = cv2.getRotationMatrix2D(c, ang, 1.0)
            crop = cv2.warpAffine(crop, M, (crop.shape[1], crop.shape[0]), flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_REPLICATE)
            return crop, f'xoay thẳng {ang:.1f}°'
    return crop, 'cắt vùng'

def digit_box(roi, kind):
    """Vùng dòng số lớn nhất trong màn hình (ký tự tối trên nền sáng; LED: sáng trên nền tối)."""
    g = cv2.cvtColor(roi, cv2.COLOR_BGR2GRAY)
    if kind == 'LED':
        _, b = cv2.threshold(g, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    else:
        b = cv2.adaptiveThreshold(g, 255, cv2.ADAPTIVE_THRESH_MEAN_C, cv2.THRESH_BINARY_INV, max(15, (min(g.shape) // 4) | 1), 10)
    k = max(3, roi.shape[1] // 40)
    bc = cv2.morphologyEx(b, cv2.MORPH_CLOSE, np.ones((max(3, k // 2), k), np.uint8))
    n, lab, st, _ = cv2.connectedComponentsWithStats(bc)
    H, W = g.shape; best = None
    for i in range(1, n):
        x, y, w, h, a = st[i]
        if h < 0.25 * H or h > 0.95 * H or w < 0.15 * W or x == 0 and w == W: continue
        if best is None or w * h > best[2] * best[3]: best = (x, y, w, h)
    return best

# ---------------- OCR (Tesseract) ----------------
def tess(img, psm=7, wl='0123456789.,-'):
    with tempfile.NamedTemporaryFile(suffix='.png', delete=False) as f: cv2.imwrite(f.name, img); p = f.name
    try:
        r = subprocess.run(['tesseract', p, 'stdout', '--psm', str(psm), '-c', f'tessedit_char_whitelist={wl}', 'tsv'], capture_output=True, text=True)
    finally: os.unlink(p)
    words, confs = [], []
    for line in r.stdout.splitlines()[1:]:
        c = line.split('\t')
        if len(c) == 12 and c[11].strip() and float(c[10]) >= 0: words.append(c[11]); confs.append(float(c[10]) / 100)
    return ''.join(words), (min(confs) if confs else 0.0)

def ocr_roi(roi, kind):
    g = cv2.cvtColor(roi, cv2.COLOR_BGR2GRAY)
    g = cv2.resize(g, None, fx=PARAMS['roi_scale'], fy=PARAMS['roi_scale'], interpolation=cv2.INTER_CUBIC)
    _, b = cv2.threshold(g, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    if (b == 0).mean() > 0.5: b = 255 - b                         # chữ đen trên nền trắng
    k = max(3, g.shape[0] // 25)
    b = cv2.morphologyEx(b, cv2.MORPH_OPEN, np.ones((k, k), np.uint8))   # nối điểm ma trận / đoạn LED (chữ đen = 0 → mở trên nền trắng = nở nét đen)
    b = cv2.copyMakeBorder(b, 20, 20, 20, 20, cv2.BORDER_CONSTANT, value=255)
    return tess(b, 7, '0123456789.,-HRCBVkg')

UNIT = r'(HRC|HRB|HRA|HV|HB|kg|g)'
def parse(raw):
    s = (raw or '').replace(' ', '')
    m = re.search(r'(-?\d{1,4})[.,](\d{1,3})' + UNIT + '?', s) or re.search(r'(-?\d{1,4})()' + UNIT, s)
    if not m: return None, None
    v = m.group(1) + ('.' + m.group(2) if m.group(2) else '')
    return v, m.group(3)


def skew_angle(bgr):
    """Góc nghiêng của màn hình chính (LCD / màn hình chữ nhật lớn nhất); None nếu không chắc."""
    regs = [r for r in detect_regions(bgr) if r['kind'] in ('LCD', 'Màn hình', 'LED', 'Tem nhãn')]
    for r in regs:
        if r.get('rot') is not None:
            ang = r['rot'][2]
            if ang > 45: ang -= 90
            return ang
        if r.get('quad') is not None:
            q = order_quad(r['quad']); d = q[1] - q[0]
            return float(np.degrees(np.arctan2(d[1], d[0])))
    return None

def deskew(bgr):
    a = skew_angle(bgr)
    if a is None or not (0.7 <= abs(a) <= 25): return bgr, None
    h, w = bgr.shape[:2]
    M = cv2.getRotationMatrix2D((w / 2, h / 2), a, 1.0)
    return cv2.warpAffine(bgr, M, (w, h), flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_REPLICATE), f'xoay thẳng {a:.1f}°'

def for_reading(bgr):
    """Bản xử lý để ĐỌC SỐ — CHỈ sửa đúng lỗi đo được, giữ nguyên kích thước; ảnh tốt → giữ nguyên (không thêm bước thừa
    làm lệch bộ đọc): nghiêng → xoay thẳng; tối → gamma; nhiễu → khử nhiễu; tương phản thấp → CLAHE."""
    img, st = deskew(bgr); steps = [st] if st else []
    q = quality(img)
    if q['noise'] > PARAMS['noise_denoise']:
        h = float(min(10.0, q['noise'] * 1.2)); sw = 11 if img.shape[0] * img.shape[1] > 6_000_000 else 21; img = cv2.fastNlMeansDenoisingColored(img, None, h, h, 7, sw); steps.append(f'khử nhiễu h={h:.1f}')
    g = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY); m = float(g.mean()) / 255; sd = float(g.std())
    if m < 0.30:
        gm = max(PARAMS['gamma_min'], np.log(0.45) / np.log(max(m, 1e-3)))
        lut = np.clip(((np.arange(256) / 255.0) ** gm) * 255.0 + 0.5, 0, 255).astype(np.uint8); img = cv2.LUT(img, lut); steps.append(f'gamma={gm:.2f}')
    elif sd < 30:
        lab = cv2.cvtColor(img, cv2.COLOR_BGR2LAB); L, A, B = cv2.split(lab)
        L = cv2.createCLAHE(clipLimit=PARAMS['clahe_clip'], tileGridSize=(8, 8)).apply(L); img = cv2.cvtColor(cv2.merge([L, A, B]), cv2.COLOR_LAB2BGR); steps.append('CLAHE')
    return img, steps
