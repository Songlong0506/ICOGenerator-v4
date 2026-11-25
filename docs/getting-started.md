# Bắt đầu — chạy app lần đầu

## Tech stack

| Thành phần | Công nghệ | Ghi chú |
|---|---|---|
| Runtime | **.NET 8** (`net8.0`), ASP.NET Core **MVC** (Razor Views) | Không có SPA framework; JS/CSS thuần trong `wwwroot/` |
| ORM | **EF Core 8** | Provider chọn runtime: `SqlServer` (mặc định) hoặc `Sqlite` (dev/CI) |
| Agent runtime | **Microsoft.Agents.AI 1.10.0** (Microsoft Agent Framework) | `ChatClientAgent` + `AgentSession` tự lo vòng lặp ReAct |
| LLM abstraction | **Microsoft.Extensions.AI 10.7.0** + `Microsoft.Extensions.AI.OpenAI` | Nói chuyện với mọi endpoint OpenAI-compatible (LM Studio, DeepSeek, OpenAI...) |
| Sinh tài liệu | **DocumentFormat.OpenXml 3.5.1** | Điền nội dung vào template `.docx` trong `Templates/`; dựng `.docx` từ Markdown do LLM trả về |
| Đọc PDF | **PdfPig 0.1.15** | Trích text từ tài liệu nguồn user upload; trang SCAN (không có text) được lấy ảnh cả trang, trang có text được lấy các hình nhúng đủ lớn — cả hai ra PNG cho model vision |
| Logging | **Serilog** (Console + File xoay ngày) | Cấu hình hoàn toàn qua `appsettings.json` |
| Tracing/Metrics | **OpenTelemetry** (OTLP) | OPT-IN qua `Otel:Enabled`, mặc định tắt |
| Job theo lịch | **Hangfire 1.8** | Đồng bộ OrgUnits/Associates từ employee-api mỗi ngày; lưu job chung DB SQL Server (Sqlite: bộ nhớ trong). Dashboard `/hangfire` chỉ Admin — xem [architecture.md](architecture.md#job-chạy-theo-lịch-đi-qua-hangfire) |
| Test | **xUnit** (`tests/ICOGenerator.Tests`) | Chạy trên EF Sqlite — không cần SQL Server |
| Auth | Cookie + **SSO OpenID Connect** (IdentityServer) hoặc **cục bộ** (tự đăng nhập khi `IdentityServer:LocalRole` có giá trị), phân quyền tự xây (claim `ClaimTypes.Role` của phiên + `RolePermission`) | Không dùng ASP.NET Identity; app **không lưu mật khẩu, cũng không lưu vai trò** |

Solution có 2 project: `ICOGenerator.csproj` (web app, ở root) và `tests/ICOGenerator.Tests/ICOGenerator.Tests.csproj`.

---

## Yêu cầu môi trường

- **.NET 8 SDK**.
- **SQL Server** — *hoặc không cần gì cả* nếu chạy chế độ Sqlite (xem [Ba kịch bản chạy](#ba-kịch-bản-chạy)).
- **Chromium headless** cho tầng kiểm POC — *không cần cài tay*, app tự tải lần đầu (xem [Chromium cho tầng kiểm POC](#chromium-cho-tầng-kiểm-poc)).
- **Một endpoint LLM tương thích OpenAI.** Model seed mặc định trỏ LM Studio tại `http://127.0.0.1:1234/v1` và DeepSeek (`https://api.deepseek.com`, cần điền ApiKey). Bạn có thể thêm/sửa model ở màn hình **AI Models** sau khi đăng nhập.

## Bí mật bắt buộc (app fail-fast nếu thiếu)

```bash
# Khóa AES mã hóa cột ApiKey của bảng AiModels. KHÔNG commit giá trị thật.
Encryption__ApiKeyKey=<chuỗi-bí-mật-của-bạn>
```

Nạp qua biến môi trường hoặc `dotnet user-secrets`. **Cảnh báo:** đổi khóa này sau khi đã có ApiKey trong DB sẽ làm các ApiKey cũ không giải mã được (xem [operations.md](operations.md#troubleshooting--lỗi-thường-gặp)).

Khóa này cũng mã hóa Git token của từng dự án (nhập ở popup *Config delivery* của Agent Dashboard — không có biến môi trường nào cho tài khoản Git).

Các bí mật *tùy chọn* khác (chỉ khi dùng tính năng tương ứng): `Notifications__Email__Password`, `Notifications__BoschEmail__ApiKey`, `BoschTemplate__BackendRepoUrl` / `BoschTemplate__FrontendRepoUrl`.

> Deploy lên máy chủ (IIS…) xong, vào **Settings → Test Environment** để app tự kiểm mọi thứ ở trên (và hơn
> thế) dưới đúng tài khoản đang chạy nó — xem [operations.md](operations.md#kiểm-tra-môi-trường-triển-khai-nút-test-environment).

## Ba kịch bản chạy

**Kịch bản A — máy dev "đầy đủ" (Windows + SQL Server + LM Studio):**

1. Sửa `appsettings.json`:
   - `ConnectionStrings:DefaultConnection` → SQL Server của bạn.
   - `AgentWorkspace:RootPath` → một thư mục **tồn tại** trên máy (nơi agent đọc/ghi file sinh ra).
2. Đặt `Encryption__ApiKeyKey`.
3. `dotnet run` → app nghe tại `https://localhost:55356` / `http://localhost:55357` (theo `Properties/launchSettings.json`).

> ⚠️ `launchSettings.json` ép `ASPNETCORE_ENVIRONMENT=Production` — nghĩa là `dotnet run` mặc định dùng **SqlServer** theo `appsettings.json`.

**Kịch bản B — không có SQL Server (Sqlite):**

Bật Sqlite bằng cách để connection string **rỗng** — biến môi trường `ConnectionStrings__DefaultConnection=` (không giá trị) đè lên chuỗi SQL Server trong `appsettings.json`; app dùng file `ICOGenerator.db` (đã `.gitignore`). Vẫn nên chạy DLL trực tiếp với `ASPNETCORE_ENVIRONMENT=Development` — vừa tránh launchSettings ép Production, vừa nới `Cookie.SecurePolicy` để login chạy được qua HTTP:

```bash
dotnet build -v q
ASPNETCORE_ENVIRONMENT=Development \
ConnectionStrings__DefaultConnection= \
Encryption__ApiKeyKey=dev-key \
AgentWorkspace__RootPath=/tmp/ico-workspaces \
EmployeeApi__Enabled=false \
ASPNETCORE_URLS=http://127.0.0.1:5099 \
dotnet bin/Debug/net8.0/ICOGenerator.dll
```

> `EmployeeApi__Enabled=false` tắt job đồng bộ dữ liệu tổ chức: máy không vào được mạng Bosch thì job chỉ thất bại và ghi log lỗi mỗi lần thử lại. Hệ quả là bảng `OrgUnits`/`Associates` trống — BA vẫn chạy (bối cảnh tổ chức fail-open), nhưng tạo project cần một orgUnit nên phải tự chèn vài dòng vào `OrgUnits`.

> ⚠️ Trên Linux/macOS **luôn override `AgentWorkspace__RootPath`** — giá trị mặc định là đường dẫn Windows (`C:\Study App\...`), Linux sẽ tạo một thư mục literal chứa backslash ngay trong repo và làm `dotnet build` lần sau fail `MSB3552` (xem [operations.md](operations.md#troubleshooting--lỗi-thường-gặp)).

**Kịch bản C — Claude Code web / CI:** dùng skill có sẵn trong repo `.claude/skills/verify/SKILL.md` — hướng dẫn đầy đủ cách dựng LLM stub (SSE) và lái UI bằng Playwright để xác minh end-to-end không cần SQL Server / LLM thật. Xem [testing.md](testing.md).

## Điều gì xảy ra khi khởi động

`Program.cs` gọi `DbInitializer.InitializeAsync` **trước khi** nhận request:

1. **Schema**: SqlServer → `MigrateAsync()` (chạy migrations); Sqlite → `EnsureCreatedAsync()` (dựng thẳng từ model, vì migration sinh ra là SQL-Server-specific).
2. **Cứu task mồ côi**: task còn `Running` sau restart được re-queue (tối đa 3 lần thử — quá thì đánh `Failed` cả task lẫn run).
3. **Seed ma trận quyền** (khi bảng trống): chỉ role Admin (`HCP_CBO_API.CBO.ADMIN`) = toàn bộ quyền (cấu hình được). Role IS4 khác không được seed — chưa có quyền nào cho tới khi Admin tick ở popup Permissions.
4. **Seed golden set Prompt Evals** (khi bảng `EvalScenarios` trống): bộ scenario mặc định phủ các prompt đánh-giá-được (xem `Data/EvalScenariosSeedData.cs`) — sửa/tắt thoải mái, không bị ghi đè ở lần khởi động sau.
5. **Đồng bộ danh mục tool**: `ToolDiscoveryService` quét các method có `[Description]` trong các class `*Tools` → upsert bảng `ToolDefinitions`.
6. **Seed 2 AiModels** (Qwen3.6 27B @ LM Studio, DeepSeek V4 Flash) + **5 agents** (BA/Tech Lead/Developer/Tester/UI-UX) kèm bộ tool mặc định cho từng vai — chỉ khi các bảng trống.

Ngay sau đó `OrganizationSyncJob.ScheduleAsync` đăng ký lịch đồng bộ OrgUnits/Associates hằng ngày (Hangfire) và, nếu hai bảng còn trống, xếp một lần chạy ngay — **không còn dữ liệu tổ chức seed sẵn** (xem [data-model.md](data-model.md#orgunits--associates--đồng-bộ-từ-employee-api)).

Vào app → redirect `/Account/Login`. Với `IdentityServer:LocalRole = Admin` (mặc định của repo này) bước này **không có form** — app tự phát cookie Admin rồi vào thẳng route mặc định **Projects** (`{controller=Projects}/{action=Index}`).

## Chạy test

```bash
dotnet test
```

xUnit, chạy trên Sqlite — không cần SQL Server hay LLM. Test nằm ở `tests/ICOGenerator.Tests/`, tổ chức theo đúng khu vực code (`Requirements/`, `Workflows/`, `Prompts/`, `Evals/`...).

## Chromium cho tầng kiểm POC

Bước POC không chỉ quét chuỗi: `PlaywrightPocRuntimeChecker` **mở poc-demo.html trong Chromium headless** để chạy self-test business rule, lái kịch bản nghiệm thu bằng click thật, và chụp ảnh từng màn hình cho Visual QA (xem [workspace-and-poc.md](workspace-and-poc.md#poc-demo)). Package NuGet `Microsoft.Playwright` đã có sẵn trong `.csproj`, nhưng **binary Chromium thì không nằm trong repo** — nó ~300MB mỗi nền tảng, vượt trần 100MB/file của GitHub và sẽ nằm vĩnh viễn trong git history.

**Máy mới chỉ cần clone rồi chạy.** Lần audit POC đầu tiên, nếu chưa có binary, app **tự tải một lần** vào cache dùng chung của máy (`%LOCALAPPDATA%\ms-playwright` trên Windows, `~/.cache/ms-playwright` trên Linux/macOS) rồi chạy tiếp — mất khoảng một phút, và mọi project Playwright khác trên máy dùng chung bộ đó.

Muốn cài trước cho chủ động (hoặc máy chặn tải lúc runtime):

```powershell
dotnet build
pwsh bin/Debug/net8.0/playwright.ps1 install chromium   # cần PowerShell 7: winget install Microsoft.PowerShell
```

**Máy không tải được** (mạng công ty chặn CDN Playwright): trỏ thẳng vào Chrome/Edge sẵn có bằng `Poc:RuntimeCheck:BrowserPath` hoặc biến môi trường `POC_BROWSER_PATH`. Có đường dẫn chỉ định sẵn thì app **không** tự tải nữa — đã chỉ đường mà sai thì tải về cũng không dùng tới.

Toàn tầng này **fail-open**: không có browser, tải hỏng, hay tắt bằng `Poc:RuntimeCheck:Enabled=false` thì audit POC vẫn chạy phần kiểm tra tĩnh và pipeline không bao giờ bị chặn. Trang **POC Review** nói thẳng chuyện đó ở panel *"Máy đã tự kiểm"* — dòng *"Tầng chạy thử trong trình duyệt không hoạt động ở môi trường này (…)"* kèm lý do và lệnh cài. Panel đọc bản chụp của **vòng audit cuối**, nên cài browser xong phải **restart app** (lỗi launch được cache theo process) và chạy lại một vòng POC thì các dòng ✓ mới hiện.
