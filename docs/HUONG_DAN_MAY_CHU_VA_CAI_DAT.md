# Hướng dẫn cài đặt: mỗi máy chạy độc lập, dữ liệu chung trên NAS Synology (0.9.2)

## 1. Mô hình hoạt động

```
                 NAS Synology edoc.oristar.vn  (thư mục nhóm TQM HƯNG YÊN \ DU LIEU NHIET)
                        ▲            ▲            ▲
         Synology Drive │            │            │  (đồng bộ qua internet, vài giây – vài phút)
                        │            │            │
   ADMIN-PC: C:\Users\..\SynologyDrive\DU LIEU NHIET   Máy QC: ...\SynologyDrive\DU LIEU NHIET   Máy KD: ...
            └ app CQ Nhiệt Luyện                        └ app                                  └ app
```

- Mỗi máy cài app riêng (`CQNhietLuyen-Setup-x.y.z-x64.exe`). App chạy độc lập, không cần cài .NET.
- Mỗi máy cài **Synology Drive Client** và đồng bộ thư mục `DU LIEU NHIET` về ổ của máy mình. App đọc, ghi trên bản này.
- **ADMIN-PC tắt nguồn vẫn dùng được.** Máy nào cũng có đủ dữ liệu: tài khoản, đơn, ảnh, CQ, chữ ký, con dấu, template.
- Tài khoản app do admin tạo một lần. Máy nào đã đồng bộ xong thì đăng nhập đúng tài khoản là dùng được ngay.
- File CQ xuất ra (Excel, PDF) được lưu vào thư mục `DU LIEU NHIET\File CQ\...`. Synology Drive tự đưa lên NAS cho các máy khác.
- Cần **internet** để các máy thấy dữ liệu của nhau. Mất mạng vẫn làm được trên máy mình; có mạng lại, Synology Drive tự đồng bộ.

### App xử lý trường hợp hai máy làm cùng lúc như sau
| Trường hợp | App xử lý |
|---|---|
| Hai máy sửa **cùng một đơn** trước khi đồng bộ kịp | Không mất bản nào. Mọi máy chọn cùng một bản là bản đang dùng (bản ghi sớm hơn). Máy có bản bị thay sẽ thấy mục **Xung đột** để xem và chọn giữ bản nào. |
| Hai máy cấp **cùng số báo cáo** trước khi đồng bộ kịp | Chỉ một CQ giữ được số đó. CQ kia bị **chặn khi duyệt hoặc phát hành**, kèm thông báo bấm "Đổi số báo cáo (tạo lại CQ)". Không bao giờ có hai CQ phát hành cùng số. |
| Máy mới chưa đồng bộ xong | App báo "Kho CHƯA ĐỒNG BỘ XONG" và **không tạo kho rỗng mới**. Chờ đồng bộ xong rồi bấm Thử lại. |
| Hai máy cùng đăng nhập một tài khoản lúc chưa đồng bộ | Cả hai vẫn vào được, vì app không chặn được khi chưa đồng bộ. Mỗi người nên dùng tài khoản riêng. |

## 2. Cài trên MỖI máy (kể cả ADMIN-PC)
1. Cài **Synology Drive Client** (tải ở synology.com → Download Center, hoặc hỏi IT).
2. Mở Synology Drive Client → **Tác vụ đồng bộ**:
   - Địa chỉ máy chủ: `edoc.oristar.vn` (hoặc QuickConnect ID do IT đưa).
   - Đăng nhập bằng tài khoản NAS của người dùng. Tài khoản này phải được cấp quyền đọc và ghi thư mục nhóm **TQM HƯNG YÊN**.
   - Thư mục trên NAS: `TQM HƯNG YÊN`. Thư mục trên máy: để mặc định `C:\Users\<tên>\SynologyDrive`.
3. Chờ đồng bộ xong. Sau đó chuột phải thư mục `DU LIEU NHIET` → Synology Drive → **Luôn giữ trên thiết bị này**, để không có file "chỉ trên máy chủ".
4. Chạy `tools\kiem_tra_dong_bo_synology.ps1` (chuột phải → Run with PowerShell). Các dòng phải là OK.
5. Chạy `CQNhietLuyen-Setup-x.y.z-x64.exe`.
   - Ở trang **Kho dữ liệu chung**, để mặc định `%USERPROFILE%\SynologyDrive\DU LIEU NHIET`.
   - Máy đã cài bản cũ trỏ về `\\ADMIN-PC\...`: bộ cài tự đề nghị đường dẫn mới. App cũng tự chuyển sang bản đồng bộ trên máy nếu bản này đã có dữ liệu.
6. Mở app và đăng nhập tài khoản app. Vào **Cài đặt → Kiểm tra máy này**: mục "Vị trí kho chung" và "Synology Drive" phải là ✓.
7. **Máy tạo hoặc duyệt CQ cần Microsoft Excel**, vì app xuất PDF đúng mẫu bằng Excel.

> Thư mục trên máy có thể khác `SynologyDrive` (ví dụ đặt ở ổ D). Khi đó nhập đúng đường dẫn đến `...\DU LIEU NHIET` ở bước 5, hoặc trong app: màn Đăng nhập → Đổi đường dẫn kho. Đường dẫn phải chứa chữ `SynologyDrive`. Nếu không chứa, app coi đó là thư mục thường và báo lỗi vị trí kho.

## 3. Cài đặt chung trong app (admin, làm 1 lần)
- **Cài đặt → Thư mục ERP / Thư mục lưu CQ**: chọn thư mục **bên trong** `DU LIEU NHIET`, ví dụ `DU LIEU NHIET\ERP` và `DU LIEU NHIET\File CQ`.
  - App lưu đường dẫn dạng `{KHO}\File CQ`, nên mỗi máy tự hiểu theo thư mục Synology Drive của máy mình.
  - Đường dẫn cũ kiểu `\\ADMIN-PC\...\DU LIEU NHIET\...` hoặc `C:\Users\Admin\SynologyDrive\DU LIEU NHIET\...` được app tự đổi sang thư mục tương ứng trên máy đang dùng.
- Chọn **Người duyệt, Người kiểm tra mặc định**. Tạo thêm **1 tài khoản admin dự phòng**.

## 4. Sửa nội dung CQ bằng tay trước khi in
- Ở màn duyệt / xuất CQ, bấm **Mở Excel để sửa**. App mở file Excel của CQ. Mọi ô nội dung đều sửa được.
- Sửa xong thì **lưu file trong Excel**, quay lại app và bấm **Tạo PDF từ file đã sửa**. Áp dụng cho cả bản ký, bản đóng dấu chưa ký và bản ký tay.
- App kiểm tra file sửa vẫn đúng CQ (còn số báo cáo, số ĐNGC), ghi "sửa tay" vào nhật ký và giữ lại file đã sửa cùng CQ.

## 5. Cập nhật phiên bản mới
- Lấy file cài mới từ GitHub (tab Actions → Artifacts, hoặc trang Releases) rồi chạy đè lên bản cũ. Dữ liệu giữ nguyên.
- Gỡ cài đặt **không xoá dữ liệu**.

## 6. Sao lưu và khôi phục
- Toàn bộ dữ liệu app nằm trong `DU LIEU NHIET\CQNL` (records, blobs, events, audit, counters).
- Nên bật **lịch sử phiên bản (Versioning)** cho thư mục nhóm TQM HƯNG YÊN trên NAS.
- Trong app: Cài đặt → Sao lưu (admin). "Reset dữ liệu" luôn tự sao lưu trước khi xoá.
- **Không** sửa, xoá hay đổi tên file trong `CQNL` bằng tay. File có chữ "Conflict" do Synology tạo ra thì app bỏ qua. Báo quản trị xem, đừng xoá.

## 7. Sự cố thường gặp
| Hiện tượng | Kiểm tra |
|---|---|
| App báo "Kho CHƯA ĐỒNG BỘ XONG" | Synology Drive Client đã đăng nhập và đang đồng bộ TQM HƯNG YÊN chưa; chờ xong rồi bấm Thử lại |
| Máy A làm xong mà máy B chưa thấy | Hai máy đều có internet chưa; Synology Drive trên máy A đã tải lên xong chưa (biểu tượng ✓) |
| App báo trùng số báo cáo khi duyệt | Hai máy cấp số cùng lúc khi chưa đồng bộ. Bấm "Đổi số báo cáo (tạo lại CQ)" |
| Có mục **Xung đột** | Hai máy sửa cùng đơn cùng lúc. Mở mục Xung đột, so sánh rồi chọn giữ bản nào |
| Tạo, duyệt CQ báo thiếu Excel | Cài Microsoft Excel trên máy đó (WPS không thay được) |

## 8. Cách cũ (không khuyến nghị): kho trên ổ ADMIN-PC qua mạng LAN
Vẫn dùng được với đường dẫn `\\ADMIN-PC\Users\Admin\SynologyDrive\DU LIEU NHIET`. Khi đó:
- ADMIN-PC **phải bật nguồn**.
- Máy trạm chạy `tools\ket_noi_kho_chung_may_tram.ps1`.
- ADMIN-PC chạy `tools\cau_hinh_may_chu_ADMIN-PC.ps1`.
