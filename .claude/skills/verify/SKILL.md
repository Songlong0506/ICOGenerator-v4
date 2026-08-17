---
name: verify
description: Chạy ICOGenerator end-to-end trong môi trường không có SQL Server / LLM thật (Claude Code web, CI) để xác minh một thay đổi bằng cách lái UI thật.
---

# Verify ICOGenerator end-to-end (không cần SQL Server / LLM thật)

## Build & chạy app (Sqlite)

Sqlite bật qua env var `Database__Provider=Sqlite` (DB file `ICOGenerator.db` đã .gitignore; connection string vẫn là chuỗi SQL Server thì code tự fallback về file này). Vẫn **chạy DLL trực tiếp** với `ASPNETCORE_ENVIRONMENT=Development` — `launchSettings.json` ép `Production` khi `dotnet run`, mà env Development mới nới `Cookie.SecurePolicy` để login chạy được qua HTTP:

```bash
dotnet build -v q
ASPNETCORE_ENVIRONMENT=Development Database__Provider=Sqlite Encryption__ApiKeyKey=verify-key \
  ASPNETCORE_URLS=http://127.0.0.1:5099 dotnet bin/Debug/net8.0/ICOGenerator.dll
```

- `Encryption__ApiKeyKey` bắt buộc (fail-fast nếu thiếu); giá trị bất kỳ.
- Boot xong DB tự migrate/EnsureCreated + seed: users (`superadmin`, `teamdev`, `user`), 5 agents, 2 AiModels.
- **KHÔNG có form đăng nhập**: provider `Local` tự phát cookie theo tài khoản SuperAdmin ngay tại
  `/Account/Login` (`AccountController.SignInLocalAdminAsync`), claim `Name` = `superadmin`. Nhưng nó
  **rơi mất ReturnUrl có query string** ⇒ vào một trang bất kỳ (`/`) trước để lấy cookie, rồi mới `goto`
  URL kèm `?projectId=…`; đi thẳng vào là bị đẩy về `/` và tưởng là lỗi phân quyền.

## LLM stub (để workflow agent chạy thật)

Model seed trỏ endpoint không tồn tại (và một model có ApiKey rỗng → lỗi "Value cannot be an empty string (Parameter 'key')"). Dựng stub OpenAI-compatible rồi trỏ model vào:

- Stub PHẢI hỗ trợ **SSE streaming** (`stream:true`) — trả JSON thường thì agent chạy "thành công" nhưng Output rỗng.
- `created` trong mỗi chunk là Unix **giây** (`Math.floor(Date.now()/1000)`). Trả mili giây thì MỌI lời gọi fail với `Valid values are between -62135596800 and 253402300799, inclusive. (Parameter 'seconds')` — lỗi này bị mã hoá trong `AgentModelCallLogs.ErrorMessage` (AES-GCM, key = SHA-256 của `Encryption__ApiKeyKey`) nên UI chỉ hiện lượt hỏng chung chung.
- Prompt chat của BA cũng **nhắc tới** "Bản đồ bao phủ yêu cầu" (bản đồ được nhét vào ngữ cảnh). Stub muốn trả nội dung khác nhau theo từng lượt thì phải khớp **dòng đầu** của system prompt (`# Vai trò: …`), khớp cả body là trả nhầm bản đồ vào chỗ lời thoại.
- Ghi request body ra file để soi prompt app thực sự gửi.
- Trỏ model: `UPDATE AiModels SET Endpoint='http://127.0.0.1:5098/v1', ApiKey='sk-stub'` (ApiKey plaintext trong DB vẫn đọc được — protector passthrough giá trị không có prefix mã hóa).

## Seed trạng thái workflow (không có sqlite3 CLI — dùng python3)

Enum lưu dạng **TEXT** (`'WaitingForHuman'`, `'ArchitectureDesign'`…). Project cần đủ các cột NOT NULL (Status=1, các *Count=0). **Datetime phải format EF: `'YYYY-MM-DD HH:MM:SS.ffffff'` (dấu CÁCH, không phải 'T')** — sai format là mọi ORDER BY datetime lệch.

Hai chỗ nữa làm hàng seed "đúng" mà app không thấy:

- **Guid lưu TEXT CHỮ HOA** (`'B16C2794-BFA2-…'`). So sánh của Sqlite phân biệt hoa/thường ⇒ insert id lowercase là mọi `WHERE Id = @id` trượt, và màn hình chỉ im lặng redirect về danh sách dự án như thể project không tồn tại. Dùng `str(uuid.uuid4()).upper()`.
- **`Projects.CreatedByUsername` phải khớp người đang đăng nhập** (`'superadmin'`) trừ khi role có `ProjectsViewAll`: `[RequireProjectAccess]` trả về giống hệt ca "không tồn tại", nên thiếu cột này cũng chỉ thấy một cú redirect.

```python
# WorkflowRun WaitingForHuman tại stage X + AgentTask Completed cùng loại = cổng duyệt mở
```

## Lái UI

Playwright global: `require('/opt/node22/lib/node_modules/playwright')` + `executablePath: '/opt/pw-browsers/chromium'`.
Selectors cổng duyệt (Agent Dashboard `/AgentDashboard?projectId=...`): `#delivery-gate`, `#dg-approve-form`, `#dg-reject-form`, `#dg-revise-btn`, `#dg-retry-form`, `#revise-modal`, `#dg-status`, `#dg-timeline`, `#dg-revise-note`. Gate poll ~2.5s; worker nhặt task Queued ~2s.

## Gotchas

- App fail SqlServer lúc boot = thiếu env var `Database__Provider=Sqlite` (mặc định `appsettings.json` là SqlServer).
- Worker chạy nền sẽ TỰ nhặt task Queued ngay — muốn quan sát trạng thái tĩnh thì đừng seed task Queued.
- DB 4KB là bình thường (WAL); file `ICOGenerator.db*` đã gitignore.
- **Sau khi chạy app trên Linux, XÓA thư mục rác `C:\Study App\ICOGeneratorWorkspaces` và `Logs/` trong repo root** — `AgentWorkspace:RootPath` là đường dẫn Windows nên Linux tạo thư mục literal chứa backslash, làm `dotnet build` fail `MSB3552 (**/*.resx cannot be found)`. Muốn tránh hẳn thì set env `AgentWorkspace__RootPath=/tmp/ico-workspaces` khi chạy.
