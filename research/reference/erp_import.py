"""THAM CHIẾU import ERP MF0015 (.xlsx và HTML-xls Oracle BI) — xem trước + áp dụng không ghi đè dữ liệu QC.
Nhận cột theo TÊN tiêu đề. Khóa đơn = Số ĐNGC + Dòng."""
import re, datetime, html
from decimal import Decimal
import domain

# tên cột ERP → trường (chỉ các trường này thuộc ERP; mọi trường khác của đơn là của QC/app)
ERP_FIELDS = {'Số ĐNGC': 'Dngc', 'Ngày nhận ĐNGC': 'ReceivedAt', 'Dòng': 'LineNo', 'Số đơn hàng': 'SalesOrderNo',
              'Khách hàng': 'Customer', 'Sản phẩm': 'ProductCode', 'Tên sản phẩm': 'ProductName',
              'Độ cứng': 'HardnessRequirementErp', 'Số lượng': 'Quantity', 'Trọng lượng': 'WeightKg',
              'Phương thức gia công': 'ProcessMethod', 'Công đoạn': 'Stage'}


def _norm_dt(v):
    if isinstance(v, datetime.datetime): return v.strftime('%Y-%m-%d %H:%M:%S')
    if isinstance(v, str):
        m = re.match(r"'?(\d{2})/(\d{2})/(\d{4})(?: (\d{2}):(\d{2}):(\d{2}))?", v.strip())
        if m:
            d, mo, y, H, M, S = m.groups()
            return f'{y}-{mo}-{d} {H or "00"}:{M or "00"}:{S or "00"}'
    return None


def _num(v):
    if v is None or (isinstance(v, str) and v.strip() in ('', "'")): return None
    try: return Decimal(str(v).strip().replace(',', ''))
    except Exception: return 'ERR'


def _rows_xlsx(path):
    import openpyxl, warnings
    warnings.filterwarnings('ignore')
    ws = openpyxl.load_workbook(path, data_only=True).worksheets[0]
    rows = list(ws.iter_rows(values_only=True))
    return rows


def _rows_html(path):
    txt = open(path, encoding='utf-8', errors='replace').read()
    out = []
    for tr in re.findall(r'<tr[^>]*>(.*?)</tr>', txt, re.S | re.I):
        cells = []
        for m in re.finditer(r'<t([dh])([^>]*)>(.*?)</t\1>', tr, re.S | re.I):
            span = re.search(r'colspan="?(\d+)', m.group(2))
            val = html.unescape(re.sub(r'<[^>]+>', '', m.group(3))).replace('\xa0', ' ').strip()
            cells.append(val if val != '' else None)
            cells += [None] * (int(span.group(1)) - 1 if span else 0)
        out.append(tuple(cells))
    return out


def read(path):
    head = open(path, 'rb').read(512).lower()
    rows = _rows_html(path) if b'<html' in head or b'<table' in head else _rows_xlsx(path)
    # dòng tiêu đề: dòng có cả 'Số ĐNGC' và 'Dòng'
    hi = next((i for i, r in enumerate(rows) if r and 'Số ĐNGC' in [str(c).strip() if c else '' for c in r]
               and 'Dòng' in [str(c).strip() if c else '' for c in r]), None)
    if hi is None:
        raise ValueError('Không phải báo cáo MF0015 (không thấy cột Số ĐNGC/Dòng)')
    header = [str(c).strip() if c else '' for c in rows[hi]]
    idx = {}
    for name in ERP_FIELDS:
        if name in header: idx[name] = header.index(name)        # lấy lần xuất hiện ĐẦU (cột 'Ghi chú' có 2 lần)
    missing = [n for n in ('Số ĐNGC', 'Dòng', 'Khách hàng', 'Sản phẩm', 'Độ cứng', 'Số lượng', 'Trọng lượng') if n not in idx]
    if missing:
        raise ValueError(f'Thiếu cột bắt buộc: {missing}')
    recs, rejects = [], []
    for rn, r in enumerate(rows[hi + 1:], start=hi + 2):
        get = lambda n: r[idx[n]] if n in idx and idx[n] < len(r) else None
        dngc = get('Số ĐNGC')
        if not dngc or not str(dngc).startswith('DNGC'):
            continue                                              # dòng tiêu đề phụ / trống
        line = _num(get('Dòng'))
        if line in (None, 'ERR'):
            rejects.append((rn, dngc, 'Dòng không hợp lệ')); continue
        rec = {'Dngc': str(dngc).strip(), 'LineNo': int(line), '_row': rn, '_warnings': []}
        for n, f in ERP_FIELDS.items():
            if f in ('Dngc', 'LineNo'): continue
            v = get(n)
            if f == 'ReceivedAt': v = _norm_dt(v)
            elif f in ('Quantity', 'WeightKg'):
                v = _num(v)
                if v == 'ERR': rec['_warnings'].append(f'{n} không phải số'); v = None
            elif f == 'HardnessRequirementErp':
                if isinstance(v, datetime.datetime):              # Excel đã tự đổi '4-7' thành ngày → không đoán ngược
                    rec['_warnings'].append(f'Độ cứng bị Excel đổi thành ngày ({v:%d/%m/%Y}) → nhập tay'); v = None
                elif v is not None:
                    v = str(v).strip()
                    if domain.parse_requirement(v) is None: rec['_warnings'].append(f'Độ cứng "{v}" không hợp lệ → nhập tay')
            elif v is not None:
                v = str(v).strip()
            rec[f] = v
        m = re.match(r'^[A-Z]+_(.+?)\s+SP$', rec.get('ProductCode') or '')
        rec['GradeFromErp'] = m.group(1).strip() if m else None
        if rec['GradeFromErp'] is None: rec['_warnings'].append('Không lấy được mác từ mã sản phẩm')
        recs.append(rec)
    keys = [(r['Dngc'], r['LineNo']) for r in recs]
    dup = {k for k in keys if keys.count(k) > 1}
    for r in recs:
        if (r['Dngc'], r['LineNo']) in dup:
            rejects.append((r['_row'], r['Dngc'], 'Trùng khóa ĐNGC+Dòng trong cùng file'))
    recs = [r for r in recs if (r['Dngc'], r['LineNo']) not in dup]
    return recs, rejects


ERP_OWNED = [f for f in ERP_FIELDS.values() if f not in ('Dngc', 'LineNo')] + ['GradeFromErp']


def preview(store, recs):
    """store: dict key → order. Trả kế hoạch: thêm / cập nhật (trường đổi) / không đổi."""
    plan = {'add': [], 'update': [], 'same': []}
    for r in recs:
        k = (r['Dngc'], r['LineNo']); cur = store.get(k)
        if cur is None: plan['add'].append(r); continue
        changes = {f: (cur['erp'].get(f), r.get(f)) for f in ERP_OWNED if cur['erp'].get(f) != r.get(f)}
        (plan['update'] if changes else plan['same']).append((r, changes))
    return plan


def apply(store, plan):
    """Chỉ ghi phần 'erp' của đơn. Phần 'qc' (mác chọn, template, yêu cầu độ cứng đã sửa, Mã QL, ảnh, đo, CQ) KHÔNG đụng.
    Đơn đã có CQ: gắn cờ cảnh báo 'ERP thay đổi sau CQ' (CQ cũ không đổi)."""
    for r in plan['add']:
        store[(r['Dngc'], r['LineNo'])] = {'erp': {f: r.get(f) for f in ERP_OWNED}, 'qc': {}, 'flags': []}
    for r, ch in plan['update']:
        o = store[(r['Dngc'], r['LineNo'])]
        for f, (_, new) in ch.items(): o['erp'][f] = new
        if o['qc'].get('cqVersions'): o['flags'].append('ERP thay đổi sau CQ: ' + ', '.join(ch))
