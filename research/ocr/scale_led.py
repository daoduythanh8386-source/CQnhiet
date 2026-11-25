"""Đọc số trên màn hình LED đỏ 7 đoạn của đầu cân (ảnh xuất hàng gốc, ví dụ Ohaus Defender 2000).
Đọc cả dấu thập phân. Kết quả là GỢI Ý (tối đa 0.8) – QC xác nhận trước khi ghi lên ảnh CQ.
Dùng: python scale_led.py <ảnh gốc>..."""
import sys, re, json, cv2, numpy as np

# Đoạn: a trên, b phải-trên, c phải-dưới, d dưới, e trái-dưới, f trái-trên, g giữa
SEG = {'abcdef': '0', 'bc': '1', 'abged': '2', 'abgcd': '3', 'fgbc': '4', 'afgcd': '5', 'afgedc': '6',
       'abc': '7', 'abcdefg': '8', 'abcdfg': '9', 'g': '-', 'fbc': '7', 'afbc': '7', 'abcfg': '9', 'afgcde': '6'}


def find_display(img):
    """1) Mặt đỏ của đầu cân = vùng đỏ lớn nhất; 2) cửa sổ hiển thị = vùng tối lớn nhất nằm trong mặt đỏ;
    3) đoạn LED sáng = kênh G cao (lõi LED gần trắng) trong cửa sổ — kênh G tách rõ khỏi quầng đỏ."""
    hsv = cv2.cvtColor(img, cv2.COLOR_BGR2HSV)
    red = (((hsv[..., 0] < 12) | (hsv[..., 0] > 165)) & (hsv[..., 1] > 80) & (hsv[..., 2] > 120)).astype(np.uint8)
    n, lab, st, _ = cv2.connectedComponentsWithStats(cv2.dilate(red, np.ones((15, 15), np.uint8)))
    if n < 2: return None, None, []
    # mặt đỏ lớn nhất; nới lên/xuống 1.5 lần chiều cao: có đầu cân (Ohaus Defender) mặt đỏ bị cửa sổ tối chia làm 2 dải trên/dưới
    i = 1 + int(np.argmax(st[1:, 4])); x, y, w, h = (int(v) for v in st[i][:4])
    H0, W0 = img.shape[:2]
    ya, yb = max(0, y - int(1.5 * h)), min(H0, y + h + int(1.5 * h)); y, h = ya, yb - ya
    face = img[y:y + h, x:x + w]
    # cửa sổ tối: V thấp sau khi bỏ lõi LED (đóng hình thái để lấp chỗ chữ số)
    v = cv2.cvtColor(face, cv2.COLOR_BGR2HSV)[..., 2]
    G = face[..., 1]; R = face[..., 2]
    core = (G > 120) & (R > 180)
    dark = ((v < 110) | core).astype(np.uint8)
    dark = cv2.morphologyEx(dark, cv2.MORPH_CLOSE, np.ones((7, 7), np.uint8))
    dark = cv2.morphologyEx(dark, cv2.MORPH_OPEN, np.ones((5, 5), np.uint8))
    n2, lab2, st2, _ = cv2.connectedComponentsWithStats(dark)
    cand = []
    for k in range(1, n2):
        cx, cy, cw, chh, ca = (int(t) for t in st2[k])
        if cw <= 2.0 * chh or ca <= 0.015 * w * h: continue
        box_v = v[cy:cy + chh, cx:cx + cw]
        if (box_v < 110).mean() < 0.4: continue                 # tem dán / nhãn trắng (sáng) → không phải cửa sổ hiển thị
        cand.append((cx, cy, cw, chh, int(core[cy:cy + chh, cx:cx + cw].sum()), ca))
    if not cand: return None, None, []
    # chọn cửa sổ có nhiều điểm LED sáng nhất (rồi tới diện tích)
    wx, wy, ww, wh, _, _ = max(cand, key=lambda c: (c[4], c[5]))
    ext = int(0.15 * ww)                                   # nới ngang: chữ số cuối có thể sát mép phải
    wx, ww = max(0, wx - ext), min(w, wx + ww + ext) - max(0, wx - ext)
    win = face[wy:wy + wh, wx:wx + ww]
    g = cv2.GaussianBlur(win[..., 1], (3, 3), 0)
    t, _ = cv2.threshold(g, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    lit = (g > max(t, 90)).astype(np.uint8)
    n3, lab3, st3, _ = cv2.connectedComponentsWithStats(lit)
    comps = [tuple(int(t) for t in st3[k]) for k in range(1, n3) if st3[k][4] >= 4]
    return lit, (x + wx, y + wy, ww, wh), comps


def decode7(sub):
    h, w = sub.shape
    def on(y_a, y_b, x_a, x_b):
        r = sub[int(y_a * h):max(int(y_a * h) + 1, int(y_b * h)), int(x_a * w):max(int(x_a * w) + 1, int(x_b * w))]
        return r.mean() > 0.15
    s = {'a': on(0.0, 0.16, 0.3, 0.85), 'g': on(0.42, 0.58, 0.25, 0.75), 'd': on(0.84, 1.0, 0.15, 0.7),
         'f': on(0.12, 0.45, 0.0, 0.35), 'b': on(0.12, 0.45, 0.65, 1.0),
         'e': on(0.55, 0.88, 0.0, 0.35), 'c': on(0.55, 0.88, 0.65, 1.0)}
    key = ''.join(k for k in 'abcdefg' if s[k])
    return next((v for k, v in SEG.items() if set(k) == set(key)), None), key


def read(path):
    img = cv2.imread(path)
    lit, box, comps = find_display(img)
    if lit is None or not comps:
        return {'file': path, 'value': None, 'reason': 'không thấy màn hình LED'}
    Hh, Ww = lit.shape
    inner = [c for c in comps if c[0] > 1 and c[1] > 1 and c[0] + c[2] < Ww - 1 and c[1] + c[3] < Hh - 1]
    if not inner: return {'file': path, 'value': None, 'reason': 'không thấy chữ số'}
    hmax = max(c[3] for c in inner)
    tall = [c for c in inner if c[3] >= 0.4 * hmax]
    y0 = min(c[1] for c in tall if c[3] >= 0.8 * hmax); y1 = max(c[1] + c[3] for c in tall if c[3] >= 0.8 * hmax); H = y1 - y0
    tall = [c for c in tall if c[1] >= y0 - 0.2 * H and c[1] + c[3] <= y1 + 0.2 * H]
    # bỏ đèn báo tròn/vuông (kg, Stable…) và chấm: khối thấp (< 0.5 chiều cao số) gần vuông — đoạn LED dọc thì mảnh
    tall = [c for c in tall if not (c[3] < 0.5 * H and c[2] >= 0.6 * c[3])]
    # LED đoạn rời (vd Ohaus Defender: mỗi đoạn một khối riêng): thêm các đoạn NGANG (a/g/d) trong dải dòng số để nối
    # đoạn dọc trái + phải của cùng một chữ số khi gom cột (không có chúng '4' bị tách thành 2 chữ số '1')
    bars = [c for c in inner if c[2] >= 0.2 * H and c[3] < 0.3 * H and c[2] > 2 * c[3]
            and c[1] >= y0 - 0.35 * H and c[1] + c[3] <= y1 + 0.2 * H and c not in tall]
    tall = tall + bars
    # gom các đoạn rời của cùng chữ số theo chồng lấn cột
    tall.sort(key=lambda c: c[0]); groups = []
    for c in tall:
        if groups and c[0] <= groups[-1][1] + 0.12 * H: groups[-1][1] = max(groups[-1][1], c[0] + c[2])
        else: groups.append([c[0], c[0] + c[2]])
    dots = [c for c in inner if c[3] < 0.3 * H and c[2] < 0.3 * H and c[1] + c[3] >= y1 - 0.3 * H and c[1] >= y0 + 0.55 * H]
    tokens = []
    for xa, xb in groups:
        # khung riêng từng chữ số theo điểm sáng thật (đoạn 'a' có thể nằm cao hơn dải chung)
        reg = lit[max(0, int(y0 - 0.35 * H)):int(y1 + 0.1 * H), xa:xb]
        rows = np.nonzero(reg.any(1))[0]; oy = max(0, int(y0 - 0.35 * H))
        sub = reg[rows.min():rows.max() + 1] if len(rows) else lit[y0:y1, xa:xb]
        w = xb - xa
        if w < 0.3 * H:
            ch = '1'
            # '1' dính chấm thập phân ở chân phải: phần đáy rộng hơn hẳn phần thân
            hs = sub.shape[0]
            stem = np.nonzero(sub[int(0.2 * hs):int(0.6 * hs)].any(0))[0]
            right = sub[int(0.65 * hs):, stem.max() + 2:] if len(stem) else sub[:0]
            tokens.append((xa, ch))
            # chấm thập phân dính chân phải '1': có điểm sáng ở 1/3 dưới, nằm bên phải thân chữ số
            if right.size and right.sum() >= 4: tokens.append((xb - 0.5, '.'))
            continue
        ch, key = decode7(sub)
        tokens.append((xa, ch if ch else '?'))
    for c in dots:
        cx = c[0] + c[2] / 2
        inside_line = groups and groups[0][0] < cx < groups[-1][1]      # đèn báo trạng thái nằm ngoài dòng số → bỏ
        if inside_line and not any(xa <= cx <= xb for xa, xb in groups): tokens.append((c[0], '.'))
    out = ''.join(ch for _, ch in sorted(tokens))
    res = {'file': path, 'raw': out, 'value': None, 'confidence': 0.0, 'display_box': box}
    if re.fullmatch(r'-?\d{1,4}(\.\d{1,3})?', out):
        res.update(value=out, confidence=0.8)   # 1 ảnh mẫu → luôn chờ QC xác nhận
    else:
        res['reason'] = f'không chắc ({out!r}) → nhập tay'
    return res


if __name__ == '__main__':
    for f in sys.argv[1:]: print(json.dumps(read(f), ensure_ascii=False))
