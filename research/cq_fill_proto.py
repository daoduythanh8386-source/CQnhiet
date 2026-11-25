"""Nguyên mẫu (tham chiếu thuật toán cho bản .NET): điền CQ bằng cách sửa trực tiếp XML,
giữ nguyên mọi part khác của template. Dùng: python cq_fill_proto.py <root> <templateId> <data.json> <out.xlsx>"""
import sys, json, zipfile, re, io, struct
from lxml import etree
NS = {'m': 'http://schemas.openxmlformats.org/spreadsheetml/2006/main',
      'xdr': 'http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing',
      'a': 'http://schemas.openxmlformats.org/drawingml/2006/main',
      'r': 'http://schemas.openxmlformats.org/officeDocument/2006/relationships',
      'rel': 'http://schemas.openxmlformats.org/package/2006/relationships'}
M = '{%s}' % NS['m']

def col2n(c):
    n = 0
    for ch in c: n = n * 26 + ord(ch) - 64
    return n
def split(ref):
    m = re.match(r'([A-Z]+)(\d+)$', ref); return m.group(1), int(m.group(2))

def set_cell(sheet, ref, value, kind):
    col, row = split(ref)
    sd = sheet.find(M + 'sheetData')
    rows = {int(r.get('r')): r for r in sd.findall(M + 'row')}
    if row not in rows:
        new = etree.Element(M + 'row', r=str(row))
        after = [r for k, r in rows.items() if k < row]
        (after[-1].addnext(new) if after else sd.insert(0, new)); rows[row] = new
    r = rows[row]
    cells = {c.get('r'): c for c in r.findall(M + 'c')}
    c = cells.get(ref)
    if c is None:
        c = etree.Element(M + 'c', r=ref)
        before = [x for x in r.findall(M + 'c') if col2n(split(x.get('r'))[0]) < col2n(col)]
        (before[-1].addnext(c) if before else r.insert(0, c))
    style = c.get('s')
    for ch in list(c): c.remove(ch)
    for a in list(c.attrib):
        if a not in ('r', 's'): del c.attrib[a]
    if value is None or value == '':
        return
    if kind == 'number':
        v = etree.SubElement(c, M + 'v'); v.text = repr(float(value)).rstrip('0').rstrip('.') if isinstance(value, float) else str(value)
    else:
        c.set('t', 'inlineStr'); is_ = etree.SubElement(c, M + 'is'); t = etree.SubElement(is_, M + 't')
        t.text = str(value); t.set('{http://www.w3.org/XML/1998/namespace}space', 'preserve')

def png_size(b):
    assert b[:8] == b'\x89PNG\r\n\x1a\n'; return struct.unpack('>II', b[16:24])

MDW = 7   # bề rộng chữ số lớn nhất (px) của font mặc định (Arial Narrow 11) — bản .NET đo bằng GDI trên Windows (rủi ro R-EXCEL-1)

def col_widths_px(sheet):
    fmt = sheet.find(M + 'sheetFormatPr')
    base = float(fmt.get('defaultColWidth', fmt.get('baseColWidth', 8))) if fmt is not None else 8.43
    conv = lambda wd: int(((256 * wd + int(128 / MDW)) / 256) * MDW)
    w = {}
    for c in sheet.iter(M + 'col'):
        for i in range(int(c.get('min')), min(int(c.get('max')), 200) + 1): w[i] = conv(float(c.get('width')))
    return lambda i: w.get(i, conv(base))
def row_heights_pt(sheet):
    h = {int(r.get('r')): float(r.get('ht')) for r in sheet.iter(M + 'row') if r.get('ht')}
    hidden = {int(r.get('r')) for r in sheet.iter(M + 'row') if r.get('hidden') == '1'}
    fmt = sheet.find(M + 'sheetFormatPr')
    dflt = float(fmt.get('defaultRowHeight', 15)) if fmt is not None else 15.0   # gio: 27.75, carbon/khuon: 18.75
    return lambda i: 0 if i in hidden else h.get(i, dflt)

class Geo:
    """Toạ độ tuyệt đối (EMU) của lưới ô — để đặt ảnh/dấu ở BẤT KỲ vị trí nào trên biểu mẫu."""
    def __init__(self, sheet):
        self.cw, self.rh = col_widths_px(sheet), row_heights_pt(sheet)
    def col_x(self, c): return sum(self.cw(i) for i in range(1, c)) * 9525          # mép trái cột c (1-based)
    def row_y(self, r): return int(sum(self.rh(i) for i in range(1, r)) * 12700)   # mép trên dòng r
    def rect(self, rng):
        a, b = rng.split(':'); c1, r1 = split(a); c2, r2 = split(b); c1, c2 = col2n(c1), col2n(c2)
        x0, y0 = self.col_x(c1), self.row_y(r1)
        return x0, y0, self.col_x(c2 + 1) - x0, self.row_y(r2 + 1) - y0
    def to_cell(self, x, y):
        col = 1
        while x >= self.cw(col) * 9525: x -= self.cw(col) * 9525; col += 1
        row = 1
        while self.rh(row) == 0 or y >= self.rh(row) * 12700: y -= self.rh(row) * 12700; row += 1
        return col, int(x), row, int(y)


def img_size(b):
    if b[:8] == b'\x89PNG\r\n\x1a\n': return png_size(b)
    import cv2, numpy as np
    im = cv2.imdecode(np.frombuffer(b, np.uint8), cv2.IMREAD_UNCHANGED); return im.shape[1], im.shape[0]


def place(drawing, rels, ct, files, geo, img, name, idx, x, y, ew, eh):
    """Neo ảnh tại toạ độ tuyệt đối (x,y) EMU, kích thước ew×eh (oneCellAnchor, không phụ thuộc vùng gộp)."""
    ext = 'png' if img[:4] == b'\x89PNG' else 'jpeg'
    media = f'xl/media/cqnl_{idx}.{ext}'; files[media] = img
    rid = f'rIdCqnl{idx}'
    etree.SubElement(rels, '{%s}Relationship' % NS['rel'], Id=rid,
                     Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/image', Target=f'../media/cqnl_{idx}.{ext}')
    if not any(d.get('Extension') == ext for d in ct):
        etree.SubElement(ct, '{http://schemas.openxmlformats.org/package/2006/content-types}Default', Extension=ext, ContentType=f'image/{ext}')
    col, cx, row, cy = geo.to_cell(x, y)
    X = '{%s}' % NS['xdr']; A = '{%s}' % NS['a']
    anc = etree.SubElement(drawing, X + 'oneCellAnchor')
    fr = etree.SubElement(anc, X + 'from')
    for t, v in (('col', col - 1), ('colOff', cx), ('row', row - 1), ('rowOff', cy)): etree.SubElement(fr, X + t).text = str(int(v))
    etree.SubElement(anc, X + 'ext', cx=str(int(ew)), cy=str(int(eh)))
    pic = etree.SubElement(anc, X + 'pic'); nv = etree.SubElement(pic, X + 'nvPicPr')
    etree.SubElement(nv, X + 'cNvPr', id=str(1000 + idx), name=name); cp = etree.SubElement(nv, X + 'cNvPicPr')
    etree.SubElement(cp, A + 'picLocks', noChangeAspect='1')
    bf = etree.SubElement(pic, X + 'blipFill'); etree.SubElement(bf, A + 'blip').set('{%s}embed' % NS['r'], rid)
    st = etree.SubElement(bf, A + 'stretch'); etree.SubElement(st, A + 'fillRect')
    sp = etree.SubElement(pic, X + 'spPr'); xf = etree.SubElement(sp, A + 'xfrm')
    etree.SubElement(xf, A + 'off', x='0', y='0'); etree.SubElement(xf, A + 'ext', cx=str(int(ew)), cy=str(int(eh)))
    pg = etree.SubElement(sp, A + 'prstGeom', prst='rect'); etree.SubElement(pg, A + 'avLst')
    etree.SubElement(anc, X + 'clientData')


def dedup_hardness(items, d, min_count=3):
    """Quy tắc người dùng 13/11/2026: khi CQ có từ 3 ảnh độ cứng trở lên, các ảnh có KẾT QUẢ ĐỒNG HỒ TRÙNG NHAU
    của cùng một đơn (Số ĐNGC), cùng khách hàng, cùng mác chỉ đưa 1 ảnh vào CQ (giữ ảnh thêm vào trước).
    - ≤ 2 ảnh: giữ nguyên tất cả.
    - So sánh theo giá trị đã QC xác nhận, dạng số thập phân (47.3 = 47.30); ảnh chưa xác nhận không bao giờ bị gộp.
    - Chỉ bỏ khỏi CQ; ảnh vẫn nằm trong đơn. Danh sách ảnh bị bỏ trả về để hiển thị/ghi audit."""
    from decimal import Decimal
    d.setdefault('_warnings', [])
    if len(items) < min_count:
        return items
    seen, kept = {}, []
    for it in items:
        v = it.get('value')
        if v is None:
            kept.append(it); continue
        k = (it.get('dngc', d.get('Dngc')), (it.get('customer', d.get('Customer')) or '').strip().upper(),
             (it.get('grade', d.get('Material')) or '').strip().upper(), Decimal(str(v)).normalize())
        if k in seen:
            d['_warnings'].append(f"Bỏ ảnh trùng kết quả {v} ({it['path'].split('/')[-1]}) – đã có {seen[k]['path'].split('/')[-1]}")
            continue
        seen[k] = it; kept.append(it)
    return kept


def tile(img_bytes, no, value, unit, aspect, label_frac=0.13, W=900, show_index=False):
    """Thẻ ảnh cho CQ: mọi thẻ cùng kích thước và cùng tỉ lệ ô bảng; ảnh căn giữa giữ tỉ lệ;
    dải nhãn bên dưới cùng cỡ chữ: "1  |  38.2 HRC". Ảnh gốc không đổi (đây là bản sao cho CQ)."""
    import cv2, numpy as np
    im = cv2.imdecode(np.frombuffer(img_bytes, np.uint8), cv2.IMREAD_COLOR)
    H = int(round(W / aspect)); sh = int(H * label_frac); ah = H - sh
    canvas = np.full((H, W, 3), 255, np.uint8)
    s = min(W / im.shape[1], ah / im.shape[0]); iw, ih = int(im.shape[1] * s), int(im.shape[0] * s)
    canvas[(ah - ih) // 2:(ah - ih) // 2 + ih, (W - iw) // 2:(W - iw) // 2 + iw] = cv2.resize(im, (iw, ih), interpolation=cv2.INTER_AREA)
    # Mặc định chỉ ghi giá trị (tránh nhầm STT ảnh với STT mẫu trong bảng kết quả); bật show_index nếu muốn đánh số
    core = f'{value} {unit}'.rstrip() if value is not None else 'chua xac nhan'
    text = f'{no}  |  {core}' if show_index else core
    scale, th = sh / 40.0, max(2, sh // 18)
    (tw, tht), _ = cv2.getTextSize(text, cv2.FONT_HERSHEY_DUPLEX, scale, th)
    cv2.putText(canvas, text, ((W - tw) // 2, ah + (sh + tht) // 2), cv2.FONT_HERSHEY_DUPLEX, scale, (0, 0, 0), th, cv2.LINE_AA)
    ok, buf = cv2.imencode('.png', canvas); return buf.tobytes()


def annotate_corner(img_bytes, text, box_h_px):
    """Ghi chú số cân ở GÓC DƯỚI BÊN TRÁI bản sao ảnh xuất hàng: chữ đen đậm trên nền trắng, viền đen mảnh
    → in A4 đen trắng vẫn rõ. box_h_px = chiều cao ô chữ (tính từ cỡ in thực tế). Ảnh gốc không đổi."""
    import cv2, numpy as np
    im = cv2.imdecode(np.frombuffer(img_bytes, np.uint8), cv2.IMREAD_COLOR)
    h, w = im.shape[:2]; bh = int(max(14, box_h_px)); pad = max(2, bh // 6)
    scale = 1.0; th = max(1, bh // 9)
    (tw, tht), base = cv2.getTextSize(text, cv2.FONT_HERSHEY_DUPLEX, scale, th)
    scale = (bh - 2 * pad) / (tht + base) ; th = max(1, int(round(bh / 9)))
    (tw, tht), base = cv2.getTextSize(text, cv2.FONT_HERSHEY_DUPLEX, scale, th)
    m = max(3, int(0.015 * min(w, h)))                      # lề cách mép ảnh
    x0, y1 = m, h - m; x1, y0 = x0 + tw + 2 * pad, y1 - bh
    cv2.rectangle(im, (x0, y0), (x1, y1), (255, 255, 255), -1)
    cv2.rectangle(im, (x0, y0), (x1, y1), (0, 0, 0), max(1, bh // 14))
    cv2.putText(im, text, (x0 + pad, y1 - pad - base // 2), cv2.FONT_HERSHEY_DUPLEX, scale, (0, 0, 0), th, cv2.LINE_AA)
    ok, buf = cv2.imencode('.png', im); return buf.tobytes()


def table_layout(rects, sizes, pad=0.04, gap=0.04, cells=False):
    """Bảng ô đều nhau trải qua nhiều khung đặt cạnh nhau (cùng hàng). Thứ tự: hàng 1 từ trái qua phải
    (hết khung 1 sang khung 2), rồi hàng 2… Chọn số cột/khung để ảnh to nhất. Trả về [(x,y,w,h)] theo thứ tự ảnh."""
    n = len(sizes)
    if n == 0: return []
    F = len(rects); best = None
    for cpf in range(1, n + 1):                       # số cột mỗi khung
        rows = -(-n // (cpf * F))
        cw = min((W * (1 - 2 * pad) - (cpf - 1) * W * gap) / cpf for _, _, W, _ in rects)
        ch = min((H * (1 - 2 * pad) - (rows - 1) * H * gap) / rows for _, _, _, H in rects)
        sc = min(min(cw / iw, ch / ih) for iw, ih in sizes)
        if best is None or sc > best[0] + 1e-9: best = (sc, cpf, rows, cw, ch)
    _, cpf, rows, cw, ch = best
    out = []
    for i, (iw, ih) in enumerate(sizes):
        r, c = divmod(i, cpf * F); fi, cc = divmod(c, cpf)
        X, Y, W, H = rects[fi]
        gx, gy = W * gap, H * gap
        bx = X + (W - (cpf * cw + (cpf - 1) * gx)) / 2 + cc * (cw + gx)       # ô bảng căn giữa trong khung
        by = Y + (H - (rows * ch + (rows - 1) * gy)) / 2 + r * (ch + gy)
        if cells: out.append((bx, by, cw, ch)); continue
        s = min(cw / iw, ch / ih); w, h = iw * s, ih * s                        # mọi ảnh cùng cỡ ô, giữ tỉ lệ
        out.append((bx + (cw - w) / 2, by + (ch - h) / 2, w, h))
    return out


def grid_layout(rect, sizes, pad=0.04, gap=0.03):
    """Xếp n ảnh vào 1 khung: thử mọi số cột, chọn cách cho ảnh nhỏ nhất to nhất. Trả về [(x,y,w,h)]."""
    X, Y, W, H = rect; n = len(sizes)
    if n == 0: return []
    px, py = W * pad, H * pad; W2, H2 = W - 2 * px, H - 2 * py
    best = None
    for cols in range(1, n + 1):
        rows = -(-n // cols)
        cw = (W2 - (cols - 1) * W * gap) / cols; ch = (H2 - (rows - 1) * H * gap) / rows
        scales = [min(cw / iw, ch / ih) for iw, ih in sizes]
        if best is None or min(scales) > best[0]: best = (min(scales), cols, rows, cw, ch)
    _, cols, rows, cw, ch = best
    out = []
    for i, (iw, ih) in enumerate(sizes):
        r, c = divmod(i, cols); s = min(cw / iw, ch / ih); w, h = iw * s, ih * s
        inrow = min(cols, n - r * cols)                       # căn giữa hàng cuối nếu thiếu ảnh
        offx = (W2 - inrow * cw - (inrow - 1) * W * gap) / 2
        x = X + px + offx + c * (cw + W * gap) + (cw - w) / 2
        y = Y + py + (H2 - rows * ch - (rows - 1) * H * gap) / 2 + r * (ch + H * gap) + (ch - h) / 2
        out.append((x, y, w, h))
    return out


def main(root, tid, data_path, out):
    m = json.load(open(f'{root}/config/template-maps/{tid}.json')); d = json.load(open(data_path))
    zin = zipfile.ZipFile(f"{root}/sources/templates/{m['fileName']}")
    files = {i.filename: zin.read(i.filename) for i in zin.infolist()}; order = [i.filename for i in zin.infolist()]
    sheet = etree.fromstring(files['xl/worksheets/sheet1.xml'])
    f = m['fields']
    for key, spec in f.items():
        if 'cell' in spec and key in d:
            set_cell(sheet, spec['cell'], d[key], spec.get('type', 'text'))
            if spec.get('unhideRowWhenFilled') and d[key]:
                for r in sheet.iter(M + 'row'):
                    if r.get('r') == str(spec['unhideRowWhenFilled']) and 'hidden' in r.attrib: del r.attrib['hidden']
    for i, v in enumerate(d.get('Samples', [])): set_cell(sheet, f['Samples']['cells'][i], v, 'text')
    drawing = etree.fromstring(files['xl/drawings/drawing1.xml'])
    rels = etree.fromstring(files['xl/drawings/_rels/drawing1.xml.rels'])
    ct = etree.fromstring(files['[Content_Types].xml'])
    # Nhiệt độ tôi trong textbox hình vẽ (khuon/gio) — thay theo thứ tự xuất hiện, chỉ khi văn bản khớp chính xác bản gốc
    texts = f.get('DrawingTemperatures', {}).get('texts', [])
    if texts and d.get('QuenchTemps'):
        ts = [t for t in drawing.iter('{%s}t' % NS['a']) if t.text in texts]
        if [t.text for t in ts] != texts: raise SystemExit('Hình vẽ nhiệt độ không khớp template map: %r' % [t.text for t in ts])
        if len(d['QuenchTemps']) != len(texts) or not all(d['QuenchTemps']):
            raise SystemExit(f'Mác có {len(d["QuenchTemps"])} giai đoạn tôi, template cần {len(texts)} → không tạo CQ (QC kiểm tra mác/template)')
        for t, v in zip(ts, d['QuenchTemps']): t.text = v
    if 'QuenchTemp1' in f and d.get('QuenchTemps') and (len(d['QuenchTemps']) != 2 or not all(d['QuenchTemps'])):
        raise SystemExit(f'Mác có {len(d["QuenchTemps"])} giai đoạn tôi, template carbon cần 2 → không tạo CQ')
    for i, key in enumerate(('QuenchTemp1', 'QuenchTemp2')):
        if key in f and d.get('QuenchTemps') and len(d['QuenchTemps']) > i and d['QuenchTemps'][i]:
            set_cell(sheet, f[key]['cell'], d['QuenchTemps'][i], 'text')
    # Nhiệt độ/thời gian RAM theo dữ liệu đầu vào: textbox hình vẽ (carbon/khuon) hoặc ô (gio)
    rtexts = f.get('DrawingRamTexts', {}).get('texts', [])
    if rtexts and d.get('RamValues'):
        ts = [t for t in drawing.iter('{%s}t' % NS['a']) if t.text in rtexts]
        if [t.text for t in ts] != rtexts: raise SystemExit('Hình vẽ RAM không khớp template map: %r' % [t.text for t in ts])
        if len(d['RamValues']) != len(rtexts) or not all(d['RamValues']):
            raise SystemExit('Số giai đoạn RAM không khớp template → không tạo CQ')
        for t, v in zip(ts, d['RamValues']): t.text = v
    if 'RamCells' in f and d.get('RamValues'):
        for c, v in zip(f['RamCells']['cells'], d['RamValues']):
            if v: set_cell(sheet, c, v, 'text')
    geo = Geo(sheet); idx = 0
    # Ảnh vào khung. Ảnh độ cứng: SẮP XẾP TĂNG DẦN theo giá trị đo đã xác nhận, xếp thành BẢNG Ô đều nhau,
    # đọc lần lượt trái → phải, trên → dưới qua cả 2 khung; mỗi ảnh có nhãn "số thứ tự · giá trị" bên dưới.
    for frame, key in (('SampleImageFrame', 'SampleImages'), ('HardnessImageFrames', 'HardnessImages')):
        rngs = [f[frame]['range']] if 'range' in f[frame] else f[frame]['ranges']
        items = [it if isinstance(it, dict) else {'path': it} for it in (d.get(key) or [])]
        limit = f[frame].get('maxImages', 8 if key == 'HardnessImages' else 4)
        if len(items) > limit: raise SystemExit(f'{key}: {len(items)} ảnh vượt giới hạn {limit} của khung')
        if key == 'HardnessImages':
            items = dedup_hardness(items, d)
            # ảnh chưa có giá trị xác nhận xếp cuối; bằng nhau giữ thứ tự thêm ảnh (sắp xếp ổn định)
            items.sort(key=lambda it: (it.get('value') is None, float(it['value']) if it.get('value') is not None else 0))
            raw = [open(it['path'], 'rb').read() for it in items]
            rects = [geo.rect(r) for r in rngs]
            # chọn bố cục theo ảnh thật, sau đó mọi thẻ dựng đúng cỡ ô bảng → các ô đều nhau, dễ nhìn
            boxes = table_layout(rects, [img_size(b) for b in raw], cells=True)
            unit = d.get('HardnessUnit', '').strip('() ')
            datas = [tile(b, i + 1, it.get('value'), unit, bw / bh, show_index=bool(d.get('HardnessImageShowIndex'))) for i, (b, it, (_, _, bw, bh)) in enumerate(zip(raw, items, boxes))]
        else:
            datas = [open(it['path'], 'rb').read() for it in items]
            boxes = table_layout([geo.rect(r) for r in rngs], [img_size(b) for b in datas])
            # Ảnh xuất hàng: ghi số cân (đã QC xác nhận, đọc từ ẢNH GỐC) ở góc dưới trái, cao ≈ label_mm khi in
            ps = sheet.find(M + 'pageSetup'); pscale = int(ps.get('scale', '100')) / 100 if ps is not None else 1.0
            for i, it in enumerate(items):
                if it.get('scaleValue'):
                    _, _, bw, bh = boxes[i]; iw, ih = img_size(datas[i])
                    printed_mm = bh / 36000 * pscale                     # chiều cao ảnh khi in ra giấy
                    box_px = ih * d.get('ScaleLabelMm', 4.0) / printed_mm
                    datas[i] = annotate_corner(datas[i], str(it['scaleValue']), box_px)
        for dat, (x, y, w, h) in zip(datas, boxes):
            idx += 1; place(drawing, rels, ct, files, geo, dat, key, idx, x, y, w, h)
    # Dấu & chữ ký: vị trí tự do (ô neo + lệch mm + rộng mm) — mặc định theo khung, người làm chỉnh được
    overlays = d.get('Overlays')
    if overlays is None:   # mặc định: chữ ký giữa khung, dấu đè góc trái khung Xác nhận
        overlays = []
        for frame, key in (('ApproverSignatureFrame', 'ApproverSignature'), ('CheckerSignatureFrame', 'CheckerSignature'), ('ApproverSignatureFrame', 'Stamp')):
            if d.get(key): overlays.append({'image': d[key], 'frame': f[frame]['range'], 'name': key})
    for ov in overlays:
        dat = open(ov['image'], 'rb').read(); iw, ih = img_size(dat)
        if 'frame' in ov:
            (x, y, w, h), = grid_layout(geo.rect(ov['frame']), [(iw, ih)], pad=0.06)
        else:
            c, r = split(ov['cell']); x = geo.col_x(col2n(c)) + ov.get('dx_mm', 0) * 36000; y = geo.row_y(r) + ov.get('dy_mm', 0) * 36000
            w = ov['width_mm'] * 36000; h = w * ih / iw
        idx += 1; place(drawing, rels, ct, files, geo, dat, ov.get('name', 'Overlay'), idx, x, y, w, h)
    for w in d.get('_warnings', []): print('CẢNH BÁO:', w)
    files['xl/worksheets/sheet1.xml'] = etree.tostring(sheet, xml_declaration=True, encoding='UTF-8', standalone=True)
    files['xl/drawings/drawing1.xml'] = etree.tostring(drawing, xml_declaration=True, encoding='UTF-8', standalone=True)
    files['xl/drawings/_rels/drawing1.xml.rels'] = etree.tostring(rels, xml_declaration=True, encoding='UTF-8', standalone=True)
    files['[Content_Types].xml'] = etree.tostring(ct, xml_declaration=True, encoding='UTF-8', standalone=True)
    with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
        for n in order + [n for n in files if n not in order]: z.writestr(n, files[n])

if __name__ == '__main__': main(*sys.argv[1:5])
