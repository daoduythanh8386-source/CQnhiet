"""Kiểm thử pipeline trên ảnh THẬT + biến thể xấu (tối, mờ, lóa, nghiêng, nhiễu, độ phân giải thấp, đen trắng).
Tiêu chí an toàn: không có trường hợp "sai im lặng" (đưa ra số khác số thật mà KHÔNG cảnh báo)."""
import sys, os, json, cv2, numpy as np, tempfile
HERE = os.path.dirname(os.path.abspath(__file__)); ROOT = os.path.abspath(os.path.join(HERE, '..', '..'))
sys.path.insert(0, HERE); sys.path.insert(0, os.path.join(ROOT, 'research', 'ocr'))
import vision as V
import read_hardness, scale_led

SAMPLES = [('mitu1_47.3.png', '47.3', 'hardness'), ('mitu_goc_38.2.jpg', '38.2', 'hardness'), ('mitu_full2_58.4.png', '58.4', 'hardness'),
           ('ref_mitu_cropped_50.1.jpg', '50.1', 'hardness'), ('dyna1_47.3.png', '47.3', 'hardness'), ('dyna2_47.4.png', '47.4', 'hardness'),
           ('sanpham_goc.jpg', '41.40', 'scale'),
           ('mitu_47.6.jpg', '47.6', 'hardness'), ('mitu_47.3b.jpg', '47.3', 'hardness'), ('ohaus_4.90.jpg', '4.90', 'scale')]

def variants(img):
    yield 'gốc', img
    yield 'tối', np.clip(((img / 255.0) ** 2.2) * 255 * 0.6, 0, 255).astype(np.uint8)
    yield 'mờ', cv2.GaussianBlur(img, (0, 0), 2.0)
    n = np.random.default_rng(7).normal(0, 18, img.shape)
    yield 'nhiễu', np.clip(img + n, 0, 255).astype(np.uint8)
    h, w = img.shape[:2]
    M = cv2.getRotationMatrix2D((w / 2, h / 2), 9, 1.0); yield 'nghiêng 9°', cv2.warpAffine(img, M, (w, h), borderMode=cv2.BORDER_REPLICATE)
    src = np.float32([[0, 0], [w, 0], [w, h], [0, h]]); dst = np.float32([[0.06 * w, 0.03 * h], [w, 0], [0.97 * w, h], [0, 0.95 * h]])
    yield 'phối cảnh', cv2.warpPerspective(img, cv2.getPerspectiveTransform(src, dst), (w, h), borderMode=cv2.BORDER_REPLICATE)
    g = img.copy().astype(np.float32); yy, xx = np.mgrid[0:h, 0:w]
    blob = np.exp(-(((xx - 0.62 * w) / (0.10 * w)) ** 2 + ((yy - 0.35 * h) / (0.08 * h)) ** 2)) * 200
    yield 'lóa', np.clip(g + blob[..., None], 0, 255).astype(np.uint8)
    yield 'độ phân giải thấp', cv2.resize(img, None, fx=0.4, fy=0.4, interpolation=cv2.INTER_AREA)
    gg = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY); yield 'đen trắng', cv2.cvtColor(gg, cv2.COLOR_GRAY2BGR)

def reader(img, kind):
    with tempfile.NamedTemporaryFile(suffix='.png', delete=False) as f: cv2.imwrite(f.name, img); p = f.name
    try: r = read_hardness.read(p) if kind == 'hardness' else scale_led.read(p)
    except Exception as e: r = {'value': None, 'reason': 'lỗi ' + str(e)}
    finally: os.unlink(p)
    return r.get('value'), r.get('confidence', 0)

def run():
    rows = []; out_dir = os.path.join(HERE, 'out'); os.makedirs(out_dir, exist_ok=True)
    for f, truth, kind in SAMPLES:
        src = cv2.imread(os.path.join(ROOT, 'sources', 'samples', 'images', f))
        for vn, img in variants(src):
            hd, steps = V.enhance_hd(img)
            rd, rsteps = V.for_reading(img)
            ro, co = reader(img, kind); rp, cp = reader(rd, kind)
            regs = V.detect_regions(img)
            # quyết định theo quy trình: chỉ đề xuất khi 2 lần đọc (ảnh gốc / ảnh xử lý) KHỚP và đủ tin cậy; khác nhau / thiếu → cảnh báo, chờ người xác nhận
            flags = []
            if ro is None and rp is None: flags.append('không đọc được')
            elif ro != rp: flags.append(f'gốc≠xử lý ({ro}≠{rp})')
            if max(co or 0, cp or 0) < 0.8 and (ro or rp): flags.append('độ tin cậy thấp')
            sugg = ro if ro is not None else rp
            silent_wrong = sugg is not None and not flags and sugg != truth
            rows.append(dict(file=f, variant=vn, truth=truth, orig=ro, proc=rp, suggestion=sugg, flags=flags, silent_wrong=silent_wrong,
                             regions=[r['kind'] for r in regs], steps=steps))
            if vn in ('gốc', 'tối', 'nghiêng 9°'):
                tag = os.path.splitext(f)[0] + '_' + {'gốc': 'goc', 'tối': 'toi', 'nghiêng 9°': 'nghieng'}[vn]
                small = lambda x: cv2.resize(x, (600, int(600 * x.shape[0] / x.shape[1])))
                cv2.imwrite(os.path.join(out_dir, tag + '_truoc_sau.jpg'), np.hstack([small(img), small(hd), cv2.cvtColor(small(V.print_bw(hd)), cv2.COLOR_GRAY2BGR)]))
    return rows

if __name__ == '__main__':
    rows = run()
    json.dump(rows, open(os.path.join(HERE, 'out', 'ket_qua.json'), 'w'), ensure_ascii=False, indent=1)
    ok = sum(1 for r in rows if r['suggestion'] == r['truth'] and not r['flags'])
    flagged = sum(1 for r in rows if r['flags'])
    wrong = [r for r in rows if r['silent_wrong']]
    for r in rows: print(f"{r['file']:28s} {r['variant']:18s} thật={r['truth']:6s} gốc={str(r['orig']):6s} xửlý={str(r['proc']):6s} {'; '.join(r['flags']) or 'OK'} {r['regions']}")
    print(f'\nTỔNG {len(rows)}: đúng & khớp {ok}, cảnh báo chờ xác nhận {flagged}, SAI IM LẶNG {len(wrong)}')
