import sys; sys.path.insert(0, 'research/reference')
from decimal import Decimal as D
import domain as d
import datetime

def check(cond, msg):
    global fails
    print(('ĐẠT  ' if cond else 'LỖI  ') + msg); fails += (not cond)
fails = 0
# Test vector trong docs/business-rules.md
V = [('58-62', 'MIN -0.7', ['57.3'], 'PASS'), ('58-62', 'MIN -0.7', ['57.2'], 'FAIL'), ('58-62', 'MIN -0.7', ['62.1'], 'FAIL'),
     ('56-60', '(MAX-MIN)±1', ['55 ; 61'], 'PASS'), ('56-60', '( MAX-MIN ) ±1', ['61.1'], 'FAIL'),
     ('60-62', 'MAX +0.5', ['62.5', '59.9'], 'FAIL'), ('58-60', None, ['58 60'], 'PASS'), ('MAX', 'MIN -0.7', ['58'], 'INVALID_REQUIREMENT')]
for req, tol, s, want in V:
    r = d.evaluate(req, tol, s); check(r['status'] == want, f'{req} | {tol} | {s} → {r["status"]} (mong đợi {want})')
check(d.acceptance_limits((D(58), D(62)), 'MIN - 0.7') == (D('57.3'), D(62)), "'MIN - 0.7' có khoảng trắng")
check(d.acceptance_limits((D(58), D(62)), 'MIN ±0.7') == (D('57.3'), D(62)), "'MIN ±0.7'")
check(d.acceptance_limits((D(58), D(62)), 'MAX+0.7') == (D(58), D('62.7')), "'MAX+0.7'")
try: d.acceptance_limits((D(58), D(62)), 'abc'); check(False, 'dung sai lạ phải báo lỗi')
except d.ToleranceError: check(True, 'dung sai lạ → báo lỗi, chặn CQ')
for bad in ['MAX', None, datetime.datetime(2026, 7, 4), 65.67, '49-91', 'Bề măt: 350-450hv; Lõi: 250-320HV', '62-58']:
    check(d.parse_requirement(bad) is None, f'yêu cầu không hợp lệ bị từ chối: {bad!r}')
check(d.parse_requirement(' 58 – 62 ') == (D(58), D(62)), 'gạch ngang dài + khoảng trắng')
check(d.parse_points('58,4;58.8  59') == [D('58.4'), D('58.8'), D('59')], 'tách điểm , ; khoảng trắng')
try: d.parse_points('58.4 5x'); check(False, 'điểm lỗi phải báo')
except ValueError: check(True, 'điểm đo lỗi → báo lỗi, không bỏ qua')
check(d.cq_sample_cell('58.4 58,80') == '58.4 / 58.80', 'ô CQ giữ số chữ số người nhập')
check(d.cq_sample_cell('') == '-', 'ô trống → "-"')
check(d.remark('FAIL', 'mẫu 2 thấp') == 'Nhận xét/Remark: Không đạt độ cứng; mẫu 2 thấp', 'nhận xét không đạt')
try: d.evaluate('58-62', None, ['58'] * 7); check(False, '7 mẫu phải lỗi')
except ValueError: check(True, 'tối đa 6 mẫu')
L = lambda **k: {'Dngc': 'A', 'SteelGrade': 'SKD11', 'HardnessRequirement': '58-62', 'HasCq': False, 'MergeGroup': None, **k}
check(d.can_merge([L(), L(SteelGrade='skd-11')])[0], 'gộp: mác chuẩn hóa SKD11 = skd-11')
check(not d.can_merge([L(), L(Dngc='B')])[0], 'gộp: khác ĐNGC → không')
check(not d.can_merge([L(), L(HardnessRequirement='58-60')])[0], 'gộp: khác yêu cầu → không')
check(not d.can_merge([L(), L(HasCq=True)])[0], 'gộp: đã có CQ → không')
check(d.next_report_no({1, 2, 4}, '2026-11') == ('005/11', 5), 'số báo cáo = lớn nhất + 1, không dùng lại số đã xóa (3)')
check(d.next_report_no(set(), '2026-12') == ('001/12', 1), 'tháng mới bắt đầu 001')
print('Lỗi:', fails); sys.exit(1 if fails else 0)
