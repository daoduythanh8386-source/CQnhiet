# Quy tắc nghiệp vụ đã chốt + bộ test bắt buộc (port sang xUnit)

## Yêu cầu độ cứng
Chấp nhận `a-b` (khoảng trắng tùy ý, dấu `-`/`–`), 0<a<b≤70. Khác → "cần nhập tay" (không đoán).

## Giới hạn (OQ-01)
| Dung sai (sau khi bỏ khoảng trắng, không phân biệt hoa thường) | Giới hạn |
|---|---|
| `(MAX-MIN)±n` | [a−n, b+n] |
| `MIN-n` | [a−n, b] |
| `MIN±n` | [a−n, b] |
| `MAX+n` | [a, b+n] |
| trống | [a, b] |
| khác | lỗi cấu hình → chặn tạo CQ |

## Test vector
| Yêu cầu | Dung sai | Điểm đo | Kết quả |
|---|---|---|---|
| 58-62 | MIN -0.7 | 57.3 | ĐẠT (biên dưới) |
| 58-62 | MIN -0.7 | 57.2 | KHÔNG ĐẠT |
| 58-62 | MIN -0.7 | 62.1 | KHÔNG ĐẠT |
| 56-60 | (MAX-MIN)±1 | 55 ; 61 | ĐẠT |
| 56-60 | ( MAX-MIN ) ±1 | 61.1 | KHÔNG ĐẠT |
| 60-62 | MAX +0.5 | 62.5 / 59.9 | KHÔNG ĐẠT (59.9) |
| 58-60 | (trống) | 58 60 | ĐẠT |
| MAX | bất kỳ | — | Yêu cầu không hợp lệ |
So sánh bằng `decimal` (không dùng double) để biên 57.3 không lệch.

## Điểm đo
Tách bằng khoảng trắng hoặc `;`; dấu phẩy thập phân `,` chấp nhận và chuẩn hóa `.`; tối đa 6 mẫu; mẫu rỗng bỏ qua; cần ≥1 điểm. Điểm OCR chưa xác nhận → không đánh giá.
Ô CQ: các điểm nối ` / ` giữ nguyên số chữ số thập phân người nhập. Trống → `-`.
Nhận xét: ĐẠT → `Nhận xét/Remark: Chất lượng đạt`; KHÔNG ĐẠT → `Nhận xét/Remark: Không đạt độ cứng` + `; ` + ghi chú (nếu có).

## Số báo cáo (OQ-09)
`NNN/MM`, NNN 3 chữ số từ 001 mỗi tháng (theo ngày tạo CQ), chung mọi template; số mới = số lớn nhất đã cấp + 1, **không dùng lại** số của CQ đã xóa. Cấp bằng file tạo-không-ghi-đè `counters/reportno/yyyy-MM/NNN.json` → không trùng giữa các máy khi online. Số đổi tay: kiểm tra trùng trong cùng năm-tháng → cảnh báo.

## Gộp đơn
Cùng Dngc, cùng mác (chuẩn hóa), cùng yêu cầu độ cứng (chuẩn hóa), tất cả chưa có CQ, không dòng nào đã thuộc nhóm gộp khác. SL = tổng, KL = tổng (decimal).

## Nhiệt độ (OQ-04)
Carbon: Heat1→D15, Heat2→H14. Khuôn: textbox [650,850,1030] ← Heat1..3. Gió: [650,850,1000,1180] ← Heat1..4. Giá trị số (nhóm Gió lưu 650) hiển thị thành `650℃`. Mác thiếu giai đoạn mà template có → chặn.

## Ảnh độ cứng trùng kết quả (người dùng 13/11/2026)
Khi CQ có **từ 3 ảnh độ cứng trở lên**: các ảnh có kết quả đồng hồ trùng nhau của cùng Số ĐNGC + cùng khách hàng + cùng mác → chỉ đưa **1 ảnh** vào CQ (ảnh được thêm trước). CQ có ≤ 2 ảnh → giữ nguyên.
- So sánh theo giá trị QC đã xác nhận, kiểu số thập phân (`47.3` = `47.30`); ảnh chưa xác nhận không bị gộp.
- Ảnh bị bỏ chỉ không in lên CQ; vẫn nằm trong đơn; thông báo + audit "Bỏ ảnh trùng kết quả …".
- Bảng kết quả đo (Mẫu 1–6) **không** bị gộp — mọi giá trị đo vẫn ghi đủ.

| Test | Kỳ vọng | Kết quả |
|---|---|---|
| 5 ảnh, có 2 ảnh 47.3 cùng đơn | còn 4 ảnh, bỏ ảnh 47.3 thêm sau | Đạt |
| 2 ảnh 47.3 và 47.30 | giữ cả 2 | Đạt |
| thêm ảnh 47.3 của ĐNGC khác | giữ (khác đơn) | Đạt |
| thêm ảnh chưa xác nhận | giữ, xếp cuối | Đạt |


Cài đặt tham chiếu chạy được: `research/reference/domain.py` (+ `test_domain.py`), `erp_import.py`, `store.py`. Bản .NET phải qua cùng bộ test.
