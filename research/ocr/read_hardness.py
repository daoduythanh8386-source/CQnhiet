"""Đọc số độ cứng từ ảnh máy đo: tự nhận loại máy.
- Mitutoyo (LCD ma trận điểm vàng-xanh): bộ đọc lưới điểm 5x7 (mitutoyo_dotmatrix).
- dynaROCK II (số xanh lớn trên nền trắng): Tesseract trên vùng số lớn, chỉ nhận dạng NN.N có dấu chấm thật.
Kết quả luôn là GỢI Ý: value=None nếu không chắc; QC phải xác nhận."""
import sys, re, json, cv2, numpy as np
sys.path.insert(0, __file__.rsplit('/', 1)[0])
from roi import mitutoyo_roi, dyna_roi, ocr
import mitutoyo_dotmatrix as mdm


def _dyna(img):
    r = dyna_roi(img)
    if r is None: return None
    x, y, w, h = (int(v) for v in r); crop = img[y:y + h, x:x + w]
    g = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY)
    g = cv2.resize(g, None, fx=200 / g.shape[0], fy=200 / g.shape[0], interpolation=cv2.INTER_CUBIC)
    _, b = cv2.threshold(g, 0, 255, cv2.THRESH_BINARY + cv2.THRESH_OTSU)
    if (b == 0).mean() > 0.5: b = 255 - b
    raw = ocr(b, 7, '0123456789.,')
    # Xác minh dấu thập phân bằng hình học: phải có 1 chấm nhỏ nằm sát chân dòng giữa các chữ số
    inv = 255 - b; n, lab, st, _ = cv2.connectedComponentsWithStats((inv > 0).astype(np.uint8))
    hs = sorted(st[1:, 3]); H = hs[-1] if hs else 0
    dots = [s for s in st[1:] if s[3] < 0.3 * H and s[2] < 0.3 * H and s[1] + s[3] > 0.75 * (st[1:, 1] + st[1:, 3]).max() and s[4] > 20]
    m = re.match(r'^(\d{1,3})[.,](\d)$', raw.replace(' ', ''))
    res = {'device': 'dynaROCK II', 'raw': raw, 'value': None, 'confidence': 0.3}
    if m and len(dots) == 1:
        res.update(value=f'{m.group(1)}.{m.group(2)}', confidence=0.8)   # Tesseract: tối đa 0.8, luôn chờ xác nhận
    else:
        res['reason'] = f'không chắc (chuỗi {raw!r}, số chấm thập phân thấy được: {len(dots)})'
    return res


def read(path):
    img = cv2.imread(path)
    if img is None: return {'file': path, 'value': None, 'reason': 'không đọc được ảnh'}
    if mitutoyo_roi(img) is not None:
        r = mdm.read_image(path); r['device'] = 'Mitutoyo'
    else:
        r = _dyna(img) or {'value': None, 'reason': 'không nhận ra màn hình máy đo'}
    r['file'] = path
    if r.get('value') is not None and not (10 <= float(r['value']) <= 75):
        r['reason'] = f"giá trị {r['value']} ngoài khoảng HRC hợp lý → bỏ"; r['value'] = None
    return r


if __name__ == '__main__':
    for f in sys.argv[1:]: print(json.dumps(read(f), ensure_ascii=False))
