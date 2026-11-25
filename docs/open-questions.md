# Câu hỏi mở (cần người dùng xác nhận)

## Đã chốt (người dùng trả lời 13/11/2026)
- **OQ-18** → Chữ ký trên CQ dùng bản **nền trong suốt** (thư viện chữ ký vẫn lưu bản nền giấy A4).
- **OQ-20** → Bảng kết quả Mẫu 1–6 **sắp tăng dần** theo cùng thứ tự với ảnh (khóa = điểm nhỏ nhất của mẫu; bằng nhau giữ thứ tự nhập; chỉ đổi thứ tự in, dữ liệu nhập giữ nguyên). Nhãn ảnh vẫn chỉ ghi giá trị vì ảnh trùng kết quả bị bỏ khỏi CQ (bảng vẫn ghi đủ).
- **OQ-23** → Ô kết quả Mẫu 1–6 **đồng nhất chữ đậm** (cả 3 template). Không sửa template gốc: bản sao CQ thêm kiểu chữ đậm mới (nhân bản font + kiểu ô, giữ viền/căn lề/định dạng số).
- **OQ-21** → Số cân ghi **kèm "kg"** (vd `41.40 kg`). Phần cảnh báo lệch với trọng lượng ERP: chưa chốt, chưa làm.
- **OQ-01** → Nới đúng đầu được ghi: `(MAX-MIN)±1` → [a−1, b+1]; `MIN-x` → [a−x, b]; `MAX+x` → [a, b+x]; trống → [a, b]. `MIN±0.7` hiểu là nới đầu MIN 0.7 → [a−0.7, b] (suy từ quy tắc, vẫn hiển thị để QC thấy).
- **OQ-02/03** → ĐẠT khi **mọi điểm đo** nằm trong giới hạn; ô CQ ghi các điểm cách nhau ` / `.
- **OQ-04** → Nhiệt độ tôi **ghi theo bảng mác**: carbon ghi ô D15/H14; khuôn/gió thay chữ trong textbox hình vẽ theo thứ tự (đã thử: SCS14 → 1080℃, SUJ2 → 920℃, file vẫn đạt kiểm tra fidelity). Mác thiếu nhiệt độ → chặn tạo CQ, QC nhập tay có audit.
- **OQ-09** → Số báo cáo đánh lại từ 001 mỗi tháng, dùng chung 3 template (khóa duy nhất năm-tháng-số).


Mỗi mục ghi: mặc định tạm thời ứng dụng sẽ dùng (luôn hiển thị là "chờ xác nhận" trong Settings) và mức ảnh hưởng.
**Không phát hành CQ chính thức cho trường hợp còn phụ thuộc câu hỏi chưa chốt ở mức Cao.**

| ID | Câu hỏi | Mặc định tạm | Ảnh hưởng |
|---|---|---|---|
| OQ-01 | Cách tính **giới hạn chấp nhận** từ dung sai, với yêu cầu `a-b`: `(MAX-MIN)±1` → [a−1, b+1]? `MIN-0.7` → [a−0.7, b]? `MAX+0.5`/`MAX+0.7` → [a, b+0.5/0.7]? `MIN±0.7` → [a−0.7, b] hay [a−0.7, a+0.7…]? Dung sai trống → [a, b]? | Như đề xuất; `MIN±0.7` và dung sai trống **chặn tạo CQ** cho tới khi xác nhận | **Cao** – quyết định ĐẠT/KHÔNG ĐẠT |
| OQ-02 | ĐẠT khi **mọi điểm đo** nằm trong giới hạn, hay khi **trung bình mỗi mẫu**? Số mẫu tối thiểu? | Mọi điểm; tối thiểu 1 mẫu có ≥1 điểm | **Cao** |
| OQ-03 | Một mẫu có nhiều điểm: ô kết quả trên CQ ghi thế nào (vd `58.4 / 58.8`, hay trung bình 1 số lẻ)? | Ghi các điểm cách nhau ` / ` | Trung bình |
| OQ-04 | Nhiệt độ Tôi/Ram trên CQ: có thay theo mác không? Carbon có ô D15/H14 (SK5 800℃, SUJ2/SCM420/4340… 920℃). Khuôn/Gió nằm trong **hình vẽ** cố định 1030℃ / 1000–1180℃ – nhưng SCS14/CX15/SUS303C/SCS13/SUS304 là **1080℃**. | Carbon: ghi D15/H14 theo bảng. Khuôn/Gió: giữ hình vẽ; nếu mác có nhiệt độ khác hình vẽ → **cảnh báo, chặn duyệt** cho tới khi chốt | **Cao** – CQ có thể in sai nhiệt độ |
| OQ-05 | Cột "ĐÁNH DẤU" 1/2/3 có đúng là 1=Khuôn dập, 2=Gió, 3=Carbon? Ram carbon trong template ghi "180min" còn bảng ghi "180p" – dùng cái nào? | 1/2/3 như trên; giữ chữ trong template | Trung bình |
| OQ-06 | Mác trùng khác giá trị: `M390` (Gió vs Khuôn), `SCM420` (850 vs 920℃). Lấy dòng nào? | Báo lỗi, bắt QC chọn dòng khi gặp mác này | Cao với 2 mác này |
| OQ-07 | Dòng 8 (Mã QL/Tên chi tiết) đang **ẩn** trong carbon/khuon. Khi có Mã QL thì hiện dòng 8 trên CQ không? | Hiện dòng 8 chỉ khi có Mã QL hoặc Tên chi tiết | Trung bình |
| OQ-08 | Vị trí dấu công ty: đè lên ô Xác nhận/Approved (N34:R36) cạnh chữ ký người duyệt? | Dấu + chữ ký người duyệt ở N34:R36; chữ ký người kiểm tra ở S34:U36 | Trung bình |
| OQ-09 | Số báo cáo `NNN/MM`: đánh lại từ 001 mỗi tháng? Chung cho cả 3 template? Năm có tham gia không (trùng NNN/MM giữa các năm)? | Mỗi tháng từ 001, chung mọi template, khóa duy nhất theo năm-tháng | Cao (trùng số) |
| OQ-10 | "Vật liệu" trên CQ ghi mác (SKD11) hay tên sản phẩm ERP (Thép hợp kim SKD11)? | Mác thép | Thấp |
| OQ-11 | Chế độ "một máy không cần đăng nhập" trong PDF-3 có giữ không? Skill yêu cầu tài khoản/phiên riêng. | **Luôn đăng nhập** (an toàn hơn); có thể bật lại sau khi xác nhận | Trung bình |
| OQ-12 | OCR Mitutoyo: cần thêm ảnh thật có đủ chữ số 0–9 (≥3 ảnh/chữ số) để làm bộ đọc ma trận điểm tin cậy. Hiện đọc sai 58.4→53.4. | OCR Mitutoyo chỉ gợi ý, độ tin cậy thấp, bắt buộc nhập/xác nhận tay | Trung bình |
| OQ-13 | Có máy luôn bật để làm điều phối 24/7 không (NAS Synology có Container Manager?) — chỉ cần nếu muốn khóa tài khoản tuyệt đối khi ADMIN-PC tắt | Không; dùng lease + cảnh báo | Thấp |
| OQ-14 | Quyền "xóa CQ đã duyệt": qc được xóa (theo PDF) hay chỉ admin? | qc được, bắt buộc lý do, audit; bản gốc không bị xóa vật lý (chỉ đánh dấu) | Trung bình |
| OQ-15 | Template gio dùng footer `TQM.QT11.BM02-03/07/2023` trùng mã biểu mẫu carbon – có đúng không? | Giữ nguyên file | Thấp |
| OQ-16 | Ghi chú ERP (cột R) đôi khi là kích thước (vd `350x250x25.2`). Có muốn gợi ý vào ô Kích thước không? | Không tự điền; hiển thị để QC chép | Thấp |
| OQ-17 | Đơn ERP không có yêu cầu độ cứng (91/484) – có phải tạo CQ không (vd Thấm Nitor)? | Cho phép nhập tay yêu cầu; không có yêu cầu → không tạo được CQ | Trung bình |
| OQ-18 | Khi chèn chữ ký lên CQ: dùng bản nền giấy A4 (đúng quy ước, nhưng lộ khung xám nhạt, che một phần dấu) hay bản trong suốt cùng nét đã làm nét? | Bản nền giấy (theo quy ước) cho tới khi chốt | Trung bình |
| OQ-19 | Chữ ký Đào Duy Thanh cũ (`checker.png`, bị cắt mất cuối chữ "Thanh") có thay bằng bản mới không? | Dùng bản mới | Thấp |
| OQ-20 | Ảnh độ cứng đã xếp tăng dần. Bảng kết quả Mẫu 1–6 có sắp tăng dần theo cùng thứ tự không, và nhãn ảnh có cần số thứ tự không? | Bảng kết quả giữ thứ tự nhập; nhãn chỉ ghi giá trị | Thấp |
| OQ-21 | Số cân trên ảnh xuất hàng: có cần ghi đơn vị (kg) cạnh số không? Có cảnh báo khi lệch với Trọng lượng ERP không (bao nhiêu % thì cảnh báo)? | Chỉ ghi số đúng như màn hình; chưa cảnh báo lệch | Thấp |
| OQ-22 | Bộ đọc số cân mới thử trên **1 ảnh** (Ohaus Defender 2000). Cần thêm 5–10 ảnh gốc có đầu cân với các số khác nhau (có chữ số 2,3,5,6,7,8,9; số lớn kiểu 135.2) để kiểm chứng. | Gợi ý tối đa 0.8, bắt xác nhận | Trung bình |
| OQ-23 | Template carbon: ô kết quả mẫu 5–6 (H30, H31) chữ thường, mẫu 1–4 chữ đậm. Có muốn sửa template gốc cho đồng nhất không? (ứng dụng không tự sửa template) | Giữ nguyên template | Thấp |
