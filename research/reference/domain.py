"""Cài đặt THAM CHIẾU (Python) cho quy tắc nghiệp vụ đã chốt — đặc tả chạy được để port sang CqNl.Domain (.NET).
Không phải ứng dụng. Nguồn quy tắc: docs/business-rules.md."""
import re
from decimal import Decimal, InvalidOperation

# ---------- Yêu cầu độ cứng ----------
_REQ = re.compile(r'^\s*(\d{1,2}(?:[.,]\d+)?)\s*[-–]\s*(\d{1,2}(?:[.,]\d+)?)\s*$')


def parse_requirement(raw):
    """'58-62' → (Decimal(58), Decimal(62)); không hợp lệ → None (không đoán: MAX, ngày, HV, >70 như 49-91 …)."""
    if raw is None or not isinstance(raw, str):
        return None
    m = _REQ.match(raw)
    if not m:
        return None
    a, b = (Decimal(x.replace(',', '.')) for x in m.groups())
    if not (0 < a < b <= 70):
        return None
    return a, b


# ---------- Dung sai (OQ-01 đã chốt) ----------
_TOL = [
    (re.compile(r'^\(MAX-MIN\)±(\d+(?:\.\d+)?)$'), lambda a, b, n: (a - n, b + n)),
    (re.compile(r'^MIN-(\d+(?:\.\d+)?)$'), lambda a, b, n: (a - n, b)),
    (re.compile(r'^MIN±(\d+(?:\.\d+)?)$'), lambda a, b, n: (a - n, b)),
    (re.compile(r'^MAX\+(\d+(?:\.\d+)?)$'), lambda a, b, n: (a, b + n)),
]


class ToleranceError(ValueError):
    pass


def acceptance_limits(req, tolerance):
    a, b = req
    if tolerance is None or str(tolerance).strip() == '':
        return a, b
    t = re.sub(r'\s+', '', str(tolerance)).upper()
    for rx, f in _TOL:
        m = rx.match(t)
        if m:
            return f(a, b, Decimal(m.group(1)))
    raise ToleranceError(f'Dung sai không nhận dạng được: {tolerance!r} → chặn tạo CQ')


# ---------- Điểm đo ----------
def parse_points(text):
    """'58.4 58,8;59' → [Decimal('58.4'), Decimal('58.8'), Decimal('59')]. Lỗi → ValueError (không bỏ qua lặng lẽ)."""
    if text is None or text.strip() == '':
        return []
    out = []
    for tok in re.split(r'[\s;/]+', text.strip()):
        if not tok:
            continue
        try:
            v = Decimal(tok.replace(',', '.'))
        except InvalidOperation:
            raise ValueError(f'Điểm đo không hợp lệ: {tok!r}')
        if not (0 < v <= 100):
            raise ValueError(f'Điểm đo ngoài khoảng: {tok!r}')
        out.append(v)
    return out


def evaluate(req_raw, tolerance, samples):
    """samples: list tối đa 6 chuỗi. ĐẠT khi MỌI điểm nằm trong giới hạn (OQ-02)."""
    if len(samples) > 6:
        raise ValueError('Tối đa 6 mẫu')
    req = parse_requirement(req_raw)
    if req is None:
        return {'status': 'INVALID_REQUIREMENT'}
    lo, hi = acceptance_limits(req, tolerance)
    pts = [p for s in samples for p in parse_points(s)]
    if not pts:
        return {'status': 'NO_DATA', 'limits': (lo, hi)}
    fails = [p for p in pts if p < lo or p > hi]
    return {'status': 'PASS' if not fails else 'FAIL', 'limits': (lo, hi), 'min': min(pts), 'max': max(pts),
            'avg': (sum(pts) / len(pts)).quantize(Decimal('0.01')), 'failing': fails}


def cq_sample_cell(text):
    """Ô kết quả trên CQ: các điểm nối ' / ', giữ nguyên số chữ số người nhập; trống → '-'."""
    toks = [t.replace(',', '.') for t in re.split(r'[\s;/]+', (text or '').strip()) if t]
    return ' / '.join(toks) if toks else '-'


def remark(status, note=''):
    if status == 'PASS':
        return 'Nhận xét/Remark: Chất lượng đạt'
    return 'Nhận xét/Remark: Không đạt độ cứng' + (f'; {note}' if note else '')


# ---------- Gộp đơn ----------
def norm_grade(g):
    return re.sub(r'[\s\-_.]', '', (g or '')).upper()


def can_merge(lines):
    """lines: dict có Dngc, SteelGrade, HardnessRequirement, HasCq, MergeGroup."""
    if len(lines) < 2:
        return False, 'Cần chọn ít nhất 2 dòng'
    if len({l['Dngc'] for l in lines}) > 1:
        return False, 'Khác số ĐNGC'
    if len({norm_grade(l['SteelGrade']) for l in lines}) > 1:
        return False, 'Khác mác thép'
    reqs = {parse_requirement(l['HardnessRequirement']) for l in lines}
    if None in reqs or len(reqs) > 1:
        return False, 'Khác (hoặc thiếu) yêu cầu độ cứng'
    if any(l.get('HasCq') for l in lines):
        return False, 'Có dòng đã có CQ'
    if any(l.get('MergeGroup') for l in lines):
        return False, 'Có dòng đã thuộc nhóm gộp khác'
    return True, ''


# ---------- Số báo cáo (OQ-09) ----------
def next_report_no(existing_numbers, year_month):
    """existing_numbers: tập NNN đã cấp (kể cả của CQ đã xóa) trong năm-tháng. Trả 'NNN/MM' = lớn nhất + 1.
    KHÔNG dùng lại số của CQ đã xóa (tránh 2 CQ khác nhau cùng số trong hồ sơ). Bản thật cấp bằng file tạo-không-ghi-đè."""
    n = max(existing_numbers, default=0) + 1
    if n > 999:
        raise ValueError('Hết số báo cáo trong tháng')
    return f'{n:03d}/{year_month[5:7]}', n
