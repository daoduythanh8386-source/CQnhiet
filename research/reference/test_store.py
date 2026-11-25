import sys, os, json, tempfile, multiprocessing as mp, time
sys.path.insert(0, 'research/reference'); from store import Store, Outbox, Conflict
fails = 0
def check(c, m):
    global fails; print(('ĐẠT  ' if c else 'LỖI  ') + m); fails += (not c)
root = tempfile.mkdtemp(prefix='kho_')
A, B = Store(root, 'MAY-A'), Store(root, 'MAY-B')
check(A.commit('order', 'DNGC/1_1', {'x': 1}, 0, 'qc1', 'op1') == 1, 'tạo v1')
check(A.commit('order', 'DNGC/1_1', {'x': 1}, 0, 'qc1', 'op1') == 1, 'gửi lại cùng opId → idempotent, không tạo bản trùng')
check(B.commit('order', 'DNGC/1_1', {'x': 2}, 1, 'qc2', 'op2') == 2, 'B ghi v2 từ v1')
try: A.commit('order', 'DNGC/1_1', {'x': 3}, 1, 'qc1', 'op3'); check(False, 'A ghi trên base cũ phải xung đột')
except Conflict: check(True, 'A ghi trên base v1 cũ → XUNG ĐỘT, không ghi đè v2')
check(A.read('order', 'DNGC/1_1')['data'] == {'x': 2}, 'dữ liệu của B còn nguyên')
# hỏng dữ liệu được phát hiện
p = os.path.join(root, 'records', 'order', 'DNGC_1_1', 'v000002.json'); d = json.load(open(p)); d['data']['x'] = 99; open(p, 'w').write(json.dumps(d))
try: A.read('order', 'DNGC/1_1'); check(False, 'phải phát hiện hỏng')
except IOError: check(True, 'file bị sửa ngoài app → phát hiện sai SHA-256')

# 10 máy cùng lúc ghi cùng 1 đơn: đúng 1 thắng mỗi version, không mất bản nào
def worker(i, q):
    s = Store(root, f'MAY-{i}'); ok = 0; conf = 0
    for _ in range(20):
        base = (s.versions('order', 'race') or [0])[-1]
        try: s.commit('order', 'race', {'by': i, 'base': base}, base, f'u{i}', f'{i}-{time.time_ns()}'); ok += 1
        except Conflict: conf += 1
    q.put((ok, conf))
q = mp.Queue(); ps = [mp.Process(target=worker, args=(i, q)) for i in range(10)]
[p.start() for p in ps]; [p.join() for p in ps]
res = [q.get() for _ in ps]; total_ok = sum(r[0] for r in res)
vs = Store(root).versions('order', 'race')
check(vs == list(range(1, total_ok + 1)), f'10 tiến trình × 20 lần: {total_ok} commit thành công = {len(vs)} version liên tục, {sum(r[1] for r in res)} xung đột được chặn')
check(all(Store(root).read('order', 'race', v)['data']['base'] == v - 1 for v in vs), 'mọi version đều dựa đúng version trước (không ghi đè)')

# số báo cáo: 10 máy cùng xin số
def rp(i, q): q.put([Store(root, f'M{i}').claim_report_no('2026-11', f'cq{i}-{k}') for k in range(10)])
q = mp.Queue(); ps = [mp.Process(target=rp, args=(i, q)) for i in range(10)]
[p.start() for p in ps]; [p.join() for p in ps]
nums = sum((q.get() for _ in ps), [])
check(len(nums) == 100 and len(set(nums)) == 100 and sorted(nums)[0] == '001/11' and sorted(nums)[-1] == '100/11', '10 máy × 10 CQ cùng lúc: 100 số báo cáo, không trùng, liên tục 001–100')

# khóa
ok, _ = A.lock('order-X', 'qc1', now=1000); check(ok, 'A khóa đơn')
ok, msg = B.lock('order-X', 'qc2', now=1100); check(not ok and 'qc1' in msg, f'B bị chặn: {msg}')
ok, msg = B.lock('order-X', 'qc2', now=1000 + 300 + 121); check(ok and 'thu hồi' in (msg or ''), f'khóa quá hạn + grace → B thu hồi có ghi lại: {msg}')

# outbox offline → online, có xung đột
ob = Outbox(tempfile.mkdtemp(prefix='outbox_'))
base = A.versions('order', 'DNGC/2_1') or [0]
ob.add('order', 'DNGC/2_1', {'kq': '58.4'}, 0, 'qc1'); ob.add('order', 'DNGC/3_1', {'kq': '47.3'}, 0, 'qc1')
r = ob.sync(A, online=False); check(r['pending'] == 2 and r['offline'], 'OFFLINE: 2 mục chờ, không mất')
B.commit('order', 'DNGC/3_1', {'kq': '47.5'}, 0, 'qc2', 'x')       # máy khác sửa trước khi A online
r = ob.sync(A); check(r['pushed'] == 1 and r['conflict'] == 1, f'online lại: đẩy 1, xung đột 1 ({r})')
check(A.read('order', 'DNGC/3_1')['data'] == {'kq': '47.5'}, 'bản của máy khác KHÔNG bị ghi đè')
c = [i for i in ob.items() if i['state'] == 'conflict']
check(len(c) == 1 and os.path.exists(c[0]['conflictFile']), 'bản của A được giữ ở conflicts/ + outbox chờ xử lý')
# idempotent khi đã có máy khác ghi tiếp sau
v = A.commit('order', 'idem', {'a': 1}, 0, 'qc1', 'OP-X'); B.commit('order', 'idem', {'a': 2}, v, 'qc2', 'OP-Y')
check(A.commit('order', 'idem', {'a': 1}, 0, 'qc1', 'OP-X') == 1, 'thử lại OP-X sau khi B ghi tiếp → nhận ra đã áp dụng, không báo xung đột giả')
# tranh giành khóa quá hạn: 10 máy cùng thu hồi → đúng 1 máy thắng
A.lock('order-Y', 'qc1', now=0)
def grab(i, q): q.put(Store(root, f'R{i}').lock('order-Y', f'u{i}', now=10000)[0])
q = mp.Queue(); ps = [mp.Process(target=grab, args=(i, q)) for i in range(10)]
[p.start() for p in ps]; [p.join() for p in ps]
wins = sum(q.get() for _ in ps); check(wins == 1, f'10 máy cùng thu hồi khóa quá hạn → đúng {wins} máy giữ khóa')
A2 = Store(root, 'MAY-A'); ok, _ = A2.lock('order-Z', 'qc1', now=1); A2.unlock('order-Z', 'qc1'); ok2, _ = B.lock('order-Z', 'qc2', now=2)
check(ok and ok2, 'nhả khóa → máy khác giành được ngay')
# outbox: 2 thao tác offline nối tiếp cùng 1 đơn
ob2 = Outbox(tempfile.mkdtemp(prefix='outbox2_'))
ob2.add('order', 'chain', {'s': 1}, 0, 'qc1'); ob2.add('order', 'chain', {'s': 2}, 1, 'qc1')
r = ob2.sync(A); check(r['pushed'] == 2 and A.read('order', 'chain')['data'] == {'s': 2}, 'thao tác offline nối tiếp đẩy đúng thứ tự')
print('Lỗi:', fails); sys.exit(1 if fails else 0)
