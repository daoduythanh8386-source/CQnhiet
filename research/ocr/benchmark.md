# Thử nghiệm OCR trên ảnh thật (13/11/2026)

Công cụ: Tesseract 5.3.4 + `eng.traineddata` do người dùng cung cấp (SHA256 7d4322bd…). Ảnh: 5 ảnh người dùng gửi.
Script: `roi.py` (tìm vùng màn hình theo màu), `dotmatrix.py` (thử đọc ma trận điểm 5×7).

| Ảnh | Máy | Giá trị thật | OCR cả ảnh (psm 11) | OCR sau khi cắt vùng màn hình | Kết luận |
|---|---|---|---|---|---|
| mitu1_47.3.png | Mitutoyo | 47.3 | rác (không có số) | `.47.3HR` → 47.3 | Đúng sau khi lọc |
| mitu_full2_58.4.png | Mitutoyo (ảnh cả máy) | 58.4 | rác | `53.4HR` → **53.4** | **SAI** (8 → 3) |
| dyna1_47.3.png | dynaROCK II | 47.3 | rác | `47.3` | Đúng |
| dyna2_47.4.png | dynaROCK II | 47.4 | rác | `47.4` | Đúng |
| ocr_sample_machine.png | 2 LCD nhỏ trong CQ cũ | 61.4 / 61.8 | — | chọn sai vùng (`CC .`) | Không đọc được |

Kết quả: 3/5 đúng, 1 sai số nguy hiểm (58.4 đọc thành 53.4), 1 không đọc được.

## Kết luận bắt buộc cho thiết kế
1. `eng.traineddata` **không** đọc được ảnh nguyên máy. Phải cắt vùng màn hình theo profile máy (Mitutoyo: LCD vàng-xanh; dynaROCK II: số xanh lớn trên nền trắng).
2. LCD Mitutoyo là font ma trận điểm riêng (không trùng font HD44780 chuẩn — đã thử, sai). Muốn đọc tin cậy cần thêm ảnh mẫu đủ các chữ số 0–9 để huấn luyện/so mẫu.
3. Không được tuyên bố độ chính xác. OCR chỉ là **gợi ý**; luôn hiển thị ảnh cắt cạnh ô số; QC bắt buộc xác nhận/sửa từng giá trị trước khi tính ĐẠT/KHÔNG ĐẠT.
4. Kiểm tra hợp lệ: chuỗi phải khớp `^\d{1,3}\.\d$`, nằm trong 20–70 HRC; nếu không khớp hoặc độ tin cậy thấp → để trống, đánh dấu "cần nhập tay".
5. Với dynaROCK II, chỉ đọc **số lớn** (giá trị lần đo hiện tại). Không lấy các số nhỏ (Ø trung bình, 2/10, độ lệch, lịch sử) — dễ nhầm.

## Bổ sung 13/11/2026 — chuẩn hóa ảnh theo ảnh mẫu người dùng (preset `mitutoyo-cq`)
OCR (tesseract, ROI) trên ảnh gốc / ảnh đã chuẩn hóa:
| Ảnh | Thật | Gốc | Sau chuẩn hóa |
|---|---|---|---|
| mitu_goc_38.2 | 38.2 | 36.2 | 3.2 |
| mitu1_47.3 | 47.3 | 47.3 | 47.3 |
| mitu_full2_58.4 | 58.4 | 53.4 | 38.4 |
| ref_mitu_cropped_50.1 (ảnh mẫu) | 50.1 | 08. | — |
Kết luận: ảnh chuẩn hóa chỉ dùng để **đưa vào CQ**; OCR luôn chạy trên **ảnh gốc** và chỉ là gợi ý. Font ma trận điểm Mitutoyo vẫn đọc sai nhiều (2/4 sai ở ảnh gốc) → OQ-12 vẫn mở.

## Bổ sung 13/11/2026 (lần 2) — bộ đọc chuyên dụng, yêu cầu "đọc đúng số và dấu chấm/phẩy"
- **Mitutoyo**: LCD dùng font ma trận điểm 5×7 chuẩn HD44780 (đã đối chiếu từng chữ số 0,1,2,3,4,5,7,8 trên ảnh thật). Bộ đọc mới `mitutoyo_dotmatrix.py`: bỏ viền LCD → tách ô ký tự theo khe giữa ô → lấy mẫu 5×7 điểm từng ô → so khớp tuyệt đối với bảng font (kể cả `.` `,` `-`). Độ tin cậy chỉ xét phần số: khớp 100% điểm → 1.0; lệch ≤3 điểm và cách ký tự gần nhất ≥4 điểm → 0.8; còn lại → **không điền số**.
- **dynaROCK II**: Tesseract trên vùng số lớn + **xác minh hình học dấu thập phân** (phải thấy đúng 1 chấm nhỏ ở chân dòng); tối đa 0.8.
- Dấu `,` trên máy được chuẩn hóa thành `.` khi lưu; số chữ số thập phân giữ nguyên như màn hình.
- Kiểm thử hồi quy `test_golden.py` (10 ảnh, gồm ảnh gốc, ảnh đã chuẩn hóa, ảnh CQ cũ):

| Kết quả | Số ảnh |
|---|---|
| ĐÚNG (đúng số, đúng dấu) | 9 |
| TỪ CHỐI (không điền, bắt nhập tay) | 1 (mitu_full2_58.4 sau khi tăng tương phản; ảnh gốc của nó đọc đúng) |
| **SAI** | **0** |

Giới hạn còn lại: chưa có ảnh thật cho chữ số 6 và 9 (đang dùng đúng bảng font HD44780, chưa kiểm chứng trên ảnh thật); bộ ảnh thử còn nhỏ (6 ảnh máy). Vẫn bắt QC xác nhận từng số; OCR chạy trên ảnh gốc.

