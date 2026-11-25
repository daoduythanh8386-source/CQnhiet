"""Chuẩn hóa ảnh máy đo Mitutoyo để đưa vào khung "ẢNH ĐỘ CỨNG" của CQ — theo ảnh mẫu người dùng
(ref_mitu_cropped_50.1.jpg): cắt quanh mặt máy (logo → mép dưới thân máy, thấy một phần đe đo) và làm sáng/tăng tương phản.
Ảnh gốc không bị sửa; luôn ghi ra file mới. Tham số nằm trong config/image-presets/mitutoyo-cq.json.
Dùng: python image_normalize.py <preset.json> <ảnh gốc> <ảnh ra.jpg>
"""
import sys, json, cv2, numpy as np
sys.path.insert(0, __file__.rsplit('/', 1)[0] + '/ocr')
from roi import mitutoyo_roi


def crop_box(img, lcd, p):
    """Khung cắt tính theo tâm và bề rộng LCD (LCD có kích thước cố định trên máy → không phụ thuộc khoảng cách chụp)."""
    H, W = img.shape[:2]
    x, y, w, h = lcd
    cx, cy = x + w / 2, y + h / 2
    # Nếu ảnh gốc không đủ rộng (chụp sát), thu nhỏ đều cả 4 lề theo cùng hệ số k
    # để giữ đúng tỉ lệ khung và vị trí LCD như ảnh mẫu (không cắt lệch, không méo).
    k = min(1.0, cx / (p['left'] * w), (W - cx) / (p['right'] * w), cy / (p['top'] * w), (H - cy) / (p['bottom'] * w))
    x0, x1 = cx - k * p['left'] * w, cx + k * p['right'] * w
    y0, y1 = cy - k * p['top'] * w, cy + k * p['bottom'] * w
    box = (max(0, int(round(x0))), max(0, int(round(y0))), min(W, int(round(x1))), min(H, int(round(y1))))
    return box, k


def product_box(img, p):
    """Ảnh sản phẩm (ẢNH XUẤT HÀNG): khung = bao của các vật kim loại sáng, KHÔNG chạm mép ảnh
    (loại nền sàn, giá kệ), loại vùng có LED đỏ (đầu cân), rồi nới lề theo tỉ lệ ảnh mẫu."""
    H, W = img.shape[:2]
    hsv = cv2.cvtColor(img, cv2.COLOR_BGR2HSV)
    V = cv2.GaussianBlur(hsv[..., 2], (5, 5), 0); S = hsv[..., 1]; Hh = hsv[..., 0]
    t, _ = cv2.threshold(V, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    m = ((V > t) & (S < p['max_saturation'])).astype(np.uint8) * 255
    m = cv2.morphologyEx(m, cv2.MORPH_OPEN, np.ones((5, 5), np.uint8))
    red = (((Hh < 10) | (Hh > 170)) & (S > 120) & (hsv[..., 2] > 120))
    n, lab, st, _ = cv2.connectedComponentsWithStats(m)
    boxes = []
    for i in range(1, n):
        x, y, w, h, a = (int(v) for v in st[i])
        if a < p['min_component_area'] * H * W: continue
        if x <= 2 or y <= 2 or x + w >= W - 2 or y + h >= H - 2: continue      # nền/kệ chạm mép
        if red[y:y + h, x:x + w].mean() > 0.02: continue                      # màn hình LED cân
        boxes.append((x, y, x + w, y + h))
    if len(boxes) < p['min_components']:
        return None, len(boxes)
    x0 = min(b[0] for b in boxes); y0 = min(b[1] for b in boxes); x1 = max(b[2] for b in boxes); y1 = max(b[3] for b in boxes)
    bw, bh = x1 - x0, y1 - y0; mg = p['margin']
    box = (max(0, int(x0 - mg['left'] * bw)), max(0, int(y0 - mg['top'] * bh)),
           min(W, int(x1 + mg['right'] * bw)), min(H, int(y1 + mg['bottom'] * bh)))
    return box, len(boxes)


def enhance(img, p):
    out = img.astype(np.float32)
    # 0) Cân bằng trắng gray-world (một phần) — ảnh điện thoại thường ám xanh dưới đèn huỳnh quang
    # Đưa tỉ lệ trung bình B:G:R về tỉ lệ của ảnh mẫu (ảnh mẫu hơi ấm) — nhân hệ số từng kênh
    wb = p.get('white_balance_strength', 0)
    if wb > 0:
        means = out.reshape(-1, 3).mean(0); g = means.mean()
        target = np.array(p.get('target_bgr_ratio', [1, 1, 1]), np.float32)
        target = target / target.mean() * g
        out = np.clip(out * (1 + wb * (target / means - 1)), 0, 255)
    # 1) Kéo giãn mức sáng theo phân vị (giữ màu: cùng hệ số cho 3 kênh)
    lum = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY).astype(np.float32)
    lo, hi = np.percentile(lum, [p['stretch_low_pct'], p['stretch_high_pct']])
    out = np.clip((out - lo) * (p.get('stretch_white', 255) / max(1.0, hi - lo)), 0, 255)  # stretch_white<255: chừa khoảng sáng, tránh cháy mặt kim loại
    # 2) Gamma tự chọn để độ sáng trung bình bằng ảnh mẫu (giới hạn để không cháy LCD)
    mean = max(1.0, out.mean())
    gamma = float(np.clip(np.log(p['target_mean'] / 255.0) / np.log(mean / 255.0), p['gamma_min'], p['gamma_max']))
    out = 255.0 * (out / 255.0) ** gamma
    out = np.clip(out, 0, 255).astype(np.uint8)
    # 3) Bão hòa màu: >1 tăng (LCD), <1 giảm (kim loại trung tính, bớt ám màu)
    hsv = cv2.cvtColor(out, cv2.COLOR_BGR2HSV).astype(np.float32)
    hsv[..., 1] = np.clip(hsv[..., 1] * p['saturation'], 0, 255)
    out = cv2.cvtColor(hsv.astype(np.uint8), cv2.COLOR_HSV2BGR)
    # 4) Làm nét nhẹ (unsharp mask) — nhẹ để không làm mất/nhòe dấu chấm thập phân
    if p['sharpen_amount'] > 0:
        blur = cv2.GaussianBlur(out, (0, 0), p['sharpen_sigma'])
        out = cv2.addWeighted(out, 1 + p['sharpen_amount'], blur, -p['sharpen_amount'], 0)
    return out


def normalize(preset, src, dst):
    p = json.load(open(preset))
    img = cv2.imread(src)
    if img is None:
        raise SystemExit(f'Không đọc được ảnh: {src}')
    result = {'source': src, 'output': dst, 'mode': p.get('mode', 'mitutoyo'), 'crop': None, 'needsManualCrop': False, 'warnings': []}
    lcd = None
    if p.get('mode') == 'product':
        box, cnt = product_box(img, p['detect'])
        result['components'] = cnt
        if box is None:
            result['needsManualCrop'] = True
            result['warnings'].append('Không nhận ra vùng sản phẩm → giữ nguyên khung, cần cắt tay')
            box = (0, 0, img.shape[1], img.shape[0])
        else:
            frac = (box[2] - box[0]) * (box[3] - box[1]) / (img.shape[0] * img.shape[1])
            if not (p['detect']['area_min'] <= frac <= p['detect']['area_max']):
                result['needsManualCrop'] = True
                result['warnings'].append(f'Vùng sản phẩm chiếm {frac:.0%} ảnh (bất thường) → kiểm tra/cắt tay')
    elif (lcd := mitutoyo_roi(img)) is None:
        result['needsManualCrop'] = True
        result['warnings'].append('Không tìm thấy màn hình LCD Mitutoyo → giữ nguyên khung, cần cắt tay')
        box = (0, 0, img.shape[1], img.shape[0])
    else:
        lcd = tuple(int(v) for v in lcd)
        result['lcd'] = lcd
        box, k = crop_box(img, lcd, p['crop'])
        result['scale'] = round(k, 3)
        if k < p['min_scale']:
            result['needsManualCrop'] = True
            result['warnings'].append(f'Ảnh chụp quá sát (chỉ lấy được {k:.0%} khung chuẩn) → có thể mất thân máy, cần kiểm tra/cắt tay')
        aspect = lcd[2] / max(1, lcd[3])
        if not (p['lcd_aspect_min'] <= aspect <= p['lcd_aspect_max']):
            result['needsManualCrop'] = True
            result['warnings'].append(f'Tỉ lệ LCD bất thường ({aspect:.2f}) → độ tin cậy thấp, cần cắt tay')
    x0, y0, x1, y1 = box
    result['crop'] = [x0, y0, x1, y1]
    out = enhance(img[y0:y1, x0:x1], p['enhance'])
    if out.shape[1] > p['max_width']:
        s = p['max_width'] / out.shape[1]
        out = cv2.resize(out, None, fx=s, fy=s, interpolation=cv2.INTER_AREA)
    cv2.imwrite(dst, out, [cv2.IMWRITE_JPEG_QUALITY, p['jpeg_quality']])
    return result


if __name__ == '__main__':
    print(json.dumps(normalize(*sys.argv[1:4]), ensure_ascii=False))
