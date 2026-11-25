# Vai trò và quyền (nguồn: PDF-3 §1.2, PDF-13 §11, SK §4.1)

Quyền được kiểm tra ở tầng Application (không chỉ ẩn nút giao diện).

| Quyền | admin | qc | kinh doanh (sales) | viewer |
|---|:-:|:-:|:-:|:-:|
| Xem dashboard, đơn, CQ, lịch sử | ✔ | ✔ | ✔ | ✔ |
| Mở PDF/file CQ, lưu bản sao | ✔ | ✔ | ✔ | ✔ |
| Import ERP (file, thư mục theo dõi, Quét ngay) | ✔ | ✔ | ✘ | ✘ |
| Sửa thông tin đơn, gộp/tách đơn | ✔ | ✔ | ✘ | ✘ |
| Thêm/sửa/xóa ảnh, cắt ảnh | ✔ | ✔ | ✘ | ✘ |
| Nhập kết quả đo, OCR, nhập CSV/Excel, xác nhận kết quả | ✔ | ✔ | ✘ | ✘ |
| Tạo CQ, đổi số báo cáo, duyệt CQ, đánh dấu đã in | ✔ | ✔ | ✘ | ✘ |
| Xóa CQ (bắt buộc lý do) | ✔ | ✔ (OQ-14) | ✘ | ✘ |
| Quản lý tài khoản, đặt lại mật khẩu, khóa tài khoản | ✔ | ✘ | ✘ | ✘ |
| Cài đặt nguồn dữ liệu, kho chung, template, bản đồ ô | ✔ | ✘ | ✘ | ✘ |
| Chữ ký, con dấu, người duyệt/kiểm tra mặc định | ✔ | ✘ | ✘ | ✘ |
| Backup, restore, reset dữ liệu | ✔ | ✘ | ✘ | ✘ |
| Gỡ khóa đơn/phiên cũ (stale) | ✔ | ✘ | ✘ | ✘ |
| Xử lý xung đột dữ liệu | ✔ | ✔ (chỉ đơn) | ✘ | ✘ |
| Đổi mật khẩu của tôi | ✔ | ✔ | ✔ | ✔ |
| Tùy biến giao diện cá nhân (ẩn/hiện, thứ tự cột) | ✔ | ✔ | ✔ | ✔ |

| Xem ảnh kiểm tra (ảnh lớn, ảnh gốc), lưu bản sao ảnh | ✔ | ✔ | ✔ | ✔ |
| Ghi LƯU Ý đơn hàng (kèm ảnh, nhắc ở đơn sau của khách) | ✔ | ✔ | ✔ | ✘ |
| Ẩn lưu ý (giữ lịch sử) | ✔ (mọi lưu ý) | của mình | của mình | ✘ |
| Tổng quan + xuất báo cáo Excel | ✔ | ✔ | ✔ | ✔ |

Quy tắc bổ sung:
- Tài khoản bị khóa (`Active=false`) không đăng nhập được; phiên đang mở bị kết thúc ở lần heartbeat kế tiếp.
- Ít nhất 1 admin đang hoạt động; không cho tự khóa/tự hạ quyền admin cuối cùng.
- Duyệt CQ: người thao tác phải là admin/qc; chữ ký người duyệt/kiểm tra chọn từ danh sách Signer do admin quản lý. Việc có ảnh chữ ký không tự động duyệt.
- Mật khẩu: PBKDF2-HMAC-SHA256, salt 16 byte, 210.000 vòng; tối thiểu 6 ký tự (PDF-4), khác mật khẩu mặc định; không ghi log mật khẩu.
