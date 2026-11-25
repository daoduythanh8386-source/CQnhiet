# ADR-001: Lưu trữ dùng chung bằng file bất biến trên UNC, không server

**Bối cảnh**: 6–10 máy; ADMIN-PC không bật 24/7; cấm SQLite chung trên SMB (SK §3); cần offline + nhiều máy, không ghi đè.
**Quyết định**: mỗi bản ghi là chuỗi version `vNNNNNN.json` tạo-không-ghi-đè; publish = ghi staging → kiểm hash → `File.Move` không ghi đè (CAS); blob theo SHA-256; nguồn sự kiện `events/`; khóa lease bằng `CreateNew`; outbox cục bộ; cache SQLite chỉ cục bộ.
**Lựa chọn đã loại**: SQLite/LiteDB trên UNC (hỏng khi nhiều máy ghi); API trên ADMIN-PC (không 24/7); đồng bộ hai chiều thư mục Synology Drive (không có transaction, dễ sinh bản conflict).
**Hệ quả**: không cần hạ tầng mới; xung đột được phát hiện chắc chắn, không mất dữ liệu; số file tăng dần (có thể nén lưu trữ version cũ sau này); phải xử lý xung đột thủ công; một-phiên/tài khoản không tuyệt đối khi kho offline.
**Rủi ro cần kiểm thử trên Windows thật**: tính nguyên tử của MoveFileEx không REPLACE và CreateNew qua SMB tới ADMIN-PC; độ trễ liệt kê thư mục; cache SMB client (dùng thăm dò lại trước commit).
