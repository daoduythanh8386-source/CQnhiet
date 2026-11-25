# Yêu cầu — CQ Nhiệt Luyện

Danh sách yêu cầu đầy đủ, có ID và nguồn: `source-traceability.md` (R-01…R-38). Điểm mơ hồ: `open-questions.md`. Tài liệu này tóm tắt phạm vi, tiêu chí nghiệm thu và yêu cầu phi chức năng.

## 1. Phạm vi
Ứng dụng desktop Windows x64 cho nhân viên QC nhiệt luyện: nhập đơn từ ERP MF0015, ảnh kiểm tra, kết quả đo độ cứng (tay/OCR gợi ý/CSV), tạo CQ Excel theo 3 template gốc, duyệt thành PDF có dấu/chữ ký, theo dõi in, lịch sử, dùng chung 6–10 máy qua thư mục UNC `\\ADMIN-PC\Users\Admin\SynologyDrive\DU LIEU NHIET`.

## 2. Hiện trạng khảo sát (13/11/2026)
| Hạng mục | Hiện trạng |
|---|---|
| Source code cũ (`CqApp.Wpf.exe` nhắc trong PDF-3) | **Không có trong phiên** (người dùng từ chối cấp quyền thư mục) → làm mới |
| Tài liệu PDF hướng dẫn | Có (bản ứng dụng cũ, tác giả Trương Ngọc Hùng) |
| 3 template | Có, đã phân tích; có hình vẽ, logo, dữ liệu rác ngoài vùng in |
| Bảng mác thép | Có; 4 mác trùng, 3 thiếu dung sai, 16 thiếu nhiệt độ |
| ERP mẫu | Có 2 định dạng (.xlsx 484 dòng; HTML-xls 17 dòng chồng lấn) |
| Ảnh máy đo | 2 Mitutoyo, 2 dynaROCK II, 1 CQ cũ; OCR thô thất bại; bộ đọc chuyên dụng (ma trận điểm Mitutoyo, dynaROCK có xác minh dấu thập phân): 9/10 đúng, 1 từ chối, 0 sai (research/ocr/benchmark.md) |
| Chữ ký / dấu | 2 chữ ký (Trương Ngọc Hùng, Đào Duy Thanh – nét bị cắt cuối "Thanh"), 1 dấu |
| Môi trường build | Chặn NuGet/.NET/npm/PyPI — **đang chờ người dùng mở mạng** |

## 3. Phi chức năng
- **An toàn dữ liệu**: không mất/ghi đè khi import, đồng bộ, xung đột, crash, nâng cấp. Ghi kiểu create-only + version; outbox chỉ xóa sau khi đọc lại và khớp hash.
- **Offline**: các thao tác nhập liệu làm được khi UNC mất; trạng thái hiển thị rõ.
- **Nhiều máy**: 6–10 máy; không có server 24/7; không dùng SQLite chung trên UNC.
- **Bảo mật**: PBKDF2; không lưu/log mật khẩu; audit đầy đủ (R-33).
- **Giao diện**: tiếng Việt, bàn phím (Enter mở đơn, Ctrl+V dán ảnh), DPI-aware, trạng thái loading/lỗi/rỗng, xác nhận thao tác nguy hiểm. Chân trang cố định (R-01).
- **Hiệu năng**: danh sách 10.000 dòng lọc < 1 s (bộ nhớ đệm cục bộ).
- **Cài đặt**: installer x64 tự chứa .NET; gỡ không xóa `%LocalAppData%\CQNhietLuyen` và kho chung.

## 4. Tiêu chí nghiệm thu (theo SK §8)
1. Các luồng R-07…R-33 chạy thật, không nút giả, không dữ liệu demo.
2. Import ERP-X rồi ERP-H: dòng chồng lấn chỉ cập nhật trường ERP; kết quả đo/ảnh/CQ giữ nguyên (test tự động).
3. Ba template sinh CQ: so sánh XML trước/sau chỉ khác các ô đã map + ảnh thêm vào drawing; render PDF 1 trang, không tràn.
4. Hai máy sửa cùng đơn khi offline → xung đột được giữ cả hai bản (test tự động mô phỏng 2 client trên cùng thư mục).
5. Khóa stale sau crash được thu hồi theo lease, có audit.
6. Backup → restore vào thư mục trống → kiểm tra toàn vẹn khớp hash.
7. Installer tạo được; cài thử nếu có Windows.
