# ADR-002: Công nghệ

- **.NET 8 + WPF** (SK §6; người dùng xác nhận 13/11/2026). Tự chứa runtime khi publish win-x64.
- **Excel**: DocumentFormat.OpenXml (MIT) sửa trực tiếp XML ô/drawing. Loại: openpyxl/ClosedXML-kiểu "load–save toàn bộ" vì đã thử openpyxl làm mất hình vẽ và conditional formatting của template thật.
- **HTML-xls ERP**: AngleSharp hoặc parser bảng HTML tự viết (định dạng Oracle BI ổn định).
- **Ảnh**: OpenCvSharp4 (Apache-2.0). **OCR**: Tesseract (charlesw, Apache-2.0) + `eng.traineddata` người dùng cung cấp, chạy trên ROI theo profile máy.
- **Cache cục bộ**: Microsoft.Data.Sqlite. **MVVM**: CommunityToolkit.Mvvm.
- **PDF**: Excel COM (late-bound) → LibreOffice. **Installer**: Inno Setup 6 (biên dịch trên Windows).
- **Test**: xUnit; test kho chung mô phỏng nhiều client trên cùng thư mục (nhiều tiến trình).
Thư viện chốt sau khi thử fidelity trên file thật khi mạng NuGet được mở.
