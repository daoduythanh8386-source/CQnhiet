# Từ điển dữ liệu

## 1. Nguồn ERP MF0015 (ERP-X `.xlsx`, ERP-H HTML-xls)

Tiêu đề 3 dòng (dòng 4–6), dữ liệu từ dòng 7 (xlsx). Bản HTML: 1 bảng, 3 dòng tiêu đề sau dòng tiêu đề báo cáo. Nhận diện cột **theo tên tiêu đề**, không theo vị trí.

| Cột ERP | Trường | Kiểu | Ghi chú/kiểm tra |
|---|---|---|---|
| Số ĐNGC | `Dngc` | text, bắt buộc | dạng `DNGC/HY3-yyMMnnnn` |
| Dòng | `LineNo` | số nguyên, bắt buộc | **Khóa đơn = Dngc + LineNo** (kiểm tra: 484 dòng mẫu không trùng) |
| Ngày nhận ĐNGC | `ReceivedAt` | ngày giờ | xlsx: kiểu ngày; HTML: `dd/MM/yyyy HH:mm:ss` |
| Số đơn hàng | `SalesOrderNo` | text | |
| Khách hàng | `Customer` | text | |
| Sản phẩm | `ProductCode` | text | `THEPHOPKIM_SKD11 SP` → mác = phần giữa `_` và ` SP` |
| Tên sản phẩm | `ProductName` | text | |
| KT1–KT4, DS1–DS4 | `Kt1..4`, `Ds1..4` | text | trống ở toàn bộ dữ liệu mẫu |
| Độ cứng | `HardnessRequirementErp` | text | xem §1.1 |
| Ghi chú (cột R) | `ErpNote` | text | có khi chứa kích thước/mã khuôn — **không** tự chép sang Kích thước |
| Số lượng | `Quantity` | số | |
| Trọng lượng | `WeightKg` | số thập phân | HTML có dạng `.5` |
| Công đoạn, Phương thức gia công, Số phiếu GC, Số lệnh SX, Trạng thái LSX, Lot phôi, SL/KL xuất, SL/KL hoàn thành, thời gian… | lưu nguyên trong `ErpExtra` | | chỉ hiển thị, không dùng cho CQ |

### 1.1 Chất lượng dữ liệu "Độ cứng" (ERP-X, 484 dòng)
- 91 dòng trống; 3 dòng `MAX`; 1 dòng là ô ngày (Excel đã tự đổi, ví dụ `04/07/2026`); 1 dòng số `65.67`; 1 dòng `49-91` (nghi gõ sai); 1 dòng HV `Bề măt: 350-450hv; Lõi: 250-320HV`.
- Quy tắc: chỉ chấp nhận tự động dạng `a-b` với a<b, 0<a,b≤70 (HRC). Còn lại: **vẫn nhập đơn** nhưng đánh dấu "Yêu cầu độ cứng cần nhập tay", không tự đoán; ô ngày bị Excel đổi → không tự chuyển ngược.

### 1.2 Mác thép từ ERP
- Mác không có trong bảng: `THEPHOPKIM SP` (36 dòng, không có mác), `S-Star` (33 – bảng ghi `S STAR`), `STKM 13`/`STKM 590`, `NHOM_A6063`, `THEPCACBON SP`.
- So khớp chuẩn hóa: bỏ khoảng trắng, `-`, `_`, `.`, so không phân biệt hoa thường (`S-Star`→`SSTAR` = `S STAR`). Khớp chuẩn hóa được hiển thị "khớp gần đúng" để QC xác nhận. Không khớp → để trống mác, QC chọn tay.

## 2. Bảng mác thép (SG)
Dòng tiêu đề 4–7, dữ liệu dòng 8–232. Cột: A Phân loại (ô gộp theo nhóm), B STT, C Mác, D–G Gia nhiệt 1–4 (Tôi), H–J Ram 1–3, K Dung sai độ cứng, L Thành phần hóa, M Đánh dấu (1/2/3).

Vấn đề đã phát hiện (ứng dụng phải báo khi nạp bảng, không tự chọn):

| Vấn đề | Dòng |
|---|---|
| Trùng mác, **khác giá trị**: `M390` (dòng 71 nhóm Gió, 1000/1180℃, MAX +0.5; dòng 134 nhóm Khuôn, 1030℃, (MAX-MIN)±1) | 71, 134 |
| Trùng mác, khác nhiệt độ: `SCM420` (850℃ vs 920℃) | 98, 109 |
| Trùng mác, cùng giá trị: `W302`, `S55C` | 51/131, 89/100 |
| Thiếu dung sai: S136H, HPM1 (dung sai bị ghi lệch sang cột L), YXR3 | 43, 52, 65 |
| Thiếu nhiệt độ: SUS402J, M340, UNIMAX, SUS420, DAC, HPM1, S590, YXMIR, YXR3, S20C, GO4, S60C, STKM13, SNCM439, XSMO, KHAI | |
| Nhiệt độ lúc là text `650℃`, lúc là số `650` (nhóm Gió); Ram lúc `180P`, lúc `180p` | |
| Dung sai có nhiều cách viết: `MIN - 0.7`, `MIN -0.7`, `( MAX-MIN ) ±1`, `MAX +0.5`, `MIN ±0.7` | |

Dung sai sau chuẩn hóa (bỏ khoảng trắng): `(MAX-MIN)±1` 71, `MIN-0.7` 21, `MAX+0.5` 18, `MAX+0.7` 6, `MIN±0.7` 1, trống 3. **Cách tính giới hạn chờ xác nhận — OQ-01.**

## 3. Bản đồ ô 3 template (phân tích trực tiếp file)

Chỉ ghi vào ô trên-trái vùng gộp. Ngày ghi dạng **text `dd/MM/yyyy`** vì các ô ngày có định dạng số `0` (ghi số serial sẽ hiển thị sai).

| Dữ liệu | carbon (THÉP CACBON(SHT)) | khuon (KHUÔN DẬP(ORI)) | gio (THÉP GIÓ(ORI)) |
|---|---|---|---|
| Vùng in | A1:U36, scale 50, A4 dọc | A1:U37, scale 56 | A1:U35, scale 39 |
| Khách hàng | G6 (G6:U6) | G6 | G6 |
| Vật liệu | G7 (G7:M7) | G7 | G7 |
| Kích thước | R7 (R7:U7) | R7 | R7 |
| Mã QL | G8 (G8:M8) — **dòng 8 đang ẩn** | G8 — **ẩn** | *không có* |
| Tên chi tiết | R8 (R8:U8) — **ẩn** | R8 — **ẩn** | *không có* |
| Số lượng | G9 | G9 | G8 |
| Trọng lượng (kg) | R9 (định dạng 0.00) | R9 | R8 |
| Ngày nhận | G10 | G10 | G9 |
| Ngày kiểm tra | R10 | R10 | R9 |
| Số ĐNGC | G11 | G11 | G10 |
| Số báo cáo NNN/MM | R11 | R11 | R10 |
| Nhiệt độ Tôi | ô D15 (650℃), H14 (850℃) | trong hình vẽ (textbox 650/850/1030℃) | trong hình vẽ (650/850/1000/1180℃) |
| Ram | hình vẽ "180min" ×3 | hình vẽ "180p" ×3 | ô N16/R16/U16 "180P" |
| Khung ảnh mẫu | B19:K23 | B19:K23 | A18:L22 |
| Khung ảnh độ cứng | M19:Q23 và S19:U23 (2 khung) | M19:Q23, S19:U23 | M18:Q22, S18:U22 |
| Yêu cầu độ cứng (vd 58-62) | A28 (A28:C28) | A28 | A27 |
| Đơn vị | D28 "(HRC)" | D28 | D27 |
| Dung sai | A29 (sẵn "(MAX-MIN)±1") | A29 (trống) | A28 (sẵn "(MAX-MIN)±1") |
| Kết quả mẫu 1..6 | H26..H31 (H:M gộp, định dạng 0.0) | H26..H31 | H25..H30 |
| Ngoại quan – kết quả | S26 (S26:U27) | S26 (sẵn "OK") | S25 |
| Độ cong – yêu cầu / kết quả | Q28 / S28 | Q28 / S28 | Q27 / S27 |
| Khác – kết quả | S30 | S30 | S29 (đang chứa ký tự lạ U+0336) |
| Nhận xét | A32 (A32:M33, sẵn "Nhận xét/Remark: Chất lượng đạt") | A32 (trống) | A31 (trống) |
| Ô chữ ký Xác nhận/Approved | N34:R36 | N34:R36 | N33:R35 |
| Ô chữ ký Kiểm tra/Checker | S34:U36 | S34:U36 | S33:U35 |
| Footer | `TQM.QT11.BM02-03/07/2023`, `Trang &P/&N` | `TQM.QT23.BM01-20/08/2024` | `TQM.QT11.BM02-03/07/2023` (trùng mã carbon) |

Ghi chú bảo toàn template: cả 3 file có hình vẽ (nhóm đường, textbox), logo, ảnh thừa ngoài vùng in (cột V/W), định dạng có điều kiện mở rộng; carbon/khuon còn dữ liệu rác ở dòng 2762–2776 ngoài vùng in. **openpyxl làm mất hình vẽ và conditional formatting** → không dùng thư viện "đọc–ghi lại toàn bộ"; phải sửa trực tiếp XML ô và thêm ảnh vào drawing hiện có (OpenXML SDK). Chi tiết mapping ở `config/template-maps/*.json`.

## 4. Thực thể ứng dụng (tóm tắt — chi tiết ở design.md)
- **Order** (Dngc, LineNo, trường ERP, trường nhập tay, mác, template, yêu cầu độ cứng, gộp: MergeParentKey/Children, trạng thái).
- **InspectionImage** (Id, OrderKey, BlobSha256 gốc, phiên bản xử lý (crop/rotate…), Loại, Khung CQ, Slot, OcrSuggestion).
- **HardnessResult** (Unit, CheckDate, Appearance, Curvature req/result, Other, Samples[1..6]{Points[]{Value, Source=Manual/Ocr/Import, OcrRaw, OcrImageId}}, ConfirmedBy/At, Evaluation snapshot).
- **CqVersion** (Ver, ReportNo, TemplateId+hash, file xlsx sha, Result, CreatedBy/At, Approved{By, ApproverSigner, CheckerSigner, PdfSha, At}, Printed{By, At}, Deleted{By, At, Reason}, Warnings).
- **User** (Username, FullName, Role, PasswordHash PBKDF2, MustChangePassword, Active).
- **Signer** (Id, Name, OriginalBlob, TransparentBlob, Active); **Stamp**.
- **AuditEvent**, **ImportLog**, **SyncOutboxItem**, **Lock**, **SessionLease**.
