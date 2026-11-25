"""So sánh template gốc và CQ sinh ra: mọi part phải giống hệt, trừ các part được phép; trong sheet1.xml chỉ các ô đã map/thuộc tính hidden được đổi; drawing chỉ được THÊM anchor."""
import sys,zipfile,json,re
from lxml import etree
M='{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'
tpl,out,mapf=sys.argv[1:4]
a=zipfile.ZipFile(tpl); b=zipfile.ZipFile(out); m=json.load(open(mapf))
allowed={'xl/worksheets/sheet1.xml','xl/drawings/drawing1.xml','xl/drawings/_rels/drawing1.xml.rels','[Content_Types].xml'}
errs=[]
for n in a.namelist():
    if n not in b.namelist(): errs.append('MẤT part '+n); continue
    if n not in allowed and a.read(n)!=b.read(n): errs.append('ĐỔI part '+n)
mapped=set()
for f in m['fields'].values():
    if 'cell' in f: mapped.add(f['cell'])
    mapped.update(f.get('cells',[]))
sa=etree.fromstring(a.read('xl/worksheets/sheet1.xml')); sb=etree.fromstring(b.read('xl/worksheets/sheet1.xml'))
ca={c.get('r'):etree.tostring(c) for c in sa.iter(M+'c')}; cb={c.get('r'):etree.tostring(c) for c in sb.iter(M+'c')}
for r in set(ca)|set(cb):
    if ca.get(r)!=cb.get(r) and r not in mapped: errs.append('ô ngoài mapping bị đổi: '+r)
# style index giữ nguyên
for c in sb.iter(M+'c'):
    r=c.get('r')
    if r in ca:
        sa_=etree.fromstring(ca[r]).get('s')
        if sa_!=c.get('s'): errs.append('style đổi ở '+r)
# ngoài sheetData giống hệt (merge, pageSetup, printOptions, headerFooter, conditionalFormatting...)
def strip(x):
    x=etree.fromstring(etree.tostring(x)); x.remove(x.find(M+'sheetData')); return etree.tostring(x)
if strip(sa)!=strip(sb): errs.append('phần ngoài sheetData bị đổi')
ra={r.get('r'):dict(r.attrib) for r in sa.iter(M+'row')}; rb={r.get('r'):dict(r.attrib) for r in sb.iter(M+'row')}
for k in ra:
    da=dict(ra[k]); db=dict(rb.get(k,{})); da.pop('hidden',None); db.pop('hidden',None)
    if da!=db: errs.append('thuộc tính dòng đổi '+k)
da=etree.fromstring(a.read('xl/drawings/drawing1.xml')); db=etree.fromstring(b.read('xl/drawings/drawing1.xml'))
la=[etree.tostring(x) for x in da]; lb=[etree.tostring(x) for x in db]
A='{http://schemas.openxmlformats.org/drawingml/2006/main}t'
def notext(x):
    x=etree.fromstring(x)
    for t in x.iter(A): t.text=''
    return etree.tostring(x)
if [notext(x) for x in lb[:len(la)]]!=[notext(x) for x in la]: errs.append('anchor gốc trong drawing bị đổi (ngoài văn bản)')
print(f'{out}: thêm {len(lb)-len(la)} ảnh; ' + ('ĐẠT' if not errs else 'LỖI:\n  '+'\n  '.join(errs)))
sys.exit(1 if errs else 0)
