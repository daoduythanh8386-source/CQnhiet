"""Đọc LCD ma trận điểm 5x7 của máy Mitutoyo (font HD44780) — đọc theo lưới điểm, không dùng Tesseract.
Đọc đủ chữ số, dấu chấm thập phân '.', dấu phẩy ',', dấu trừ '-' và đơn vị (HRC/HRB/HV…).
Trả về giá trị + độ tin cậy; không chắc chắn thì KHÔNG trả số (bắt QC nhập tay).
Dùng: python mitutoyo_dotmatrix.py <ảnh>...
"""
import sys, re, json, cv2, numpy as np
sys.path.insert(0, __file__.rsplit('/', 1)[0])
from roi import mitutoyo_roi

# Font HD44780 (ROM A00) – các ký tự có thể xuất hiện trên màn hình máy đo
GLYPHS = {
    '0': "01110 10001 10011 10101 11001 10001 01110", '1': "00100 01100 00100 00100 00100 00100 01110",
    '2': "01110 10001 00001 00010 00100 01000 11111", '3': "11111 00010 00100 00010 00001 10001 01110",
    '4': "00010 00110 01010 10010 11111 00010 00010", '5': "11111 10000 11110 00001 00001 10001 01110",
    '6': "00110 01000 10000 11110 10001 10001 01110", '7': "11111 00001 00010 00100 01000 01000 01000",
    '8': "01110 10001 10001 01110 10001 10001 01110", '9': "01110 10001 10001 01111 00001 00010 01100",
    '.': "00000 00000 00000 00000 00000 01100 01100", ',': "00000 00000 00000 00000 01100 00100 01000",
    '-': "00000 00000 00000 11111 00000 00000 00000", ' ': "00000 00000 00000 00000 00000 00000 00000",
    'H': "10001 10001 10001 11111 10001 10001 10001", 'R': "11110 10001 10001 11110 10100 10010 10001",
    'C': "01110 10001 10000 10000 10000 10001 01110", 'B': "11110 10001 10001 11110 10001 10001 11110",
    'V': "10001 10001 10001 10001 10001 01010 00100", 'A': "01110 10001 10001 10001 11111 10001 10001",
    'S': "01111 10000 10000 01110 00001 00001 11110", 'N': "10001 10001 11001 10101 10011 10001 10001",
    'W': "10001 10001 10001 10101 10101 10101 01010", 'K': "10001 10010 10100 11000 10100 10010 10001",
}
G = {k: np.array([[int(c) for c in r] for r in v.split()], np.uint8) for k, v in GLYPHS.items()}


def _sample(dark, top, x0, p, ncols, px=None):
    px = px or p
    """Lấy mẫu độ đậm tại tâm từng điểm của lưới 7 x ncols (mỗi điểm = trung bình vùng giữa ô)."""
    h, w = dark.shape
    M = np.zeros((7, ncols), np.float32)
    r = max(1, int(min(p, px) * 0.25))
    for i in range(7):
        cy = int(top + (i + 0.5) * p)
        for j in range(ncols):
            cx = int(x0 + (j + 0.5) * px)
            if 0 <= cy < h and 0 <= cx < w:
                M[i, j] = dark[max(0, cy - r):cy + r + 1, max(0, cx - r):cx + r + 1].mean()
    return M


def read_lcd(crop):
    g = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY)
    scale = 300.0 / g.shape[0]
    g = cv2.resize(g, None, fx=scale, fy=scale, interpolation=cv2.INTER_CUBIC)
    g = cv2.GaussianBlur(g, (3, 3), 0)
    # Chuẩn hóa nền không đều rồi đảo: điểm sáng = mực LCD
    bg = cv2.morphologyEx(g, cv2.MORPH_CLOSE, np.ones((61, 61), np.uint8))
    dark = np.clip((bg.astype(np.float32) - g) / np.maximum(bg.astype(np.float32), 1), 0, 1)
    t, _ = cv2.threshold((dark * 255).astype(np.uint8), 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    binm = (dark * 255 > t).astype(np.uint8)
    H, W = binm.shape
    # Bỏ đường viền khung LCD: nét ngang/dọc dài liên tục (ký tự ma trận điểm không có nét dài như vậy)
    lines = cv2.morphologyEx(binm, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_RECT, (int(W * 0.3), 1)))
    lines |= cv2.morphologyEx(binm, cv2.MORPH_OPEN, cv2.getStructuringElement(cv2.MORPH_RECT, (1, int(H * 0.8))))
    binm = binm & (1 - cv2.dilate(lines, np.ones((5, 5), np.uint8)))
    n, lab, st, _ = cv2.connectedComponentsWithStats(binm)
    for i in range(1, n):
        if st[i][4] < 15: binm[lab == i] = 0
    # Dòng chữ = đoạn liên tục dài nhất các hàng có mật độ ≥ 15% hàng đậm nhất
    rs = binm.sum(1); act = rs > 0.15 * rs.max()
    runs, start = [], None
    for i, a in enumerate(list(act) + [False]):
        if a and start is None: start = i
        if not a and start is not None: runs.append((start, i)); start = None
    if not runs:
        return None, 0.0, 'không thấy chữ trên LCD'
    t0, t1 = max(runs, key=lambda r: r[1] - r[0])
    rows = np.arange(t0, t1)
    band = binm[t0:t1]
    cols = np.nonzero(band.sum(0) > 0)[0]
    if len(rows) < 10 or len(cols) < 10:
        return None, 0.0, 'không thấy chữ trên LCD'
    p = (t1 - t0) / 7.0                       # bước điểm dọc (7 hàng điểm)
    band = binm[t0:t1]
    # Tách ký tự: LCD có khe hẹp giữa các ô ký tự (≈0.4 bước điểm), khác khe giữa các điểm (rất nhỏ)
    colsum = band.sum(0) > 0.3 * p          # cột thật có ít nhất ~1 điểm; bỏ nhiễu lẻ
    runs, start, gap = [], None, 0
    for x, on in enumerate(list(colsum) + [False] * int(p)):
        if on:
            if start is None: start = x
            last, gap = x, 0
        elif start is not None:
            gap += 1
            if gap >= 0.18 * p:
                runs.append((start, last + 1)); start = None
    runs = [r for r in runs if r[1] - r[0] >= 0.5 * p]   # bỏ mảnh viền/nhiễu hẹp hơn 1 điểm
    if not runs:
        return None, 0.0, 'không tách được ký tự'
    # Bề rộng ký tự đủ 5 cột (W5): run hẹp nhất trong số run "rộng" (≥ 0.45 chiều cao chữ).
    # '.' (2 cột) và '1' (3 cột) hẹp hơn nên bị loại; run dính nhiều ký tự rộng gấp k lần → tách đều.
    wide = [r for r in runs if r[1] - r[0] >= 0.45 * (t1 - t0)]
    if not wide:
        return None, 0.0, 'không thấy ký tự đủ rộng'
    W5 = min(r[1] - r[0] for r in wide)
    gaps0 = [b0 - a1 for (a0, a1), (b0, b1) in zip(runs, runs[1:])]
    g = float(np.median([x for x in gaps0 if x < 0.5 * W5])) if any(x < 0.5 * W5 for x in gaps0) else 0.1 * W5
    anchors = []        # ô ký tự chắc chắn: (x_bắt_đầu, rộng)
    for a0, a1 in wide:
        k = max(1, int(round((a1 - a0 + g) / (W5 + g))))
        cw = (a1 - a0 - (k - 1) * g) / k
        anchors += [(a0 + i * (cw + g), cw) for i in range(k)]
    # Điền ô vào khoảng trống giữa các ô chắc chắn (nơi có '.', '1', dấu cách…); chia đều để chịu được phối cảnh nghiêng
    cells = []
    lead = runs[0][0]
    if lead < anchors[0][0] - 0.5 * W5:
        n = int(round((anchors[0][0] - lead) / (W5 + g)))
        pitch = (anchors[0][0] - lead) / max(1, n)
        cells += [(anchors[0][0] - (n - i) * pitch, W5) for i in range(n)]
    for (x, w), nxt in zip(anchors, anchors[1:] + [None]):
        cells.append((x, w))
        if nxt is None: break
        free = nxt[0] - (x + w)
        n = int(round((free - g) / (W5 + g)))
        if n > 0:
            pitch = (free - g) / n
            cells += [(x + w + g + i * pitch, pitch - g) for i in range(n)]
    tail = runs[-1][1]
    last_end = anchors[-1][0] + anchors[-1][1]
    if tail > last_end + 0.3 * W5:
        n = int(round((tail - last_end) / (W5 + g)))
        cells += [(last_end + g + i * (W5 + g), W5) for i in range(max(1, n))]
    on = dark[t0:t1][band > 0]; off = dark[t0:t1][band == 0]
    thr = 0.5 * (on.mean() + off.mean())
    text, dists = '', []
    for cx0, cw in cells:
        M = _sample(dark, t0, cx0, p, 5, cw / 5)
        cell = (M > thr).astype(np.uint8)
        ranked = sorted(((int(np.abs(cell.astype(int) - gl).sum()), ch) for ch, gl in G.items()))
        d, ch = ranked[0]
        text += ch
        if not (ch == ' ' and d == 0):
            dists.append((d, ranked[1][0] - d, ch))
    cp, px = W5 + g, W5 / 5
    text = text.strip()
    # Độ tin cậy chỉ xét PHẦN SỐ (chữ số, dấu . , -) — đơn vị HRC đọc sai không làm sai giá trị.
    num = []
    for d, margin, ch in dists:
        if ch in '0123456789.,-': num.append((d, margin))
        else: break
    if not num:
        conf = 0.0
    elif all(d == 0 for d, _ in num):
        conf = 1.0          # khớp tuyệt đối từng điểm của mọi chữ số và dấu thập phân
    elif all(d <= 3 and m >= 4 for d, m in num):
        conf = 0.8          # gần đúng, cách xa ký tự khác → gợi ý, tô vàng chờ QC xác nhận
    else:
        conf = 0.3          # không tin cậy → không điền số
    return text, conf, f'bước dọc {p:.1f}, ô ký tự {cp:.1f}, ngang {px:.1f}; lệch {[(c, d) for d, _, c in dists]}'


VALUE = re.compile(r'^(-?\d{1,3})([.,])(\d{1,2})\s*([A-Z]*)$')


def read_image(path):
    img = cv2.imread(path)
    roi = mitutoyo_roi(img)
    if roi is None:
        return {'file': path, 'value': None, 'reason': 'không tìm thấy LCD'}
    x, y, w, h = roi
    text, conf, info = read_lcd(img[y:y + h, x:x + w])
    out = {'file': path, 'raw': text, 'confidence': conf, 'info': info, 'value': None, 'unit': None}
    m = VALUE.match(text or '')
    if m and conf >= 0.8:
        out['value'] = f'{m.group(1)}.{m.group(3)}'   # giữ đúng số chữ số; dấu ',' của máy chuẩn hóa thành '.'
        out['decimalSeparatorOnDevice'] = m.group(2)
        out['unit'] = m.group(4) if m.group(4) in ('HRC', 'HRB', 'HRA', 'HV', 'HB', 'HRF') else None
        if out['unit'] is None: out['warning'] = 'đơn vị đọc không rõ: ' + m.group(4)
    else:
        out['reason'] = 'không chắc chắn → bắt buộc nhập tay/xác nhận'
    return out


if __name__ == '__main__':
    for f in sys.argv[1:]:
        print(json.dumps(read_image(f), ensure_ascii=False))
