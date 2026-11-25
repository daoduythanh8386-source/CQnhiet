# CQ Nhiệt Luyện

Ứng dụng Windows (WPF, .NET 8) lập **Chứng nhận chất lượng (CQ) nhiệt luyện**: nhập đơn từ ERP, ảnh đo độ cứng / ảnh sản phẩm (đọc số tự động + kiểm soát OCR), tạo CQ theo template Excel, duyệt có dấu + chữ ký, xuất PDF. Nhiều máy dùng chung **một kho dữ liệu** trên thư mục chia sẻ của máy chủ ADMIN-PC.

© Đào Duy Thanh. Mọi quyền được bảo lưu.

## Lấy file cài (.exe) từ GitHub
1. Đẩy mã lên nhánh `main` (hoặc tab **Actions** → *Build bo cai CQ Nhiet Luyen* → **Run workflow**).
2. Chờ lần chạy có dấu ✓ xanh (khoảng 5–10 phút).
3. Bấm vào lần chạy → mục **Artifacts** → tải **CQNhietLuyen-Setup** (file zip chứa `CQNhietLuyen-Setup-<phiên bản>-x64.exe`).
4. Muốn có trang tải cố định: tạo tag `v0.9.2` → GitHub tự tạo **Release** kèm file cài.

Phiên bản lấy từ `Directory.Build.props` (`<Version>`).

## Cài và dùng trên các máy
Xem **[docs/HUONG_DAN_MAY_CHU_VA_CAI_DAT.md](docs/HUONG_DAN_MAY_CHU_VA_CAI_DAT.md)** — cài app, kết nối kho chung, cấu hình máy chủ chỉ cần bật nguồn, sao lưu / khôi phục.

## Cấu trúc mã nguồn
| Thư mục | Nội dung |
|---|---|
| `src/CqNl.Domain` | Quy tắc nghiệp vụ (đánh giá độ cứng, quy ước CQ) |
| `src/CqNl.Infrastructure` | Kho file dùng chung, đọc ERP, ghi Excel CQ, xuất PDF qua Excel, OCR, xử lý ảnh OpenCV |
| `src/CqNl.Application` | Dịch vụ: đơn, ảnh, CQ, tài khoản, đồng bộ offline |
| `src/CqNl.Wpf` | Giao diện |
| `tests/` | Kiểm thử (đầy đủ chạy trên ADMIN-PC có Excel: `build_test.ps1`) |
| `config/`, `sources/templates` | Bản đồ ô template, preset ảnh, template Excel đi kèm bộ cài |
| `installer/` | Bộ cài Inno Setup |
| `tools/` | Script cấu hình máy chủ / máy trạm |

**Không** đưa chữ ký, con dấu thật lên GitHub (`sources/signatures/` đã bị loại trong `.gitignore`) — quản trị thêm trong app: Cài đặt → Dấu & chữ ký.
