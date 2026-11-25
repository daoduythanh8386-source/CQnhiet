"""THAM CHIẾU kho chung (ADR-001): bản ghi = chuỗi version bất biến, publish bằng move không ghi đè (CAS),
outbox cục bộ, khóa lease, cấp số báo cáo tạo-không-ghi-đè. Kiểm thử trên ổ cục bộ (Linux) — tính nguyên tử trên
SMB tới ADMIN-PC phải kiểm lại trên Windows thật (ghi ở docs)."""
import os, json, uuid, hashlib, time, socket, errno

MACHINE = socket.gethostname()


def _sha(b): return hashlib.sha256(b).hexdigest()


class Conflict(Exception):
    pass


class Store:
    def __init__(self, root, machine=MACHINE):
        self.root, self.machine = root, machine
        for d in ('records', 'staging', 'conflicts', 'locks', 'counters', 'events'):
            os.makedirs(os.path.join(root, d), exist_ok=True)

    # ---------- bản ghi có version ----------
    def _dir(self, typ, rid): return os.path.join(self.root, 'records', typ, rid.replace('/', '_'))

    def versions(self, typ, rid):
        d = self._dir(typ, rid)
        if not os.path.isdir(d): return []
        return sorted(int(f[1:7]) for f in os.listdir(d) if f.startswith('v') and f.endswith('.json') and len(f) == 12)

    def read(self, typ, rid, ver=None):
        vs = self.versions(typ, rid)
        if not vs: return None
        ver = ver or vs[-1]
        raw = open(os.path.join(self._dir(typ, rid), f'v{ver:06d}.json'), 'rb').read()
        doc = json.loads(raw)
        body = json.dumps(doc['data'], sort_keys=True, ensure_ascii=False).encode()
        if _sha(body) != doc['meta']['sha256']: raise IOError(f'Hỏng dữ liệu {typ}/{rid} v{ver}: sai SHA-256')
        return doc

    def commit(self, typ, rid, data, base_version, user, op_id):
        """CAS: chỉ tạo v(base+1); nếu đã có (máy khác ghi trước) → Conflict. Idempotent theo op_id."""
        vs = self.versions(typ, rid); latest = (vs or [0])[-1]
        if latest > base_version:
            # đã áp dụng ở lần thử trước? (có thể đã có máy khác ghi tiếp sau đó → quét mọi version > base)
            for v in vs:
                if v > base_version and self.read(typ, rid, v)['meta'].get('opId') == op_id: return v
            raise Conflict(f'{typ}/{rid}: base v{base_version} nhưng kho đã ở v{latest}')
        body = json.dumps(data, sort_keys=True, ensure_ascii=False).encode()
        doc = {'meta': {'version': base_version + 1, 'sha256': _sha(body), 'user': user, 'machine': self.machine,
                        'at': time.strftime('%Y-%m-%dT%H:%M:%S'), 'opId': op_id, 'base': base_version}, 'data': data}
        tmp = os.path.join(self.root, 'staging', f'{uuid.uuid4()}.tmp')
        with open(tmp, 'wb') as f:
            f.write(json.dumps(doc, ensure_ascii=False).encode()); f.flush(); os.fsync(f.fileno())
        if _sha(json.dumps(json.load(open(tmp, encoding='utf-8'))['data'], sort_keys=True, ensure_ascii=False).encode()) != doc['meta']['sha256']:
            os.unlink(tmp); raise IOError('Ghi tạm lỗi kiểm tra hash')
        d = self._dir(typ, rid); os.makedirs(d, exist_ok=True)
        dst = os.path.join(d, f'v{base_version + 1:06d}.json')
        try:
            os.link(tmp, dst)          # tạo-không-ghi-đè nguyên tử (Linux). Windows: File.Move(tmp, dst, overwrite:false)
        except FileExistsError:
            os.unlink(tmp)
            raise Conflict(f'{typ}/{rid}: máy khác vừa ghi v{base_version + 1}')
        os.unlink(tmp)
        ev = os.path.join(self.root, 'events', f'{time.time_ns()}-{self.machine}-{uuid.uuid4().hex[:8]}.json')
        open(ev, 'w').write(json.dumps({'type': typ, 'id': rid, 'version': base_version + 1}))
        return base_version + 1

    def save_conflict(self, typ, rid, mine, note):
        d = os.path.join(self.root, 'conflicts', typ, rid.replace('/', '_')); os.makedirs(d, exist_ok=True)
        p = os.path.join(d, f'{uuid.uuid4()}.json'); open(p, 'w').write(json.dumps({'mine': mine, 'note': note}, ensure_ascii=False))
        return p

    # ---------- số báo cáo ----------
    def claim_report_no(self, year_month, cq_ref):
        d = os.path.join(self.root, 'counters', 'reportno', year_month); os.makedirs(d, exist_ok=True)
        while True:
            used = [int(f[:3]) for f in os.listdir(d) if f[:3].isdigit()]
            n = max(used, default=0) + 1
            try:
                fd = os.open(os.path.join(d, f'{n:03d}.json'), os.O_CREAT | os.O_EXCL | os.O_WRONLY)   # CreateNew
                os.write(fd, json.dumps({'cq': cq_ref, 'machine': self.machine}).encode()); os.close(fd)
                return f'{n:03d}/{year_month[5:]}'
            except FileExistsError:
                continue                                                     # máy khác vừa lấy → thử số kế

    # ---------- khóa lease ----------
    # Mỗi lần giành khóa = tạo file thế hệ mới locks/<key>/<gen>.json bằng CreateNew → chỉ 1 máy thắng,
    # không bao giờ đổi tên/xóa khóa của người khác (tránh race "thu hồi nhầm khóa vừa tạo").
    def _lockdir(self, key):
        d = os.path.join(self.root, 'locks', key.replace('/', '_')); os.makedirs(d, exist_ok=True); return d

    def _latest_lock(self, key):
        d = self._lockdir(key); gens = sorted(int(f[:-5]) for f in os.listdir(d) if f.endswith('.json'))
        if not gens: return 0, None
        p = os.path.join(d, f'{gens[-1]}.json')
        for _ in range(5):   # file khóa vừa tạo có thể chưa ghi xong → đọc lại; vẫn dở thì coi là ĐANG GIỮ (lỗi phát hiện khi test trên Windows)
            try:
                txt = open(p).read()
                if txt: return gens[-1], json.loads(txt)
            except (json.JSONDecodeError, FileNotFoundError): pass
            time.sleep(0.02)
        w = os.path.getmtime(p)
        return gens[-1], {'user': '?', 'machine': '?', 'expires': w + 300}

    def lock(self, key, user, ttl=300, grace=120, now=None):
        now = time.time() if now is None else now   # 0 là thời điểm hợp lệ (không dùng "or")
        gen, cur = self._latest_lock(key)
        note = None
        if cur and not cur.get('released'):
            if cur['user'] == user and cur['machine'] == self.machine:
                cur['expires'] = now + ttl                                   # gia hạn khóa của chính mình
                open(os.path.join(self._lockdir(key), f'{gen}.json'), 'w').write(json.dumps(cur)); return True, None
            if now <= cur['expires'] + grace:
                return False, f'Đang sửa bởi {cur["user"]} trên {cur["machine"]}'
            note = f'thu hồi khóa cũ của {cur["user"]}@{cur["machine"]}'      # ghi audit ở tầng ứng dụng
        body = json.dumps({'user': user, 'machine': self.machine, 'at': now, 'expires': now + ttl}).encode()
        try:
            fd = os.open(os.path.join(self._lockdir(key), f'{gen + 1}.json'), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            os.write(fd, body); os.close(fd); return True, note
        except FileExistsError:
            return False, 'Máy khác vừa giành khóa'

    def unlock(self, key, user):
        gen, cur = self._latest_lock(key)
        if cur and cur['user'] == user and cur['machine'] == self.machine and not cur.get('released'):
            cur['released'] = True; open(os.path.join(self._lockdir(key), f'{gen}.json'), 'w').write(json.dumps(cur))


class Outbox:
    """Hàng đợi cục bộ khi offline. Chỉ xóa mục sau khi đọc lại kho và khớp version + SHA."""
    def __init__(self, path):
        self.path = path; os.makedirs(path, exist_ok=True)

    def add(self, typ, rid, data, base_version, user):
        op = {'opId': str(uuid.uuid4()), 'ts': time.time_ns(), 'type': typ, 'id': rid, 'data': data, 'base': base_version, 'user': user, 'state': 'pending'}
        open(os.path.join(self.path, f"{op['ts']}-{op['opId']}.json"), 'w').write(json.dumps(op, ensure_ascii=False)); return op['opId']

    def items(self):
        return [json.load(open(os.path.join(self.path, f))) for f in sorted(os.listdir(self.path))]

    def sync(self, store, online=True):
        if not online: return {'pushed': 0, 'conflict': 0, 'pending': len(self.items()), 'offline': True}
        res = {'pushed': 0, 'conflict': 0}
        for f in sorted(os.listdir(self.path)):
            p = os.path.join(self.path, f); op = json.load(open(p))
            if op['state'] == 'conflict': res['conflict'] += 1; continue
            try:
                v = store.commit(op['type'], op['id'], op['data'], op['base'], op['user'], op['opId'])
                back = store.read(op['type'], op['id'], v)
                if back['meta']['opId'] == op['opId'] and back['data'] == op['data']:
                    os.unlink(p); res['pushed'] += 1                          # xác nhận xong mới xóa
            except Conflict as e:
                op['state'] = 'conflict'; op['conflictFile'] = store.save_conflict(op['type'], op['id'], op, str(e))
                open(p, 'w').write(json.dumps(op, ensure_ascii=False)); res['conflict'] += 1
        res['pending'] = len(self.items()); return res
