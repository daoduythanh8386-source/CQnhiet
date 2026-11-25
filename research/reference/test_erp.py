import sys, copy, collections; sys.path.insert(0, 'research/reference')
import erp_import as E
fails = 0
def check(c, m):
    global fails; print(('ĐẠT  ' if c else 'LỖI  ') + m); fails += (not c)
X, Hh = 'sources/samples/erp.xlsx', 'sources/samples/erp_html.xls'
rx, jx = E.read(X); rh, jh = E.read(Hh)
check(len(rx) == 484 and not jx, f'xlsx: 484 dòng, 0 từ chối (đọc {len(rx)}, từ chối {len(jx)})')
check(len(rh) == 17 and not jh, f'HTML: 17 dòng (đọc {len(rh)})')
w = collections.Counter(x.split(' ')[0] for r in rx for x in r['_warnings'])
print('   cảnh báo xlsx:', dict(w))
check(sum(1 for r in rx if any('Excel đổi thành ngày' in x for x in r['_warnings'])) == 1, 'ô độ cứng bị Excel đổi thành ngày → cảnh báo, không đoán')
check(sum(1 for r in rx if r['HardnessRequirementErp'] is None) == 92, 'độ cứng trống (91) + ô ngày (1) = 92')
store = {}
p = E.preview(store, rx); check(len(p['add']) == 484, 'lần 1: thêm 484'); E.apply(store, p)
p = E.preview(store, rx); check(len(p['same']) == 484 and not p['add'] and not p['update'], 'nhập lại cùng file: 484 không đổi (idempotent)')
# QC đã làm việc trên 1 đơn chồng lấn
k = ('DNGC/HY3-26100206', 228642)
store[k]['qc'] = {'SteelGrade': 'SKD61', 'HardnessRequirement': '46-48', 'PartNo': 'MQ-01', 'images': ['a.png'], 'samples': ['47.1'], 'cqVersions': [1]}
qc_before = copy.deepcopy(store[k]['qc'])
p = E.preview(store, rh)
keys_h = {(r['Dngc'], r['LineNo']) for r in rh}; overlap = keys_h & set(store)
print(f'   HTML: thêm {len(p["add"])}, cập nhật {len(p["update"])}, không đổi {len(p["same"])}; chồng lấn {len(overlap)}')
for r, ch in p['update']: print('   cập nhật', r['Dngc'], r['LineNo'], ch)
E.apply(store, p)
check(store[k]['qc'] == qc_before, 'dữ liệu QC (mác, yêu cầu sửa tay, Mã QL, ảnh, đo, CQ) giữ nguyên sau import')
check(len(store) == 484 + len(p['add']), 'tổng đơn = cũ + thêm mới')
# ERP đổi số lượng cho đơn đã có CQ → cập nhật trường ERP + cờ cảnh báo, CQ không đổi
r2 = copy.deepcopy(rh); [r.update(Quantity=r['Quantity'] + 1) for r in r2 if (r['Dngc'], r['LineNo']) == k]
p = E.preview(store, r2); E.apply(store, p)
check(any('ERP thay đổi sau CQ' in f for f in store[k]['flags']) and store[k]['qc'] == qc_before, 'ERP đổi đơn đã có CQ → cờ cảnh báo, QC/CQ giữ nguyên')
# file không phải MF0015
import tempfile, os
t = tempfile.NamedTemporaryFile('w', suffix='.xls', delete=False); t.write('<html><table><tr><td>abc</td></tr></table></html>'); t.close()
try: E.read(t.name); check(False, 'file lạ phải bị từ chối')
except ValueError as e: check(True, f'file không phải MF0015 → từ chối: {e}')
os.unlink(t.name)
print('Lỗi:', fails); sys.exit(1 if fails else 0)
