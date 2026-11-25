#!/bin/bash
# Chạy toàn bộ kiểm thử tham chiếu. Thoát mã ≠ 0 nếu có lỗi.
cd "$(dirname "$0")/.." || exit 1
rc=0
run(){ echo "=== $1"; shift; "$@" > /tmp/t.out 2>&1; c=$?; tail -n 3 /tmp/t.out; [ $c -ne 0 ] && { cat /tmp/t.out; rc=1; }; }
run "Bản đồ ô 3 template" python3 research/validate_maps.py .
run "Quy tắc nghiệp vụ" python3 research/reference/test_domain.py
run "Import ERP (xlsx + HTML)" python3 research/reference/test_erp.py
run "Kho chung / outbox / khóa / số báo cáo" python3 research/reference/test_store.py
run "Đọc số máy đo độ cứng" python3 research/ocr/test_golden.py
run "Đọc số đầu cân" python3 research/ocr/test_scale.py
for t in carbon khuon gio; do
  run "CQ $t – dữ liệu cơ bản + fidelity" bash -c "python3 research/cq_fill_proto.py . $t research/out/data.json /tmp/rt_$t.xlsx && python3 research/fidelity_check.py sources/templates/$t.xlsx /tmp/rt_$t.xlsx config/template-maps/$t.json && cd /tmp && soffice --headless --convert-to pdf rt_$t.xlsx >/dev/null 2>&1 && pdfinfo rt_$t.pdf | grep -q 'Pages: *1$' && echo '1 trang A4'"
done
run "CQ carbon – 5 ảnh, sắp xếp, bỏ trùng, số cân, dấu tự do" bash -c "python3 research/cq_fill_proto.py . carbon research/out/data_can.json /tmp/rt_full.xlsx && python3 research/fidelity_check.py sources/templates/carbon.xlsx /tmp/rt_full.xlsx config/template-maps/carbon.json && cd /tmp && soffice --headless --convert-to pdf rt_full.xlsx >/dev/null 2>&1 && pdfinfo rt_full.pdf | grep -q 'Pages: *1$' && echo '1 trang A4'"
run "Chặn mác/template lệch số giai đoạn tôi (phải báo lỗi)" bash -c "! python3 research/cq_fill_proto.py . gio research/out/data_5anh.json /tmp/x.xlsx && echo 'đã chặn đúng'"
echo; [ $rc -eq 0 ] && echo "TẤT CẢ ĐẠT" || echo "CÓ LỖI"; exit $rc
