# ZendiKey Setup

Ứng dụng Windows giúp kiểm tra API key và tự động cấu hình Claude, Codex/ChatGPT để dùng gateway `https://zendigikey.shop/v1`.

## Tải và sử dụng

1. [Tải `ZendiKeySetup.exe`](../../releases/latest) trong phần **Assets**.
2. Mở file vừa tải, nhập API key, chọn ứng dụng cần dùng rồi bấm **Set up selected**.
3. Khởi động lại Claude Desktop/ChatGPT sau khi hoàn tất.

**Set up selected** sẽ kiểm tra key, tìm model tương thích, cài các client còn thiếu đã được chọn và ghi cấu hình. Nút **Install selected** chỉ cài các client đang chọn mà không thay đổi cấu hình gateway.

ChatGPT thông thường vẫn dùng tài khoản OpenAI đang đăng nhập. Gateway tùy chỉnh chỉ áp dụng cho Codex mode.

## Cảnh báo Windows SmartScreen

Bản phát hành hiện chưa ký chứng thư số nên Windows có thể hiện cảnh báo ở lần chạy đầu. Nếu file được tải từ đúng trang Releases của dự án, chọn **More info** rồi **Run anyway**. Mã SHA-256 của từng bản được ghi trong release notes và có thể kiểm tra bằng:

```powershell
Get-FileHash .\ZendiKeySetup.exe -Algorithm SHA256
```

## Ứng dụng được cấu hình

- Claude Code qua `%USERPROFILE%\.claude\settings.json`
- Claude Desktop qua `%LOCALAPPDATA%\Claude-3p\configLibrary`
- Codex CLI qua `%USERPROFILE%\.codex\config.toml`
- ChatGPT Desktop Codex mode qua cấu hình Codex dùng chung
- Client còn thiếu qua `winget` và `npm`
- Danh sách model Codex tương thích với phiên bản client và gateway

Claude Code nhận gateway origin `https://zendigikey.shop` vì client tự nối thêm `/v1/messages`. Codex và bước kiểm tra API sử dụng base URL đầy đủ `https://zendigikey.shop/v1`.

## Bảo mật và khôi phục

API key được mã hóa bằng Windows DPAPI cho đúng tài khoản Windows hiện tại. Key không được ghi dạng plaintext vào JSON, TOML hoặc Registry. Trước mỗi lần áp dụng, ứng dụng tạo một bản sao lưu đã mã hóa; nút **Restore latest** khôi phục cấu hình gần nhất.

Không gửi `credential.dat` hoặc thư mục backup cho người khác. Mỗi khách hàng chỉ cần nhập key của chính họ trong ứng dụng.

## Build từ source

Yêu cầu Windows 10/11 và .NET Framework 4.x:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

File kết quả: `dist\ZendiKeySetup.exe`. Mặc định script dùng `assets\crane-source.jpg`; truyền `-IconSource` để dùng ảnh khác.

## Kiểm tra bằng dòng lệnh

```powershell
.\dist\ZendiKeySetup.exe --self-test
.\dist\ZendiKeySetup.exe --status
.\dist\ZendiKeySetup.exe --apply-existing
.\dist\ZendiKeySetup.exe --apply-stdin
.\dist\ZendiKeySetup.exe --install-missing
.\dist\ZendiKeySetup.exe --restore-latest
```

`--credential` chỉ dành cho credential helper của Claude và Codex. Các lệnh `--apply-existing`, `--apply-stdin` và `--install-missing` áp dụng cho cả hai nhóm client.
