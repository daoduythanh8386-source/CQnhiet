# Luồng nghiệp vụ và trạng thái

## Quy trình 7 bước (PDF-3 §1.1)
1. Đồng bộ đơn từ ERP (xem trước → xác nhận nhập).
2. Mở đơn, kiểm tra thông tin, mác, template, yêu cầu độ cứng.
3. Thêm ảnh: ảnh mẫu, ảnh máy đo; gán khung CQ.
4. Nhập kết quả đo (tay/OCR gợi ý/CSV) → QC xác nhận từng giá trị OCR → **Xác nhận kết quả**.
5. Tạo file CQ (Ver mới, cấp số báo cáo — cần kho chung online).
6. Duyệt CQ: chọn người duyệt + kiểm tra → PDF có dấu, chữ ký → CQ bất biến.
7. In, tích "Đã in".

## Trạng thái đơn
```
ChoKiemTra ──(xác nhận kết quả)──▶ DaXacNhan ──(tạo CQ)──▶ CoCQ ──(duyệt)──▶ DaDuyet ──(in)──▶ DaIn
     ▲                     │ sửa kết quả sau xác nhận → huỷ xác nhận (audit)        │
     └──────────── xoá CQ cuối cùng (bắt buộc lý do) ◀──────────────────────────────┘
```
- Sửa kết quả đo sau khi đã có CQ: CQ cũ giữ nguyên, gắn cảnh báo "Kết quả đã thay đổi sau khi tạo CQ"; phải tạo Ver mới.
- ERP cập nhật đơn đã có CQ: chỉ đổi trường ERP, CQ không đổi, gắn cảnh báo "ERP thay đổi sau CQ" (PDF-5 lưu ý).
- Đơn gộp: thao tác trên dòng đại diện; trạng thái lan xuống dòng con. Tách chỉ khi chưa có CQ.

## Trạng thái CQ version
`Draft(xlsx đã tạo)` → `Approved(PDF đã ký)` → (`Printed` cờ) ; `Deleted` (đánh dấu, có lý do, file giữ trong lưu trữ).
- Không sửa file của một version. Đổi số báo cáo = version mới.
- Cảnh báo cột: Trùng số báo cáo; Dữ liệu chưa đồng bộ; ERP/kết quả thay đổi sau CQ; Template đã đổi (hash khác).

## Luồng import ERP
Chọn file / thư mục theo dõi → đọc (xlsx hoặc HTML-xls) → kiểm tra từng dòng → **màn xem trước**: Thêm / Cập nhật (liệt kê trường đổi) / Không đổi (bỏ qua) / Từ chối (lý do) / Cảnh báo (mác không khớp, yêu cầu độ cứng không hợp lệ) → người dùng xác nhận → ghi (mỗi đơn 1 commit có version) → nhật ký import. Thư mục theo dõi: file mới ổn định kích thước 3 giây mới đọc; hash file để không nhập lặp.

## Luồng offline / đồng bộ (tóm tắt — chi tiết design.md §4)
Mất UNC → thanh trạng thái **OFFLINE** (nguyên nhân, lần đồng bộ thành công cuối, số mục chờ). Vẫn được: sửa thông tin đơn, thêm ảnh, nhập/xác nhận kết quả (ghi outbox). Không được: tạo số báo cáo/CQ chính thức, duyệt, xóa CQ, quản lý tài khoản, import ERP vào kho chung (có thể xem trước). Có mạng lại → đẩy outbox theo thứ tự; xung đột → danh sách "Xung đột cần xử lý".
