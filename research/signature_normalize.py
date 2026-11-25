"""Quy ước chữ ký (người dùng 13/11/2026): mọi ảnh chữ ký đưa vào app được làm nền sáng đều màu giấy A4
và làm nét chữ ký; giữ màu mực. Ảnh gốc không đổi, ghi ra file mới (PNG).
Dùng: python signature_normalize.py <preset.json> <ảnh gốc> <ảnh ra.png>
"""
import sys, json, cv2, numpy as np


def normalize(preset, src, dst):
    p = json.load(open(preset))
    img = cv2.imread(src, cv2.IMREAD_COLOR)
    if img is None:
        raise SystemExit(f'Không đọc được ảnh: {src}')
    res = {'source': src, 'output': dst, 'warnings': []}
    f = img.astype(np.float32)
    # 1) Làm phẳng nền: ước lượng nền giấy bằng median lớn (khử bóng, ánh sáng không đều)
    k = max(31, (min(img.shape[:2]) // 12) | 1)
    bg = cv2.medianBlur(cv2.resize(img, None, fx=0.25, fy=0.25), (k // 4) | 1)
    bg = cv2.resize(bg, (img.shape[1], img.shape[0]), interpolation=cv2.INTER_LINEAR).astype(np.float32)
    flat = np.clip(f / np.maximum(bg, 1) * 255.0, 0, 255)          # nền ≈ 255, mực giữ màu tương đối
    # 2) Độ "đậm mực" 0..1 theo độ sáng; dưới ngưỡng nhiễu → coi là giấy
    dark = 1.0 - cv2.cvtColor(flat.astype(np.uint8), cv2.COLOR_BGR2GRAY).astype(np.float32) / 255.0
    t0, t1 = p['noise_floor'], p['ink_full']
    alpha = np.clip((dark - t0) / (t1 - t0), 0, 1) ** p['ink_gamma']
    # Bỏ đốm nhỏ lẻ (bụi, chấm giấy)
    mask = (alpha > 0.15).astype(np.uint8)
    n, lab, st, _ = cv2.connectedComponentsWithStats(mask, connectivity=8)
    small = np.zeros(n, bool); small[1:] = st[1:, 4] < max(p['min_speck_px'], p['min_speck_frac'] * alpha.size)  # ngưỡng theo cỡ ảnh
    alpha[small[lab]] = 0
    if (alpha > 0.5).sum() < 50:
        res['warnings'].append('Không thấy nét chữ ký rõ → kiểm tra ảnh')
    # 3) Màu mực: lấy màu trung vị của các điểm mực đậm, tăng đậm theo hệ số
    core = alpha > 0.7
    ink = np.median(flat[core], axis=0) if core.any() else np.array([120, 40, 60], np.float32)
    ink = np.clip(ink * p['ink_darken'], 0, 255)
    # 4) Làm nét: unsharp trên alpha (giữ mép nét gọn, không răng cưa)
    blur = cv2.GaussianBlur(alpha, (0, 0), p['sharpen_sigma'])
    alpha = np.clip(alpha + p['sharpen_amount'] * (alpha - blur), 0, 1)
    # 5) Ghép: nền màu giấy A4 đều + mực
    paper = np.array(p['paper_bgr'], np.float32)
    out = paper * (1 - alpha[..., None]) + ink * alpha[..., None]
    # 6) Cắt sát chữ ký + lề
    ys, xs = np.nonzero(alpha > 0.2)
    if len(xs):
        m = int(p['margin'] * max(np.ptp(xs), np.ptp(ys)))
        x0, x1 = max(0, xs.min() - m), min(img.shape[1], xs.max() + m)
        y0, y1 = max(0, ys.min() - m), min(img.shape[0], ys.max() + m)
        out = out[y0:y1, x0:x1]; res['crop'] = [int(x0), int(y0), int(x1), int(y1)]
        alpha = alpha[y0:y1, x0:x1]
    out = out.astype(np.uint8)
    if out.shape[1] > p['max_width']:
        s = p['max_width'] / out.shape[1]
        out = cv2.resize(out, None, fx=s, fy=s, interpolation=cv2.INTER_AREA)
        alpha = cv2.resize(alpha, (out.shape[1], out.shape[0]), interpolation=cv2.INTER_AREA)
    cv2.imwrite(dst, out)
    # Bản trong suốt (cùng nét mực đã làm nét) để chèn lên CQ không lộ khung nền — dùng khi cấu hình cho phép
    rgba = np.dstack([np.broadcast_to(ink, out.shape).astype(np.uint8), (alpha * 255).astype(np.uint8)])
    tdst = dst.rsplit('.', 1)[0] + '_trong-suot.png'
    cv2.imwrite(tdst, rgba); res['transparent_output'] = tdst
    res['ink_bgr'] = [int(v) for v in ink]; res['size'] = [int(out.shape[1]), int(out.shape[0])]
    return res


if __name__ == '__main__':
    print(json.dumps(normalize(*sys.argv[1:4]), ensure_ascii=False))
