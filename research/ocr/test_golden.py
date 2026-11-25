"""Kiểm thử hồi quy OCR: ĐÚNG = đúng số; TỪ CHỐI = không đưa số (chấp nhận được); SAI = đưa số sai (lỗi nghiêm trọng)."""
import json, sys
sys.path.insert(0, 'research/ocr'); import read_hardness
gold = json.load(open('research/ocr/golden.json')); bad = 0; stats = {'ĐÚNG': 0, 'TỪ CHỐI': 0, 'SAI': 0}
for f, want in gold.items():
    r = read_hardness.read(f); got = r.get('value')
    k = 'ĐÚNG' if got == want else ('TỪ CHỐI' if got is None else 'SAI')
    if want is None and got is None: k = 'ĐÚNG'
    stats[k] += 1; bad += k == 'SAI'
    print(f'{k:8} {f.split("/")[-1]:32} mong đợi={want} đọc={got} tin cậy={r.get("confidence")} {r.get("reason","")}')
print(stats); sys.exit(1 if bad else 0)
