"""Hồi quy đọc số đầu cân (LED 7 đoạn) — ĐÚNG / TỪ CHỐI / SAI."""
import sys, json
sys.path.insert(0, 'research/ocr'); import scale_led
gold = {'sources/samples/images/sanpham_goc.jpg': '41.40',
        'sources/samples/images/ref_sanpham_da_chinh.jpg': None,      # đã cắt bỏ đầu cân → phải từ chối
        'sources/samples/images/mitu_goc_38.2.jpg': None}            # không có đầu cân → phải từ chối
bad = 0
for f, want in gold.items():
    r = scale_led.read(f); got = r.get('value')
    k = 'ĐÚNG' if got == want else ('TỪ CHỐI' if got is None else 'SAI'); bad += k == 'SAI'
    print(f'{k:8} {f.split("/")[-1]:28} mong đợi={want} đọc={got} {r.get("reason","")}')
sys.exit(1 if bad else 0)
