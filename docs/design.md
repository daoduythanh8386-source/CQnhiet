# Thiết kế — CQ Nhiệt Luyện

## 1. Kiến trúc
```
CqNl.Domain          (net8.0)  thực thể, quy tắc: dung sai, đánh giá, gộp đơn, số báo cáo, trạng thái
CqNl.Application     (net8.0)  use case, phân quyền, import ERP, CQ, outbox, giao diện cổng (interfaces)
CqNl.Infrastructure  (net8.0 / net8.0-windows) kho UNC, cache SQLite cục bộ, đọc ERP, OpenXML, ảnh, OCR, PDF
CqNl.Wpf             (net8.0-windows) MVVM, không chứa logic nghiệp vụ
tests/*              xUnit: Domain, Application, Infrastructure (kho mô phỏng 2 máy), Template fidelity
installer/           Inno Setup (.iss)
```

## 2. Màn hình (theo PDF-4…16)
Đăng nhập → Đặt mật khẩu mới (lần đầu) → Shell: thanh trên (người dùng, vai trò, **trạng thái ONLINE/OFFLINE**, số mục chờ, xung đột, Đồng bộ ngay), menu trái: Dashboard · Đơn gia công · Lịch sử CQ · Đang online · Cài đặt & Template (Nguồn dữ liệu, Dùng chung, Tài khoản, Dấu & chữ ký, Template, Backup, Nhật ký) · chân trang cố định.
Chi tiết đơn: 4 tab Thông tin / Ảnh / Kết quả đo / Xuất CQ; thanh khóa "Đang sửa bởi …".

## 3. Mô hình dữ liệu (aggregate)
- `Order` (khóa `Dngc|LineNo`): `Erp{…}` (chỉ import ghi), `Manual{PartNo, PartName, Size, CustomerOverride…}`, `SteelGrade`, `TemplateId`, `HardnessRequirement`, `Merge{ParentKey | ChildKeys}`, `Images[]`, `Hardness{…}`, `Flags`.
- `CqVersion` (khóa `OrderKey|Ver`): ReportNo, TemplateId+sha, XlsxBlob, Result, Created, Approval{ApproverSignerId, CheckerSignerId, PdfBlob, ApprovedXlsxBlob}, Printed, Deleted{Reason}.
- `User`, `Signer`, `SharedSettings` (template, mapping, mặc định), `AuditEvent`, `ImportLog`.
Mỗi bản ghi có `Meta{Version, PrevSha, Sha256, UpdatedBy, UpdatedAt, Machine, OpId}`.

## 4. Lưu trữ UNC, offline và đồng bộ (ADR-001)
Gốc: `<UNC>\CQNL\` (thư mục con trong kho đã chốt, có `store.json` đánh dấu định dạng).
```
records/<type>/<id>/v000001.json …   bất biến, tạo-không-ghi-đè
blobs/<aa>/<sha256>.<ext>             ảnh, xlsx, pdf – địa chỉ theo nội dung
events/<yyyyMMdd>/<ticks>-<machine>-<uuid>.json   nguồn thay đổi để client cập nhật cache
audit/<yyyyMM>/…                      nhật ký bất biến
counters/reportno/<yyyy-MM>/<NNN>.json   giữ số báo cáo (tạo-không-ghi-đè)
locks/<type>-<id>/<thế hệ>.json        khóa lease theo đơn (mỗi lần giành khóa = 1 file thế hệ mới)
sessions/<user>.lease                 phiên đăng nhập
conflicts/<type>/<id>/<uuid>.json     bản xung đột (giữ cả hai)
staging/<machine>/                    file tạm trước khi publish
```
**Ghi (commit)**: đọc version mới nhất N → ghi file tạm trong `staging/` → flush + đọc lại kiểm tra SHA-256 → `File.Move(tmp, v{N+1}.json)` **không ghi đè**. Nếu file đích đã có (máy khác vừa ghi) → không ghi đè, chuyển thành xung đột. Đây là compare-and-swap trên SMB; không cần server.
**Blob**: ghi tạm → kiểm hash → move không ghi đè; đã tồn tại cùng hash = thành công (idempotent).
**Idempotent**: mỗi thao tác có `OpId` (UUID) ghi trong `Meta`; trước khi commit lại sau lỗi, kiểm tra version mới nhất có `OpId` đó chưa.
**Outbox cục bộ** `%LocalAppData%\CQNhietLuyen\outbox\`: mỗi mục {OpId, thời điểm, user, máy, loại, recordId, BaseVersion, payload, blobs}. Đẩy theo thứ tự tạo; blob trước bản ghi; retry backoff 5 s→5 phút. Chỉ xóa mục sau khi đọc lại version trên UNC và khớp SHA-256. BaseVersion ≠ mới nhất → ghi `conflicts/` (bản của mình + tham chiếu bản mới nhất), mục outbox chuyển "Xung đột", **không tự chọn bản mới nhất**.
**Cache cục bộ**: SQLite trong `%LocalAppData%` (chỉ 1 máy dùng) để lọc/tìm nhanh; dựng lại được hoàn toàn từ kho. Cập nhật theo `events/` + quét toàn bộ định kỳ.
**Khóa đơn**: giành khóa = tạo file thế hệ kế tiếp `locks/<key>/<n+1>.json` bằng `FileMode.CreateNew` (nguyên tử; chỉ 1 máy thắng) chứa user, máy, lúc tạo, hết hạn (TTL 5 phút, gia hạn mỗi 60 s bằng cách ghi lại file của chính mình). Nhả khóa = đánh dấu `released`. Khóa hết hạn + 2 phút → máy khác giành bằng thế hệ mới (có audit) — **không đổi tên/xóa khóa của người khác** (bản đầu dùng đổi tên sang `stale/` có race: có thể thu hồi nhầm khóa vừa được tạo; đã sửa, có test 10 tiến trình). Thời gian so theo đồng hồ máy chủ (đo lệch bằng file thăm dò). Khóa chỉ là tư vấn; CAS version mới là chốt chặn cuối.
**Phiên**: `sessions/<user>.lease` cùng cơ chế (TTL 3 phút). Đăng nhập online: lease còn hạn ở máy khác → từ chối. Đăng nhập offline: dùng bộ xác minh mật khẩu đã cache, banner "Không kiểm tra được phiên trùng"; khi online lại phát hiện trùng → cảnh báo + audit. **Giới hạn**: khi ADMIN-PC tắt, không bảo đảm một-phiên toàn mạng; muốn tuyệt đối cần dịch vụ 24/7 (OQ-13).
**Cần online**: cấp số báo cáo, tạo/duyệt/xóa CQ, quản lý tài khoản, cài đặt dùng chung, import ERP vào kho, backup/restore kho.
**Synology Drive**: chế độ hỗ trợ là truy cập UNC trực tiếp. Nếu phát hiện file kiểu "conflict copy" của Synology trong `records/` → đưa vào danh sách xung đột.

## 5. Excel CQ (ADR-003)
Sao chép template (đã kiểm hash) vào thư mục tạm → mở bằng OpenXML SDK → sửa **chỉ** phần tử `<c>` của ô đã map (inline string/number giữ style index), bỏ ẩn dòng theo OQ-07, thêm `<xdr:twoCellAnchor>` ảnh vào **drawing có sẵn** (giữ hình vẽ, logo) với tỉ lệ khít khung → lưu → blob. Bản đồ ô: `config/template-maps/*.json`; trình sửa bản đồ từ chối ô không phải ô trên-trái vùng gộp. Test fidelity: so sánh mọi part ZIP trước/sau, chỉ cho phép khác ở sheet1.xml (ô đã map, thuộc tính hidden) và drawing/rels/media/[Content_Types].

## 6. PDF (ADR-004)
Duyệt: tạo bản xlsx đã chèn dấu + chữ ký (blob mới), xuất PDF bằng Excel (COM, late-bound) nếu có, ngược lại LibreOffice `soffice --headless --convert-to pdf`. Kiểm tra: số trang = 1 (cấu hình), file > 0, có ảnh; lỗi → không chuyển trạng thái Duyệt.

## 7. Ảnh & OCR (ADR-005)
Gốc bất biến (blob). Bản xử lý = danh sách thao tác (crop rect, rotate, perspective 4 điểm, brightness/contrast, denoise, sharpen) + blob kết quả; "Bỏ cắt" = về gốc. Tự phân loại: phát hiện LCD Mitutoyo (vùng vàng-xanh) / màn dynaROCK II (chữ số xanh lớn) → "Ảnh kết quả đo". OCR theo profile máy trên ROI, whitelist `0-9.`, regex `^\d{1,3}\.\d$`, 20–70 HRC; độ tin cậy < ngưỡng → để trống. Mỗi điểm lưu `OcrRaw`, `OcrConfidence`, `ImageId+ROI`, `ConfirmedValue`, `ConfirmedBy/At`. Chưa xác nhận hết → không tính đánh giá, không tạo CQ.

### 7.1 Chuẩn hóa ảnh máy đo cho CQ (preset `config/image-presets/mitutoyo-cq.json`, theo ảnh mẫu người dùng 13/11/2026)
1. Tìm LCD (vùng vàng-xanh). Không thấy hoặc tỉ lệ LCD bất thường → không tự cắt, yêu cầu cắt tay.
2. Khung cắt theo tâm và bề rộng LCD `w`: trái/phải 2.0w, trên 1.0w, dưới 2.23w (khung 4.0w × 3.23w, tỉ lệ ≈ ảnh mẫu 1897×1528). Ảnh chụp sát → thu đều 4 lề hệ số k để giữ tỉ lệ; k < 0.75 → cảnh báo "cần cắt tay".
3. Cân màu về tỉ lệ B:G:R của ảnh mẫu; kéo giãn mức sáng 1–99%; gamma tự chọn để độ sáng TB ≈ 140 (giới hạn 0.6–1.2); bão hòa ×1.35; làm nét nhẹ (unsharp 0.5, σ2).
4. Ghi ảnh mới (JPEG 92, rộng ≤ 1600 px); ảnh gốc không đổi; "Bỏ cắt" trả về gốc; mọi tham số sửa được trong Settings (preset).
5. OCR chạy trên ảnh gốc, không chạy trên ảnh đã tăng tương phản.

### 7.2 Ảnh sản phẩm (ẢNH XUẤT HÀNG) — preset `san-pham-cq.json` (ảnh mẫu người dùng 13/11/2026)
Khung = bao các vật kim loại sáng không chạm mép ảnh (loại sàn, kệ) và không chứa LED đỏ (đầu cân), nới lề trái 9%, phải 12%, trên 9%, dưới 8%. Thử trên ảnh gốc: khung tự động (98,305)-(933,1198) so với khung người dùng cắt tay (100,305)-(937,1191), lệch ≤ 7 px. Cân màu theo ảnh mẫu, giãn mức sáng 3–99.9% với chừa sáng (trắng = 225, tránh cháy mặt kim loại), bão hòa ×0.75, làm nét 0.8. Vùng < 15% hoặc > 95% ảnh, hoặc < 3 vật → "cần cắt tay".

### 7.3 Chữ ký — preset `chu-ky.json` (quy ước người dùng 13/11/2026)
Mọi ảnh chữ ký đưa vào app: làm phẳng nền (khử bóng), nền đổi thành màu giấy A4 đều (BGR 245,248,250), bỏ đốm bụi, giữ màu mực (đậm thêm), làm nét nét chữ, cắt sát + lề 4%. Ảnh gốc giữ nguyên. Đồng thời sinh bản trong suốt cùng nét mực. **Chờ người dùng chọn** bản nào chèn lên CQ (OQ-18): bản nền giấy lộ khung xám nhạt trên CQ in giấy trắng và có thể che dấu đỏ khi chồng lên dấu.

### 7.4 Nhiều ảnh độ cứng, bố cục tự co giãn (yêu cầu người dùng 13/11/2026)
Tab Ảnh có nút **"+ Thêm ảnh đo độ cứng"** (chọn tệp / kéo thả / Ctrl+V), sắp thứ tự kéo-thả, tối đa 8 ảnh (cấu hình theo template, `maxImages`). Khi tạo CQ: ảnh chia lần lượt vào các khung ẢNH ĐỘ CỨNG có sẵn (2 khung), trong mỗi khung xếp lưới tự chọn số cột để ảnh nhỏ nhất vẫn to nhất, giữ tỉ lệ, căn giữa. Không sửa ô/dòng/vùng in của template ⇒ CQ luôn **1 trang A4** (đã thử 5 ảnh: 1 trang). Vượt giới hạn → báo lỗi, không tạo CQ. Áp dụng tương tự cho ảnh xuất hàng (tối đa 4).

### 7.5 Biểu đồ TÔI/RAM theo dữ liệu đầu vào
Giá trị lấy từ bảng mác (Gia nhiệt 1–4, Ram 1–3) làm mặc định của đơn; QC sửa được trong tab Thông tin đơn (có audit). Khi tạo CQ: carbon ghi D15/H14 + thay 3 chữ "180min" trong hình vẽ; khuôn thay 650/850/1030℃ và 3 chữ "180p" trong hình vẽ; gió thay 650/850/1000/1180℃ trong hình vẽ và ô N16/R16/U16. Chỉ thay khi chữ trong hình vẽ khớp đúng bản gốc (nếu template bị đổi → báo lỗi, không đoán). Đã thử: SK5 → 800℃, RAM 150p/180p/200p hiển thị đúng.

### 7.6 Dấu và chữ ký đặt tự do
Con dấu SAO PHƯƠNG ĐÔNG di chuyển được **khắp biểu mẫu**; chữ ký người duyệt/kiểm tra chỉnh được vị trí và cỡ theo người làm. Hộp thoại Duyệt CQ hiển thị ảnh trang CQ: kéo-thả, kéo góc để đổi cỡ (giữ tỉ lệ), nút "Về mặc định". Lưu vị trí dạng (ô neo + lệch mm + rộng mm) → ghi bằng neo ảnh tự do (oneCellAnchor), không phụ thuộc vùng gộp. Vị trí mặc định lưu theo người ký + template (mỗi người tự đặt); vị trí dùng khi duyệt được ghi vào bản CQ đã duyệt và **bất biến** cùng PDF. Cảnh báo (không chặn) nếu dấu/chữ ký ra ngoài vùng in.

### 7.7 Số cân trên ảnh xuất hàng (yêu cầu người dùng 13/11/2026)
Khi thêm ảnh xuất hàng, app đọc số trên **màn hình LED đỏ của đầu cân trong ẢNH GỐC** (trước khi cắt — ảnh đã chuẩn hóa không còn đầu cân), giữ đúng chữ số và dấu thập phân như màn hình (vd `41.40`, `135.2`). Kết quả là gợi ý (tối đa 0.8) → QC xác nhận/sửa ô "Số cân trên ảnh". Khi tạo CQ, bản sao ảnh trong CQ có ô chú thích **góc dưới bên trái**: chữ đen đậm, nền trắng, viền đen mảnh; cỡ tính theo tỉ lệ in của từng template để ô chữ cao ≈ 4 mm trên A4 (đã đo: carbon 4.3 mm, khuôn 3.9 mm, gió 4.0 mm; ảnh xám 150 dpi vẫn đọc rõ). Chưa xác nhận số → không ghi chú, cảnh báo khi tạo CQ. Ảnh gốc không đổi. Có thể đối chiếu với "Trọng lượng (kg)" của ERP và cảnh báo nếu lệch (không chặn) — chờ xác nhận OQ-21.

## 8. Backup
Backup = sao chép `CQNL` (trừ staging/locks/sessions) sang `CQNL\backup\<yyyyMMdd-HHmmss>\` hoặc thư mục chọn + `manifest.json` (SHA-256 từng file); kiểm tra = tính lại hash. Restore chỉ vào thư mục trống rồi đổi đường dẫn (không ghi đè kho đang chạy). Reset/migration luôn backup trước.

## 9. Audit
Sự kiện bất biến: đăng nhập/đăng xuất/thất bại, sửa đơn (trường cũ→mới), import, xác nhận đo, CQ tạo/duyệt/in/xóa/đổi số, cấu hình, khóa/thu hồi, đồng bộ, xung đột & cách xử lý.
