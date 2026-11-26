# Nghiên Cứu Chuyên Sâu: PasteBar (`PasteBar/PasteBarApp`)

Tài liệu tổng hợp kiến trúc, tính năng, bản quyền và các nhược điểm thực tế của **PasteBar** (đối chiếu phiên bản `v0.7.0`) để làm cơ sở thiết kế một ứng dụng **Windows Native (.NET)** vượt trội hơn.

---

## 1. Tổng quan & Tech Stack của PasteBar

* **Mô hình:** Cross-platform (macOS & Windows), ưu tiên lưu trữ cục bộ (Local-first, không cloud).
* **Backend:** Rust + **Tauri** + **SQLite (Diesel ORM)** + Tokio, Serde.
* **Frontend:** **React 19** + TypeScript + Vite + TailwindCSS (React Query, Jotai, Zustand).
* **Tính năng cốt lõi:**
  * Lưu lịch sử clipboard vô hạn (text, ảnh, file, link, code snippet).
  * Tổ chức theo **Collection → Tab → Board → Clip** (trực quan, chia cột rõ ràng).
  * **Paste Menu** bật bằng phím tắt toàn cục (hỗ trợ tổ hợp 3 phím), nhận diện ngôn ngữ code, hỗ trợ Template/Form điền nhanh.
  * Khóa màn hình bằng PIN (`Ctrl+L`), tự động xóa khoảng trắng thừa, Backup & Restore.

---

## 2. Lưu ý Bản quyền (License)

* PasteBar **KHÔNG** dùng giấy phép MIT hay Apache.
* Nó dùng **Creative Commons Attribution-NonCommercial (CC BY-NC) with a Limited Commercial Use Exception**.
* **Hệ quả:** Không được fork nguyên app PasteBar để đổi tên, đóng gói lại và bán hoặc phân phối thương mại. Nếu muốn xây dựng sản phẩm riêng để phát hành rộng rãi hoặc thương mại hóa, bắt buộc phải **viết mới hoàn toàn từ đầu (clean-room)**, chỉ tham khảo ý tưởng UI/UX.

---

## 3. Các Nhược Điểm Thực Tế của PasteBar (Cơ Hội Cạnh Tranh)

Dựa trên GitHub Issues và phản hồi người dùng trên Reddit / Hacker News:

1. **Lỗi mở UI & Mất Focus trên Windows 11:**
   * *Issue #301:* Một số máy Windows 11 bấm mở từ Start Menu/Taskbar không hiện cửa sổ.
   * Do chạy qua WebView2 (Tauri), menu dán nhanh thỉnh thoảng mất focus hoặc tự tắt khi click/dùng đa màn hình.
2. **Tiêu tốn RAM & Độ trễ khi lịch sử lớn:**
   * Giao diện React trên WebView tốn RAM hơn app Win32/.NET thuần và có độ trễ nhẹ khi render hàng nghìn mục hoặc nhiều ảnh nặng.
   * *Issue #329:* Lỗi đồng bộ UI khi xóa ảnh trong chế độ lọc.
3. **Xung đột khóa Clipboard (`CLIPBRD_E_CANT_OPEN`):**
   * Theo dõi clipboard liên tục dễ gây tranh chấp khóa clipboard với các phần mềm khác trên Windows.
4. **Lưu ảnh thô gây phình Backup nhưng không đọc được chữ (Thiếu OCR):**
   * Lưu nhiều ảnh chụp màn hình làm file backup rất nặng, nhưng không tìm kiếm được chữ bên trong ảnh.
5. **Không bảo vệ Secret / API Key:**
   * Copy nhầm `.env`, token, `sk-...`, mật khẩu sẽ bị lưu thẳng dạng plain text vào lịch sử.
6. **Thiếu tính năng cho dân dùng AI & Xử lý text động:**
   * Chỉ lưu snippet tĩnh; chưa có Prompt Vault có biến động `{{code}}`, chưa có biến đổi text nhanh (format JSON, dịch nhanh, bỏ dấu, gọi AI).
7. **Chưa hỗ trợ Tiếng Việt** và **chưa có cơ chế chống xóa nhầm** (*Issue #330*).

---

## 4. Định hướng Sản phẩm Mới (Windows Native .NET)

Giữ lại điểm hay nhất của PasteBar (**bố cục Board trực quan + Paste Menu phím tắt**) nhưng khắc phục triệt để các nhược điểm trên:
* **Nền tảng:** C# / .NET 10 LTS (WPF hoặc WinUI 3) + SQLite (`Microsoft.Data.Sqlite`), chạy Tray App siêu nhẹ, khởi động tức thì.
* **3 Vũ khí khác biệt:**
  1. **Secret Shield:** Tự động phát hiện API Key / Password / Token khi copy → che `***` hoặc tự hủy sau 60 giây.
  2. **Offline Windows OCR:** Tự động đọc chữ từ ảnh chụp màn hình bằng `Windows.Media.Ocr` (miễn phí, offline) để tìm kiếm ảnh bằng từ khóa.
  3. **Prompt Vault & Quick Transform:** Kho Prompt AI có biến `{{selection}}` + biến đổi text 1 chạm (Format JSON, Trim, Case, Tiếng Việt).
