# Truy vết nguồn

Mã nguồn dùng trong tài liệu:

| Mã | Nguồn | Ghi chú |
|---|---|---|
| SK | Skill `cq-nhiet-luyen-builder` (hồ sơ dự án đã chốt) | Ưu tiên cao nhất cho UNC, đồng bộ, bảo mật, chân ứng dụng |
| PDF-x | `HuongDan_CQ_NhietLuyen_1.pdf`, trang x (17 trang, tác giả file: Trương Ngọc Hùng, 07/11/2026) | Hướng dẫn của bản ứng dụng cũ; hình là ảnh mô phỏng |
| TPL-C / TPL-K / TPL-G | `carbon.xlsx` / `khuon.xlsx` / `gio.xlsx` | Phân tích trực tiếp XML + render LibreOffice |
| SG | `steel_grades.xlsx`, sheet "THÔNG SỐ LĐ NHIỆT" | Bảng mác thép, nhiệt độ, dung sai |
| ERP-X | `sample_erp_MF0015.xlsx` (484 dòng, 01–31/10/2026) | |
| ERP-H | `sample_erp_MF0015_html.xls` (HTML do Oracle BI Publisher xuất, 17 dòng) | Định dạng thứ 2 của ERP |
| IMG-* | Ảnh: mitu1, mitu_full2, dyna1, dyna2, ocr_sample_machine (CQ cũ), approver, checker, stamp | |
| U-2 | Người dùng (13/11/2026): ảnh mẫu đã cắt + làm sáng và ảnh gốc, yêu cầu lệnh chuẩn hóa ảnh như mẫu | |
| U-3 | Người dùng: ảnh sản phẩm mẫu đã chỉnh + ảnh gốc (13/11/2026) | |
| U-4 | Người dùng: quy ước chữ ký nền giấy A4 + làm nét; ảnh chữ ký mới Đào Duy Thanh | |
| U-5 | Người dùng: thêm ảnh đo độ cứng, biểu đồ tôi ram theo đầu vào, đọc đúng số và dấu | |
| U-6 | Người dùng: con dấu di chuyển khắp biểu mẫu, chữ ký tùy chỉnh theo người làm | |
| U-7 | Người dùng: sắp xếp ảnh theo bảng ô từ số nhỏ đến lớn | |
| U-8 | Người dùng: ảnh trùng kết quả chỉ lấy 1 khi CQ có trên 2 ảnh | |
| U-9 | Người dùng: ghi số cân góc dưới trái ảnh xuất hàng | |
| U-1 | Người dùng (13/11/2026): chọn .NET 8 WPF, sẽ mở mạng NuGet/.NET | |

## Ma trận yêu cầu → nguồn

| ID | Yêu cầu | Nguồn |
|---|---|---|
| R-01 | Tên "CQ Nhiệt Luyện"; chân trang cố định "CQ NHIỆT LUYỆN \| Ứng dụng được xây dựng bởi Đào Duy Thanh \| © Đào Duy Thanh. Mọi quyền được bảo lưu." | SK §2 |
| R-02 | Kho chung mặc định `\\ADMIN-PC\Users\Admin\SynologyDrive\DU LIEU NHIET`, chọn/kiểm tra trong Settings, không tự đổi | SK §2 |
| R-03 | Làm việc offline với outbox, tự đồng bộ, không fallback âm thầm | SK §2, §3 |
| R-04 | Cấm nhiều máy ghi chung 1 file SQLite trên UNC | SK §3 |
| R-05 | Một phiên/tài khoản; danh sách online (tên máy, giờ đăng nhập); nói rõ giới hạn khi offline | SK §2 |
| R-06 | Khóa theo đơn có lease; xung đột giữ cả hai bản | SK §3; PDF-14 §12.2 bước 4 |
| R-07 | Vai trò admin/qc/viewer với quyền như bảng | PDF-3 §1.2 |
| R-08 | Đăng nhập, bắt đổi mật khẩu lần đầu (≥6 ký tự, khác mặc định), đổi mật khẩu của tôi | PDF-3/4 §3 |
| R-09 | Quản lý tài khoản: tạo, vai trò, đặt lại mật khẩu tạm, khóa (bỏ "Đang dùng"), cột "Chờ đổi lần đầu" | PDF-13 §11 |
| R-10 | Dashboard: thống kê tổng/chờ kiểm/đã tạo CQ/đạt/không đạt, tìm nhanh, mở đơn | PDF-4/5 §4 |
| R-11 | Import MF0015 .xlsx: báo thêm/cập nhật/bỏ qua; chỉ cập nhật trường ERP; giữ đo/ảnh/CQ; không tự sửa CQ cũ | PDF-5 §4.1; SK §4.3 |
| R-12 | Xem trước + báo lỗi + "từ chối" khi import | SK §4.3 |
| R-13 | Thư mục theo dõi ERP tự nhập + "Quét ngay"; nhật ký đồng bộ ERP | PDF-5, PDF-14 §12.1, PDF-16 |
| R-14 | Danh sách đơn: khóa = Số ĐNGC + Dòng; nền xanh khi có CQ; lọc trạng thái, lọc in, tìm kiếm, lọc kiểu Excel theo cột, ẩn/hiện cột (ghi nhớ), wrap text; quay lại giữ bộ lọc & vị trí | PDF-5..7 §5 |
| R-15 | Gộp đơn cùng ĐNGC + mác + yêu cầu độ cứng, chưa có CQ; cộng dồn SL/KL; tách khi chưa có CQ; dòng con đồng bộ trạng thái | PDF-7 §5.4, PDF-16 |
| R-16 | Tab Thông tin đơn: sửa trường ERP, mác (tra bảng), loại template (gợi ý theo mác), yêu cầu độ cứng, Mã QL/Tên chi tiết/Kích thước nhập tay; Số ĐNGC chỉ đọc; trường nhập tay không bị ERP ghi đè | PDF-7/8 §6 |
| R-17 | Ảnh: chọn tệp, kéo thả, Ctrl+V, dải xem nhanh clipboard/Tải xuống; loại ảnh (mẫu/kết quả đo/khác); khung CQ; cắt tay/bỏ cắt; xóa; tự phân loại; auto-crop Mitutoyo | PDF-8/9 §7; SK §4.5 |
| R-18 | Ảnh gốc bất biến; bản xử lý riêng | SK §5 |
| R-19 | Đo độ cứng: đơn vị, ngày kiểm, ngoại quan OK/NG, độ cong yêu cầu/kết quả; Mẫu 1–6 nhiều điểm (khoảng trắng/;); OCR; nhập CSV/Excel; Xác nhận; đánh giá ĐẠT/KHÔNG ĐẠT, giới hạn, Min/Max/TB; tự lưu | PDF-9/10 §8 |
| R-20 | OCR chỉ gợi ý; lưu giá trị OCR, giá trị xác nhận, người, thời điểm | SK §4.6 |
| R-21 | Nhận xét CQ: "Chất lượng đạt" / "Không đạt độ cứng" + ghi chú; ô trống hiển thị "-" | PDF-10 §8.4 |
| R-22 | Tạo file CQ: kiểm tra đủ dữ liệu; mỗi lần là Ver mới; bản cũ giữ | PDF-10/11 §9.1 |
| R-23 | Duyệt: chọn người duyệt/kiểm tra (mặc định theo cài đặt) → PDF có dấu & chữ ký; CQ đã duyệt bất biến | PDF-11 §9.2; SK §4.8 |
| R-24 | Số báo cáo NNN/MM tự đánh theo tháng; đổi số = tạo version mới; cảnh báo trùng | PDF-11 §9.3; TPL ô "Số báo cáo: (NNN/MM)" |
| R-25 | Đánh dấu Đã in: ghi người & thời điểm; lọc Chưa in/Đã in | PDF-11/12 §9.4 |
| R-26 | Xóa CQ: bắt buộc lý do; ghi Lịch sử xóa; đơn về Chờ kiểm tra nếu hết CQ | PDF-12 §9.5 |
| R-27 | Trang Lịch sử CQ: tab Danh sách / Lịch sử xóa, tìm kiếm, mở file/PDF/thư mục, lưu bản sao | PDF-12 §10 |
| R-28 | Cài đặt: file mác thép, thư mục template, thư mục ERP, thư mục dữ liệu (đổi + chép), dùng chung | PDF-14 §12.1 |
| R-29 | Dấu & chữ ký: thêm/đổi tên/xóa chữ ký, nền trắng → trong suốt (bản xử lý riêng, giữ gốc), người duyệt/kiểm tra mặc định, đổi dấu/khôi phục | PDF-15 §12.3; SK §5 |
| R-30 | Template: 3 mặc định + thêm template, Sửa bản đồ ô (báo lỗi nếu ô nằm trong vùng gộp nhưng không phải ô trên-trái), không sửa file gốc | PDF-15/16 §12.4; SK §5 |
| R-31 | Reset dữ liệu: tự backup trước | PDF-16; SK §4.9 |
| R-32 | Backup/restore, kiểm tra toàn vẹn, thử khôi phục | SK §3 |
| R-33 | Audit: đăng nhập, sửa dữ liệu, import, xác nhận đo, tạo/duyệt/in/xóa CQ, cấu hình, đồng bộ | SK §3 |
| R-34 | Installer Windows x64; gỡ không xóa dữ liệu | SK §6 |
| R-35 | PDF: dùng Excel hoặc LibreOffice trên máy | PDF-3 mẹo, PDF-16 |
| R-36 | Đánh giá theo dung sai trong bảng mác (cột "Dung sai độ cứng") | SG cột K; IMG ocr_sample_machine ("58-62 (HRC) MIN -0.7") |
| R-37 | Gợi ý template theo mác: cột "ĐÁNH DẤU" 1=Khuôn, 2=Gió, 3=Carbon | SG cột M + nhóm "Phân loại thép" (suy luận từ nhóm, cần xác nhận → OQ-05) |
| R-38 | Hỗ trợ ERP dạng HTML-xls (Oracle BI) ngoài .xlsx | ERP-H (người dùng cung cấp) |
| R-39 | Chuẩn hóa ảnh máy đo Mitutoyo trước khi đưa vào CQ: cắt quanh mặt máy + làm sáng như ảnh mẫu | U-2: ảnh mẫu `ref_mitu_cropped_50.1.jpg` + ảnh gốc `mitu_goc_38.2.jpg` (13/11/2026) |
| R-40 | Chuẩn hóa ảnh sản phẩm: cắt vùng sản phẩm (bỏ đầu cân/kệ), làm sáng, làm nét như ảnh mẫu | U-3: `ref_sanpham_da_chinh.jpg` + `sanpham_goc.jpg` |
| R-41 | Mọi chữ ký: nền sáng màu giấy A4, làm nét chữ ký; thêm chữ ký mới Đào Duy Thanh (bản đầy đủ) | U-4: tin nhắn 13/11/2026 + `thanh_moi_goc.jpg` |
| R-42 | Nút thêm ảnh đo độ cứng; nhiều ảnh trong khung CQ, tự co giãn vừa 1 trang A4 | U-5 (13/11/2026) |
| R-43 | Biểu đồ TÔI/RAM điền theo dữ liệu đầu vào khi thay đổi | U-5 |
| R-44 | Đọc đúng số đo độ cứng kể cả dấu chấm/phẩy | U-5 |
| R-45 | Con dấu di chuyển khắp biểu mẫu; chữ ký tùy chỉnh theo người làm | U-6 (13/11/2026) |
| R-46 | Ảnh độ cứng xếp bảng ô lần lượt, từ số nhỏ nhất đến lớn nhất, dễ nhận diện | U-7 (13/11/2026) |
| R-47 | Từ 3 ảnh trở lên: kết quả đồng hồ trùng (cùng đơn, khách, mác) chỉ lấy 1 ảnh | U-8 (13/11/2026) |
| R-48 | Ảnh xuất hàng: đọc số đồng hồ cân trên ảnh gốc, ghi nhỏ góc dưới trái, in A4 đen trắng vẫn rõ | U-9 (13/11/2026) |

