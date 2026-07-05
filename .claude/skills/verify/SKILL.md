---
name: verify
description: Chạy ICOGenerator end-to-end trong môi trường không có SQL Server / LLM thật (Claude Code web, CI) để xác minh một thay đổi bằng cách lái UI thật.
---

# Verify ICOGenerator end-to-end (không cần SQL Server / LLM thật)

## Build & chạy app (Sqlite)

`launchSettings.json` ép `ASPNETCORE_ENVIRONMENT=Production` khi dùng `dotnet run` → **chạy DLL trực tiếp** để nhận env Development (Sqlite, DB file `ICOGenerator.db` đã .gitignore):

```bash
dotnet build -v q
ASPNETCORE_ENVIRONMENT=Development Encryption__ApiKeyKey=verify-key \
  ASPNETCORE_URLS=http://127.0.0.1:5099 dotnet bin/Debug/net8.0/ICOGenerator.dll
```

- `Encryption__ApiKeyKey` bắt buộc (fail-fast nếu thiếu); giá trị bất kỳ.
- Boot xong DB tự migrate/EnsureCreated + seed: users `admin/Admin@123`, 5 agents, 2 AiModels.
- Login form: `input[name=Username]`, `input[name=Password]` tại `/Account/Login`.

## LLM stub (để workflow agent chạy thật)

Model seed trỏ endpoint không tồn tại (và một model có ApiKey rỗng → lỗi "Value cannot be an empty string (Parameter 'key')"). Dựng stub OpenAI-compatible rồi trỏ model vào:

- Stub PHẢI hỗ trợ **SSE streaming** (`stream:true`) — trả JSON thường thì agent chạy "thành công" nhưng Output rỗng.
- Ghi request body ra file để soi prompt app thực sự gửi.
- Trỏ model: `UPDATE AiModels SET Endpoint='http://127.0.0.1:5098/v1', ApiKey='sk-stub'` (ApiKey plaintext trong DB vẫn đọc được — protector passthrough giá trị không có prefix mã hóa).

## Seed trạng thái workflow (không có sqlite3 CLI — dùng python3)

Enum lưu dạng **TEXT** (`'WaitingForHuman'`, `'ArchitectureDesign'`…). Project cần đủ các cột NOT NULL (Status=1, các *Count=0). **Datetime phải format EF: `'YYYY-MM-DD HH:MM:SS.ffffff'` (dấu CÁCH, không phải 'T')** — sai format là mọi ORDER BY datetime lệch.

```python
# WorkflowRun WaitingForHuman tại stage X + AgentTask Completed cùng loại = cổng duyệt mở
```

## Lái UI

Playwright global: `require('/opt/node22/lib/node_modules/playwright')` + `executablePath: '/opt/pw-browsers/chromium'`.
Selectors cổng duyệt (Agent Dashboard `/AgentDashboard?projectId=...`): `#delivery-gate`, `#dg-approve-form`, `#dg-reject-form`, `#dg-revise-btn`, `#dg-retry-form`, `#revise-modal`, `#dg-status`, `#dg-timeline`, `#dg-revise-note`. Gate poll ~2.5s; worker nhặt task Queued ~2s.

## Gotchas

- App fail SqlServer lúc boot = env chưa phải Development (xem launchSettings ở trên).
- Worker chạy nền sẽ TỰ nhặt task Queued ngay — muốn quan sát trạng thái tĩnh thì đừng seed task Queued.
- DB 4KB là bình thường (WAL); file `ICOGenerator.db*` đã gitignore.
- **Sau khi chạy app trên Linux, XÓA thư mục rác `C:\Study App\ICOGeneratorWorkspaces` và `Logs/` trong repo root** — `AgentWorkspace:RootPath` là đường dẫn Windows nên Linux tạo thư mục literal chứa backslash, làm `dotnet build` fail `MSB3552 (**/*.resx cannot be found)`. Muốn tránh hẳn thì set env `AgentWorkspace__RootPath=/tmp/ico-workspaces` khi chạy.
