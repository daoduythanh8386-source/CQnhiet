"""Tách MỘT sheet mẫu CQ từ sổ CQ-2026.xlsx thành file template độc lập (giữ nguyên style, hình vẽ, logo, khổ in).
- Xoá công thức (VLOOKUP sang sheet khác) và giá trị của đơn cũ trong các ô dữ liệu; giữ nhãn.
- Chỉ giữ vùng in A1:U37; xoá hình nằm ngoài vùng in (cột > U) và ảnh của đơn cũ (theo tên).
- sharedStrings chỉ còn chuỗi của sheet này (không mang theo dữ liệu khách hàng của sổ theo dõi).
Dùng: python -I extract_template.py <CQ-2026.xlsx> "<tên sheet>" <out.xlsx> [--drop "Picture 27,Picture 28"] [--clear "G6,G7"] [--set "A26=1. Chiều sâu lớp thấm: "]"""
import sys, zipfile, re, copy, argparse
from lxml import etree

NS = {'m': 'http://schemas.openxmlformats.org/spreadsheetml/2006/main',
      'r': 'http://schemas.openxmlformats.org/officeDocument/2006/relationships',
      'xdr': 'http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing',
      'a': 'http://schemas.openxmlformats.org/drawingml/2006/main',
      'rel': 'http://schemas.openxmlformats.org/package/2006/relationships'}
M = '{%s}' % NS['m']; RNS = '{%s}' % NS['r']; XDR = '{%s}' % NS['xdr']; REL = '{%s}' % NS['rel']

def col2n(c):
    n = 0
    for ch in c: n = n * 26 + ord(ch) - 64
    return n

def split(ref):
    m = re.match(r'([A-Z]+)(\d+)$', ref); return col2n(m.group(1)), int(m.group(2))

def rels_of(z, part):
    d, f = part.rsplit('/', 1); rp = f'{d}/_rels/{f}.rels'
    if rp not in z.namelist(): return {}
    return {r.get('Id'): (r.get('Type'), r.get('Target'), r.get('TargetMode')) for r in etree.fromstring(z.read(rp))}

def resolve(base_part, target):
    parts = base_part.rsplit('/', 1)[0].split('/')
    for seg in target.split('/'):
        if seg == '..': parts.pop()
        elif seg != '.': parts.append(seg)
    return '/'.join(parts)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('src'); ap.add_argument('sheet'); ap.add_argument('out')
    ap.add_argument('--drop', default=''); ap.add_argument('--clear', default='')
    ap.add_argument('--set', action='append', default=[])
    ap.add_argument('--a4', action='store_true', help='chuẩn hoá in: A4 dọc, vừa 1 trang, căn giữa ngang, lề như mẫu khuôn; bỏ printerSettings')
    ap.add_argument('--maxcol', type=int, default=21); ap.add_argument('--maxrow', type=int, default=37)
    a = ap.parse_args()
    z = zipfile.ZipFile(a.src)
    wb = etree.fromstring(z.read('xl/workbook.xml'))
    wbrels = rels_of(z, 'xl/workbook.xml')
    sh = [s for s in wb.find('m:sheets', NS) if s.get('name') == a.sheet]
    if not sh: sys.exit('không có sheet ' + a.sheet)
    sheet_part = resolve('xl/workbook.xml', wbrels[sh[0].get(RNS + 'id')][1])
    sheet = etree.fromstring(z.read(sheet_part))

    # ---- chuỗi dùng chung cũ
    sst_old = []
    if 'xl/sharedStrings.xml' in z.namelist():
        sst_old = list(etree.fromstring(z.read('xl/sharedStrings.xml')))
    sst_new, sst_idx = [], {}
    clear = set(x.strip() for x in a.clear.split(',') if x.strip())
    sets = dict(s.split('=', 1) for s in a.set)

    sd = sheet.find('m:sheetData', NS)
    for row in sd.findall('m:row', NS):
        for c in row.findall('m:c', NS):
            col, r = split(c.get('r'))
            for att in ('cm', 'vm'):
                if att in c.attrib: del c.attrib[att]
            f = c.find('m:f', NS)
            outside = col > a.maxcol or r > a.maxrow
            if f is not None or outside or c.get('r') in clear or c.get('t') == 'e':
                for ch in list(c): c.remove(ch)
                for att in list(c.attrib):
                    if att not in ('r', 's'): del c.attrib[att]
                continue
            if c.get('t') == 's':
                v = c.find('m:v', NS); i = int(v.text)
                si = sst_old[i]
                key = etree.tostring(si)
                if key not in sst_idx: sst_idx[key] = len(sst_new); sst_new.append(copy.deepcopy(si))
                v.text = str(sst_idx[key])
        # ô đặt sẵn chữ
    for ref, text in sets.items():
        col, r = split(ref)
        row = next((x for x in sd.findall('m:row', NS) if int(x.get('r')) == r), None)
        c = next((x for x in row.findall('m:c', NS) if x.get('r') == ref), None) if row is not None else None
        if c is None: sys.exit('không có ô ' + ref)
        for ch in list(c): c.remove(ch)
        for att in list(c.attrib):
            if att not in ('r', 's'): del c.attrib[att]
        c.set('t', 'inlineStr'); is_ = etree.SubElement(c, M + 'is'); t = etree.SubElement(is_, M + 't'); t.text = text
        t.set('{http://www.w3.org/XML/1998/namespace}space', 'preserve')
    # bỏ phần tham chiếu sang sheet khác / không cần
    for tag in ('conditionalFormatting', 'dataValidations', 'extLst', 'legacyDrawing', 'legacyDrawingHF', 'hyperlinks', 'tableParts', 'controls', 'oleObjects', 'picture'):
        for e in sheet.findall('m:' + tag, NS): sheet.remove(e)
    for e in sheet.iter(M + 'sheetView'):
        e.set('tabSelected', '1')

    if a.a4:
        sp = sheet.find('m:sheetPr', NS)
        if sp is None: sp = etree.Element(M + 'sheetPr'); sheet.insert(0, sp)
        pp = sp.find('m:pageSetUpPr', NS)
        if pp is None: pp = etree.SubElement(sp, M + 'pageSetUpPr')
        pp.set('fitToPage', '1')
        ps = sheet.find('m:pageSetup', NS)
        for k in list(ps.attrib):
            if k == RNS + 'id' or k == 'scale': del ps.attrib[k]
        ps.set('paperSize', '9'); ps.set('orientation', 'portrait'); ps.set('fitToWidth', '1'); ps.set('fitToHeight', '1')
        pm = sheet.find('m:pageMargins', NS)
        for k, v in dict(left='0.3', right='0.3', top='0.5', bottom='0.3', header='0.1', footer='0.1').items(): pm.set(k, v)
        po = sheet.find('m:printOptions', NS)
        if po is None: po = etree.Element(M + 'printOptions'); pm.addprevious(po)
        po.set('horizontalCentered', '1')
    # ---- quan hệ của sheet: chỉ giữ drawing + printerSettings
    srels = rels_of(z, sheet_part)
    out = {}
    new_srels = []
    drawing_part = None
    for rid, (typ, tgt, mode) in srels.items():
        kind = typ.rsplit('/', 1)[1]
        if kind == 'drawing':
            drawing_part = resolve(sheet_part, tgt); new_srels.append((rid, typ, '../drawings/drawing1.xml'))
        elif kind == 'printerSettings' and not a.a4:
            out['xl/printerSettings/printerSettings1.bin'] = z.read(resolve(sheet_part, tgt)); new_srels.append((rid, typ, '../printerSettings/printerSettings1.bin'))
    used_rids = set(new_srels and [r[0] for r in new_srels])
    for e in list(sheet.iter()):
        rid = e.get(RNS + 'id')
        if rid is not None and rid not in used_rids: e.getparent().remove(e)
    out['xl/worksheets/sheet1.xml'] = etree.tostring(sheet, xml_declaration=True, encoding='UTF-8', standalone=True)
    out['xl/worksheets/_rels/sheet1.xml.rels'] = rels_xml(new_srels)

    media_ct = {}
    if drawing_part:
        d = etree.fromstring(z.read(drawing_part))
        drop = set(x.strip() for x in a.drop.split(',') if x.strip())
        for anc in list(d):
            fr = anc.find('xdr:from', NS)
            names = [e.get('name') for e in anc.iter() if e.tag.endswith('}cNvPr')]
            if (fr is not None and int(fr.find('xdr:col', NS).text) >= a.maxcol) or (names and names[0] in drop):
                d.remove(anc)
        drels = rels_of(z, drawing_part)
        used = {v for e in d.iter() for k, v in e.attrib.items() if k.startswith(RNS)}
        nr = []
        for rid, (typ, tgt, mode) in drels.items():
            if rid not in used: continue
            src = resolve(drawing_part, tgt); name = src.rsplit('/', 1)[1]
            out['xl/media/' + name] = z.read(src); nr.append((rid, typ, '../media/' + name))
            media_ct[name.rsplit('.', 1)[1].lower()] = True
        out['xl/drawings/drawing1.xml'] = etree.tostring(d, xml_declaration=True, encoding='UTF-8', standalone=True)
        out['xl/drawings/_rels/drawing1.xml.rels'] = rels_xml(nr)

    # ---- chuỗi dùng chung mới
    sst = etree.Element(M + 'sst', nsmap={None: NS['m']}); sst.set('count', str(len(sst_new))); sst.set('uniqueCount', str(len(sst_new)))
    for si in sst_new: sst.append(si)
    out['xl/sharedStrings.xml'] = etree.tostring(sst, xml_declaration=True, encoding='UTF-8', standalone=True)
    out['xl/styles.xml'] = z.read('xl/styles.xml')
    theme = resolve('xl/workbook.xml', next(t for (ty, t, m) in wbrels.values() if ty.endswith('/theme')))
    out['xl/theme/theme1.xml'] = z.read(theme)
    qn = a.sheet.replace("'", "''")
    out['xl/workbook.xml'] = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n'
        f'<workbook xmlns="{NS["m"]}" xmlns:r="{NS["r"]}"><workbookPr/><bookViews><workbookView xWindow="0" yWindow="0" windowWidth="28800" windowHeight="15000"/></bookViews>'
        f'<sheets><sheet name="{esc(a.sheet)}" sheetId="1" r:id="rId1"/></sheets>'
        f'<definedNames><definedName name="_xlnm.Print_Area" localSheetId="0">{esc(chr(39) + qn + chr(39))}!$A$1:${n2col(a.maxcol)}${a.maxrow}</definedName></definedNames>'
        '<calcPr calcId="191029"/></workbook>').encode('utf-8')
    base = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships/'
    out['xl/_rels/workbook.xml.rels'] = rels_xml([('rId1', base + 'worksheet', 'worksheets/sheet1.xml'), ('rId2', base + 'theme', 'theme/theme1.xml'),
                                                  ('rId3', base + 'styles', 'styles.xml'), ('rId4', base + 'sharedStrings', 'sharedStrings.xml')])
    out['_rels/.rels'] = rels_xml([('rId1', base + 'officeDocument', 'xl/workbook.xml')])
    ct = {'png': 'image/png', 'jpeg': 'image/jpeg', 'jpg': 'image/jpeg', 'wdp': 'image/vnd.ms-photo', 'emf': 'image/x-emf', 'wmf': 'image/x-wmf', 'gif': 'image/gif'}
    defaults = ''.join(f'<Default Extension="{e}" ContentType="{ct[e]}"/>' for e in media_ct if e in ct)
    ov = [('/xl/workbook.xml', 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml'),
          ('/xl/worksheets/sheet1.xml', 'application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml'),
          ('/xl/theme/theme1.xml', 'application/vnd.openxmlformats-officedocument.theme+xml'),
          ('/xl/styles.xml', 'application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml'),
          ('/xl/sharedStrings.xml', 'application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml')]
    if drawing_part: ov.append(('/xl/drawings/drawing1.xml', 'application/vnd.openxmlformats-officedocument.drawing+xml'))
    out['[Content_Types].xml'] = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
        '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>'
        '<Default Extension="bin" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.printerSettings"/>' + defaults
        + ''.join(f'<Override PartName="{p}" ContentType="{c}"/>' for p, c in ov) + '</Types>').encode('utf-8')
    order = ['[Content_Types].xml', '_rels/.rels', 'xl/workbook.xml', 'xl/_rels/workbook.xml.rels']
    with zipfile.ZipFile(a.out, 'w', zipfile.ZIP_DEFLATED) as o:
        for k in order + sorted(k for k in out if k not in order):
            zi = zipfile.ZipInfo(k, date_time=(2026, 11, 16, 0, 0, 0)); zi.compress_type = zipfile.ZIP_DEFLATED
            o.writestr(zi, out[k])
    print('OK', a.out, len(out), 'parts', len(sst_new), 'strings')

def n2col(n):
    s = ''
    while n: n, r = divmod(n - 1, 26); s = chr(65 + r) + s
    return s

def esc(s): return s.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;').replace('"', '&quot;')

def rels_xml(items):
    x = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
    for rid, typ, tgt in items: x += f'<Relationship Id="{rid}" Type="{typ}" Target="{tgt}"/>'
    return (x + '</Relationships>').encode('utf-8')

if __name__ == '__main__': main()
