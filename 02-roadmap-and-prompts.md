# Roadmap & Bộ Prompt Tạo Project Mới (`SmartClipVault`)

Tài liệu này chia lộ trình làm app thành các bước nhỏ để bạn chạy theo đúng quy trình tiết kiệm quota (**Hydrogen điều phối → Claude Sonnet 4.6 code nháp → Subagent `pro` soi `git diff`**).

---

## 1. Danh sách việc cần làm (MVP Checklist)

### Giai đoạn 1: Khởi tạo & Lõi Clipboard Native (Ngày 1)
- [ ] Khởi tạo Solution `.NET 10 LTS` (WPF + Tray Icon `Hardcodet.NotifyIcon.Wpf` hoặc WinUI/WinForms Tray + SQLite).
- [ ] Viết `ClipboardMonitorService` dùng Win32 `AddClipboardFormatListener` (có cơ chế retry/backoff tránh lỗi `CLIPBRD_E_CANT_OPEN`).
- [ ] Lưu lịch sử vào SQLite (`clips`: `Id`, `ContentType`, `TextContent`, `ImagePath`, `OcrText`, `IsSensitive`, `CreatedAt`, `PinBoardId`).
- [ ] Thêm bộ lọc chống rác: bỏ qua text quá ngắn/quá dài (cấu hình được), bỏ qua app nhạy cảm (như KeePass/1Password), tự động trim khoảng trắng.

### Giai đoạn 2: Vũ khí khác biệt — Secret Shield & Offline OCR (Ngày 2)
- [ ] Viết `SecretDetector`: Regex nhận diện API key (`sk-`, `ghp_`, `AIza...`, JWT, connection string) → gắn cờ `IsSensitive`, che hiển thị `***` và tùy chọn tự xóa sau 60s.
- [ ] Tích hợp `Windows.Media.Ocr` (chạy background queue không làm lag UI) để trích xuất `OcrText` khi người dùng copy ảnh → cho phép tìm kiếm ảnh bằng chữ.

### Giai đoạn 3: Giao diện Board + Quick Paste Menu + Prompt Vault (Ngày 3–4)
- [ ] Giao diện chính 2 cột lấy cảm hứng từ PasteBar: Bên trái là **Clipboard History** (tìm kiếm tức thì), bên phải là **Boards / Collections** (ghim Prompt AI, Code Snippets, Personal Info).
- [ ] Hỗ trợ **Prompt Template** có biến `{{clip}}` (chèn nhanh nội dung vừa copy vào prompt).
- [ ] **Global Hotkey** (ví dụ `Ctrl+Shift+V`) bật popup **Quick Paste Menu** ngay tại vị trí con trỏ, nhấn số `1-9` hoặc `Enter` để dán thẳng vào cửa sổ đang làm việc.

### Giai đoạn 4: Hoàn thiện & Đóng gói (Ngày 5)
- [ ] Thêm nút **Backup / Restore** (zip SQLite + thư mục ảnh) và chọn thư mục lưu dữ liệu tùy ý.
- [ ] Localization song ngữ **EN / VI** đầy đủ.
- [ ] Tích hợp **Auto-Update** (Luồng 4 trong `SKILL.md`) + Unit Test (`SelfTest`) + Push GitHub Release.

---

## 2. Bộ Prompt Mẫu (Copy-Paste để chạy ngay)

### Prompt 1: Khởi tạo Project & Lõi Clipboard (Dùng với `Gemini 4 Hydrogen`)
> "Hãy khởi tạo project .NET 10 LTS WPF mới tại `D:\code\SmartClipVault` với cấu trúc sạch (`Services/`, `Models/`, `Data/`, `Views/`). Cài đặt SQLite và tạo `ClipboardMonitorService` dùng Win32 `AddClipboardFormatListener` có cơ chế retry an toàn để không bị lỗi `CLIPBRD_E_CANT_OPEN`. Chạy `dotnet build` xác nhận build thành công."

### Prompt 2: Làm tính năng Secret Shield + Windows OCR (Áp dụng Luồng 1B trong `SKILL.md`)
> "Hãy thiết kế và triển khai `SecretDetector` (nhận diện API key, token, mật khẩu để che/tự xóa) và `OcrService` dùng `Windows.Media.Ocr` chạy ngầm cho ảnh vừa copy. Hãy lập `.claude-task.md`, gọi Claude Sonnet 4.6 code nháp 1 lượt, chạy test rồi tự động bật subagent `pro` soi `git diff` kiểm tra rò rỉ tài nguyên và bảo mật."

### Prompt 3: Làm giao diện Board + Quick Paste Menu (Áp dụng Luồng 1B)
> "Thiết kế giao diện chính 2 cột (trái: Clipboard History có thanh tìm kiếm; phải: Boards/Collections lưu Prompt Vault có biến `{{clip}}`) và Global Hotkey `Ctrl+Shift+V` bật Quick Paste Menu. Chú ý xử lý focus cửa sổ mượt trên Windows 11."

### Prompt 4: Soát lỗ hổng toàn diện trước khi Release (Áp dụng Luồng 2)
> "Hãy chạy `git diff` toàn bộ các thay đổi và gọi subagent `pro` kiểm tra 5 nhóm lỗ hổng: khóa file/clipboard lock, rò rỉ bộ nhớ/stream, Path Traversal khi Backup/Restore, lộ dữ liệu nhạy cảm trong log/SQLite, và lỗi focus UI trên Windows 11."
