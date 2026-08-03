# ICOGenerator — Sổ tay Developer toàn diện

> **Mục tiêu của tài liệu này:** một developer hoàn toàn mới, chưa từng thấy dự án, đọc xong là (1) hiểu app làm gì và vì sao, (2) chạy được app trên máy mình, (3) biết mọi mảnh ghép nằm ở đâu, (4) tự tin sửa lỗi và thêm tính năng đúng kiến trúc.
>
> Tài liệu này là bản **tổng hợp tự đứng độc lập**. Hai tài liệu chuyên sâu đi kèm:
> - [`ARCHITECTURE.md`](ARCHITECTURE.md) — luật phân tầng, pattern, và chi tiết từng cơ chế (mục 5.x).
> - [`DEVELOPER_GUIDE.md`](DEVELOPER_GUIDE.md) — tư duy thiết kế pipeline & hướng dẫn mở rộng theo ví dụ.

---

## Mục lục

1. [App này là gì](#1-app-này-là-gì)
2. [Tech stack](#2-tech-stack)
3. [Chạy app lần đầu](#3-chạy-app-lần-đầu)
4. [Bản đồ thư mục & kiến trúc phân tầng](#4-bản-đồ-thư-mục--kiến-trúc-phân-tầng)
5. [Mô hình dữ liệu — toàn bộ các bảng](#5-mô-hình-dữ-liệu--toàn-bộ-các-bảng)
6. [Hai động cơ của hệ thống](#6-hai-động-cơ-của-hệ-thống)
7. [Delivery Pipeline chi tiết](#7-delivery-pipeline-chi-tiết)
8. [Agent & hệ thống Tool](#8-agent--hệ-thống-tool)
9. [Tầng LLM](#9-tầng-llm)
10. [Hệ thống Prompt](#10-hệ-thống-prompt)
11. [Workspace & sản phẩm sinh ra](#11-workspace--sản-phẩm-sinh-ra)
12. [Các màn hình & endpoint](#12-các-màn-hình--endpoint)
13. [Bảo mật: đăng nhập, phân quyền, rào chắn](#13-bảo-mật-đăng-nhập-phân-quyền-rào-chắn)
14. [Tham chiếu cấu hình appsettings.json](#14-tham-chiếu-cấu-hình-appsettingsjson)
15. [Logging & Observability](#15-logging--observability)
16. [Các tính năng vệ tinh](#16-các-tính-năng-vệ-tinh)
17. [Test & xác minh end-to-end](#17-test--xác-minh-end-to-end)
18. [Công thức làm việc: thêm tính năng, quy ước code](#18-công-thức-làm-việc-thêm-tính-năng-quy-ước-code)
19. [Troubleshooting — lỗi thường gặp](#19-troubleshooting--lỗi-thường-gặp)
20. [Từ điển thuật ngữ](#20-từ-điển-thuật-ngữ)

---

## 1. App này là gì

**ICOGenerator là một hệ thống multi-agent dùng LLM để biến *một cuộc trò chuyện về yêu cầu phần mềm* thành *tài liệu đặc tả + demo chạy được + source code + Pull Request*, với con người duyệt ở từng cổng.**

Luồng end-to-end nhìn từ người dùng:

```
User tạo Project
  └► Chat với agent BA (hỏi đáp làm rõ yêu cầu, có thể upload tài liệu nguồn:
       ảnh, PDF — kể cả bản scan, Word .docx, Excel/CSV)
       └► "Write Requirement" → BA sinh Product Brief (ngôn ngữ đời thường, dạng draft, sửa được nhiều lần)
            └► User bấm "Approve"
                 ├► Product Brief được chốt thành V{n}
                 ├► BA sinh AI Design Spec (bản kỹ thuật) ở một run nền riêng
                 ├► CỔNG XÁC NHẬN GIẢ ĐỊNH: spec có giả định tự đưa ⇒ dừng cho user rà
                 │  (Đồng ý → dựng POC; Chưa đúng → ghi đính chính rồi sinh lại spec)
                 └► Delivery Pipeline khởi động, chạy nền với CỔNG DUYỆT giữa mỗi bước:
                      POC HTML → Tài liệu kỹ thuật (BRD/SRS/FSD/UserStories) → Kiến trúc
                      → Code đầy đủ → Code Review → Testing (tự sửa lỗi khi FAIL) → Pull Request
```

Hai nhóm người dùng chính:

| Vai | Làm gì | Dừng ở đâu |
|---|---|---|
| **User** (người có nhu cầu phần mềm) | Tạo project, chat với BA, duyệt Product Brief, xem POC demo | Flow của họ dừng ở bước POC — banner báo "đội Dev sẽ tiếp nhận" |
| **TeamDev / Admin** | Đẩy các bước sau POC trên **Agent Dashboard**: duyệt/yêu cầu chỉnh sửa/từ chối từng cổng, cấu hình delivery, xem log AI | Đến khi PR được tạo |

Bên trong, "nhân sự" là 5 **AI agent** (seed sẵn): **BA** (Business Analyst), **Tech Lead**, **Developer**, **Tester**, **UI/UX** — mỗi agent có system prompt riêng, model riêng, và một tập **tool** được phép dùng (đọc/ghi file, chạy lệnh, git...). Hệ thống có đầy đủ hạ tầng vận hành: phân quyền theo role, audit log, budget chặn chi phí LLM, thông báo (in-app/Teams/email), đo chất lượng prompt (Evals), quản lý phiên bản prompt (Prompt Studio), báo cáo Usage/Delivery Quality.

Ứng dụng được xây trong bối cảnh nội bộ Bosch: có dữ liệu tổ chức (OrgUnits/Associates đồng bộ từ HR_Portal) để BA "hiểu" phòng ban thật, và tùy chọn dựng code trên khung chuẩn Bosch (.NET backend + Angular frontend).

---

## 2. Tech stack

| Thành phần | Công nghệ | Ghi chú |
|---|---|---|
| Runtime | **.NET 8** (`net8.0`), ASP.NET Core **MVC** (Razor Views) | Không có SPA framework; JS/CSS thuần trong `wwwroot/` |
| ORM | **EF Core 8** | Provider chọn runtime: `SqlServer` (mặc định) hoặc `Sqlite` (dev/CI) |
| Agent runtime | **Microsoft.Agents.AI 1.10.0** (Microsoft Agent Framework) | `ChatClientAgent` + `AgentSession` tự lo vòng lặp ReAct |
| LLM abstraction | **Microsoft.Extensions.AI 10.7.0** + `Microsoft.Extensions.AI.OpenAI` | Nói chuyện với mọi endpoint OpenAI-compatible (LM Studio, DeepSeek, OpenAI...) |
| Sinh tài liệu | **DocumentFormat.OpenXml 3.5.1** | Điền nội dung vào template `.docx` trong `Templates/` |
| Đọc PDF | **PdfPig 0.1.15** | Trích text từ tài liệu nguồn user upload; trang SCAN (không có text) được lấy ảnh nhúng ra PNG cho model vision |
| Logging | **Serilog** (Console + File xoay ngày) | Cấu hình hoàn toàn qua `appsettings.json` |
| Tracing/Metrics | **OpenTelemetry** (OTLP) | OPT-IN qua `Otel:Enabled`, mặc định tắt |
| Test | **xUnit** (`tests/ICOGenerator.Tests`) | Chạy trên EF Sqlite — không cần SQL Server |
| Auth | Cookie authentication + phân quyền tự xây (bảng `RolePermission`) | Không dùng ASP.NET Identity đầy đủ, chỉ dùng `PasswordHasher` |

Solution có 2 project: `ICOGenerator.csproj` (web app, ở root) và `tests/ICOGenerator.Tests/ICOGenerator.Tests.csproj`.

---

## 3. Chạy app lần đầu

### 3.1. Yêu cầu môi trường

- **.NET 8 SDK**.
- **SQL Server** — *hoặc không cần gì cả* nếu chạy chế độ Sqlite (xem 3.3).
- **Một endpoint LLM tương thích OpenAI.** Model seed mặc định trỏ LM Studio tại `http://127.0.0.1:1234/v1` và DeepSeek (`https://api.deepseek.com`, cần điền ApiKey). Bạn có thể thêm/sửa model ở màn hình **AI Models** sau khi đăng nhập.

### 3.2. Bí mật bắt buộc (app fail-fast nếu thiếu)

```bash
# Khóa AES mã hóa cột ApiKey của bảng AiModels. KHÔNG commit giá trị thật.
Encryption__ApiKeyKey=<chuỗi-bí-mật-của-bạn>
```

Nạp qua biến môi trường hoặc `dotnet user-secrets`. **Cảnh báo:** đổi khóa này sau khi đã có ApiKey trong DB sẽ làm các ApiKey cũ không giải mã được (xem [§19](#19-troubleshooting--lỗi-thường-gặp)).

Các bí mật *tùy chọn* khác (chỉ khi dùng tính năng tương ứng): `PullRequest__GitHubToken`, `Notifications__Email__Password`, `Notifications__BoschEmail__ApiKey`, `BoschTemplate__BackendRepoUrl` / `BoschTemplate__FrontendRepoUrl`.

### 3.3. Ba kịch bản chạy

**Kịch bản A — máy dev "đầy đủ" (Windows + SQL Server + LM Studio):**

1. Sửa `appsettings.json`:
   - `ConnectionStrings:DefaultConnection` → SQL Server của bạn.
   - `AgentWorkspace:RootPath` → một thư mục **tồn tại** trên máy (nơi agent đọc/ghi file sinh ra).
2. Đặt `Encryption__ApiKeyKey`.
3. `dotnet run` → app nghe tại `https://localhost:55356` / `http://localhost:55357` (theo `Properties/launchSettings.json`).

> ⚠️ `launchSettings.json` ép `ASPNETCORE_ENVIRONMENT=Production` — nghĩa là `dotnet run` mặc định dùng **SqlServer** theo `appsettings.json`.

**Kịch bản B — không có SQL Server (Sqlite):**

Bật Sqlite bằng biến môi trường `Database__Provider=Sqlite` (DB file `ICOGenerator.db`, đã `.gitignore`; connection string vẫn dạng SQL Server thì code tự fallback về file này). Vẫn nên chạy DLL trực tiếp với `ASPNETCORE_ENVIRONMENT=Development` — vừa tránh launchSettings ép Production, vừa nới `Cookie.SecurePolicy` để login chạy được qua HTTP:

```bash
dotnet build -v q
ASPNETCORE_ENVIRONMENT=Development \
Database__Provider=Sqlite \
Encryption__ApiKeyKey=dev-key \
AgentWorkspace__RootPath=/tmp/ico-workspaces \
ASPNETCORE_URLS=http://127.0.0.1:5099 \
dotnet bin/Debug/net8.0/ICOGenerator.dll
```

> ⚠️ Trên Linux/macOS **luôn override `AgentWorkspace__RootPath`** — giá trị mặc định là đường dẫn Windows (`C:\Study App\...`), Linux sẽ tạo một thư mục literal chứa backslash ngay trong repo và làm `dotnet build` lần sau fail `MSB3552` (xem §19).

**Kịch bản C — Claude Code web / CI:** dùng skill có sẵn trong repo `.claude/skills/verify/SKILL.md` — hướng dẫn đầy đủ cách dựng LLM stub (SSE) và lái UI bằng Playwright để xác minh end-to-end không cần SQL Server / LLM thật. Xem [§17](#17-test--xác-minh-end-to-end).

### 3.4. Điều gì xảy ra khi khởi động

`Program.cs` gọi `DbInitializer.InitializeAsync` **trước khi** nhận request:

1. **Schema**: SqlServer → `MigrateAsync()` (chạy migrations); Sqlite → `EnsureCreatedAsync()` (dựng thẳng từ model, vì migration sinh ra là SQL-Server-specific).
2. **Cứu task mồ côi**: task còn `Running` sau restart được re-queue (tối đa 3 lần thử — quá thì đánh `Failed` cả task lẫn run).
3. **Seed users** (khi bảng trống): `admin`/`Admin@123`, `teamdev`/`TeamDev@123`, `user`/`User@123` — **đổi ngay trên môi trường thật**, app có ghi log cảnh báo.
4. **Seed ma trận quyền** (khi bảng trống): Admin = toàn bộ quyền (cấu hình được); TeamDev = mọi thứ trừ Settings/Roles; User = xem Projects/Requirements + gửi Feedback. SuperAdmin không cần dòng nào (implicit-all).
5. **Seed OrgUnits/Associates** (dữ liệu tổ chức mẫu từ HR_Portal, chỉ khi trống).
6. **Seed golden set Prompt Evals** (khi bảng `EvalScenarios` trống): bộ scenario mặc định phủ các prompt đánh-giá-được (xem `Data/EvalScenariosSeedData.cs`) — sửa/tắt thoải mái, không bị ghi đè ở lần khởi động sau.
7. **Đồng bộ danh mục tool**: `ToolDiscoveryService` quét các method có `[Description]` trong các class `*Tools` → upsert bảng `ToolDefinitions`.
8. **Seed 2 AiModels** (Qwen3.6 27B @ LM Studio, DeepSeek V4 Flash) + **5 agents** (BA/Tech Lead/Developer/Tester/UI-UX) kèm bộ tool mặc định cho từng vai — chỉ khi các bảng trống.

Vào app → redirect `/Account/Login` → đăng nhập → route mặc định là **Projects** (`{controller=Projects}/{action=Index}`).

### 3.5. Chạy test

```bash
dotnet test
```

xUnit, chạy trên Sqlite — không cần SQL Server hay LLM. Test nằm ở `tests/ICOGenerator.Tests/`, tổ chức theo đúng khu vực code (`Requirements/`, `Workflows/`, `Prompts/`, `Evals/`...).

---

## 4. Bản đồ thư mục & kiến trúc phân tầng

### 4.1. Kiến trúc: Layered + "một use case một class"

```
Controllers  ─►  Application  ─►  Services  ─►  Data  ─►  Domain
(mỏng)           (use case)       (LLM/tool/    (EF)      (entity + enum)
                                   file/prompt)
                        └────────── đều được dùng Domain & Contracts ──────────┘
```

Luật bất di bất dịch (đã kiểm chứng không có vi phạm):

- **Domain** không phụ thuộc tầng nào. **Contracts** là POCO thuần.
- **Controllers chỉ gọi Application** — không truy vấn DB, không gọi LLM trực tiếp.
- **Application** điều phối: được gọi Data, Domain, Services. Mỗi thao tác người dùng = **một class, một file, một `ExecuteAsync`**. Tên: `...Query` (đọc), `...UseCase` (ghi), `...Vm` (view model).
- **Services** là việc kỹ thuật tái dùng — *không bao giờ* `using` ngược lên Application/Controllers.
- **DI đăng ký một chỗ duy nhất**: `Extensions/ApplicationServiceCollectionExtensions.cs`, chia method `AddXxx()` — mỗi nhóm khớp một thư mục.
- **namespace = đường dẫn thư mục** (`Services/Tools/Execution/Foo.cs` → `ICOGenerator.Services.Tools.Execution`).

### 4.2. Sơ đồ thư mục

```
Program.cs               # Điểm vào: Serilog bootstrap, middleware pipeline, gọi DbInitializer
Extensions/              # ApplicationServiceCollectionExtensions — NƠI DUY NHẤT đăng ký DI
Domain/                  # Entity nghiệp vụ + Enums/ + Security/PermissionCatalog. Không phụ thuộc gì.
Contracts/               # DTO hợp đồng dữ liệu (BrdDto, FsdDto, ProductBriefDto...) — POCO thuần
Data/                    # AppDbContext, DbInitializer (migrate+seed), UtcDateTimeConverter, seed data
Migrations/              # EF migrations (SQL-Server-specific, tự sinh — không sửa tay)
Application/             # Use case theo khu vực màn hình:
  Account/ Agents/ Audit/ Evals/ Feedback/ Models/ Notifications/
  Projects/ Prompts/ Quality/ Requirements/ Roles/ Settings/ Usage/
Services/
  Agents/                # Vòng lặp agent: AgentRunService, AgentInstructionProvider, AgentPromptBuilder,
                         #   InvokerBackedAIFunction (middleware bọc tool)
  Artifacts/             # Workspace & sản phẩm: WorkspacePathResolver, LocalArtifactStorage,
                         #   PocTemplate/PocAudit/PocSpec, BoschTemplateSeeder, ImplementationSourcePackager
  Budget/                # BudgetGuard/BudgetPolicy — trần chi phí LLM theo USD
  Evals/                 # Prompt eval harness: EvalRunnerService, EvalRunWorker, EvalJudgeParser
  Feedback/              # FeedbackAttachmentStore (lưu file đính kèm)
  Llm/                   # LlmClient, OpenAIChatClientFactory, ModelCallLoggingChatClient,
                         #   TokenEstimator, MaxOutputTokenResolver, LlmCost, JsonExtractor...
  Notifications/         # NotificationService + Channels/ (Teams webhook, SMTP email, Bosch Email Server API)
  Prompts/               # PromptTemplateService, DbPromptOverrideProvider, PromptFileCatalog
  Requirements/          # BAChatService, ProductBriefDraftService, RequirementDocsService + trí nhớ/parser/generator của luồng BA
    Templates/           # RequirementTemplateService, DocxTemplateWriter (sinh .docx)
  Security/              # PermissionService, RequirePermissionAttribute, AesApiKeyProtector, AuditLogger
  Settings/              # AppSettingsFileStore (đọc/ghi appsettings từ màn hình Settings)
  Tools/                 # Tool cho agent: WorkspaceTools, CommandTools, GitTools
    Abstractions/        #   interface/record hợp đồng (IToolExecutionLogger)
    Execution/           #   ToolPolicyService, ToolExecutionLogger
    Registry/            #   ToolDiscoveryService, ToolRegistry, ToolArgumentValidator
    PullRequests/        #   GitHubPullRequestPublisher, PullRequestUrlBuilder, GitRemoteUrl
  Workflows/             # WorkflowOrchestrator, AgentTaskWorker (BackgroundService), DeliveryPipeline,
                         #   WorkflowTaskPromptBuilder, TestVerdictParser, WorkflowProgressReporter
Controllers/             # 15 MVC controller mỏng (xem §12)
Views/                   # Razor views (.cshtml) — mỗi màn hình một thư mục
wwwroot/                 # css/ + js/ thuần theo màn hình (requirements.js, agent-dashboard.js...)
Prompts/                 # Template prompt .md (copy ra output khi build) — xem §10
Templates/               # BRD_Template.docx, SRS_Template.docx, FSD_Template.docx
tests/ICOGenerator.Tests # xUnit
.claude/skills/verify/   # Skill chạy end-to-end không cần SQL Server / LLM thật
```

---

## 5. Mô hình dữ liệu — toàn bộ các bảng

`Data/AppDbContext.cs` khai báo **24 DbSet**. Điểm chung cần biết trước:

- **Mọi cột `DateTime` được chuẩn hóa `Kind=Utc` khi đọc** (`UtcDateTimeConverter`) để JSON trả ra có hậu tố `Z` — tránh lệch múi giờ trên client.
- **Hầu hết enum lưu dạng chuỗi** (tên enum, ví dụ `'WaitingForHuman'`) — dễ đọc trong DB và bền khi chèn giá trị enum mới. ⚠️ Vì vậy **đừng đổi tên giá trị enum đã có dữ liệu**.
- **`AiModel.ApiKey` được mã hóa AES** bằng value-converter gắn `AesApiKeyProtector`. Protector **bắt buộc là Singleton** (EF cache model toàn cục, converter capture instance đầu tiên) — đừng đổi lifetime, đừng bật `AddDbContextPool`.

### 5.1. Nhóm lõi: Project & Agent

| Bảng | Vai trò | Điểm đáng chú ý |
|---|---|---|
| `Projects` | Dự án — gốc nối tới tài liệu, hội thoại, workflow | Ngoài metadata còn mang **bộ nhớ của luồng BA**: `ConversationSummary` + `SummarizedTurnCount` (tóm tắt hội thoại dài), `UserMemoryHarvestedTurnCount`, `RequirementCoverageMap` + `CoverageHarvestedTurnCount` (bản đồ bao phủ 12 nhóm thông tin), `ChecklistGapHarvested`; và **nghiệm thu bản demo** `PocAcceptedAtUtc` + `PocAcceptedBy` (null = người yêu cầu chưa xác nhận POC đạt). `CreatedByUsername` để lọc "chỉ thấy project mình tạo"; `OrgUnitCode` (không FK) gắn đơn vị yêu cầu; `IsUseBoschTemplate` (mặc định true) do TeamDev đổi ở Agent Dashboard |
| `Agents` | "Nhân sự AI": `RoleKey` (BusinessAnalyst/TechLead/Developer/Tester/UiUx), `AiModelId`, `Temperature`, `Color`, `LearnedChecklistNotes` | System prompt **không lưu DB** — nạp từ `Prompts/{RoleKey}/instruction.md` qua `AgentInstructionProvider`. FK sang AiModel là `Restrict` (không xóa được model đang dùng) |
| `AiModels` | Danh mục model LLM: `ModelId`, `Endpoint`, `ApiKey` (mã hóa), `ContextWindow`, đơn giá Input/Output per-1M-token (decimal 18,6) | Đơn giá là đầu vào của trang Usage + Budget guard. Model tự host giá 0 ⇒ chi phí 0 |
| `ToolDefinitions` | Danh mục tool (đồng bộ từ code khi khởi động) | Unique index `(ServiceType, MethodName)` |
| `AgentTools` | Bảng nối agent ↔ tool được phép dùng | Khóa chính kép `(AgentId, ToolDefinitionId)` |

### 5.2. Nhóm tài liệu & hội thoại

| Bảng | Vai trò | Điểm đáng chú ý |
|---|---|---|
| `ProjectDocuments` | Tài liệu sinh ra (ProductBrief/AIDesignSpec/BRD/SRS/FSD/UserStories...): `Folder`, `VersionName`, `FileName`, `FilePath`, `Content`, `IsApproved` | Cascade theo Project |
| `ProjectDocumentRevisions` | **Lịch sử nội dung** mỗi lần document bị ghi đè CÓ thay đổi — snapshot đầy đủ + `ChangeNote` nguồn gốc | Chốt chặn duy nhất tạo revision là `RequirementDocumentGenerator.UpsertDocument`. Diff tính lúc xem bằng `DocumentDiffService` (LCS theo dòng). Unique `(DocumentId, RevisionNumber)` |
| `ProjectSourceFiles` | Tài liệu nguồn user upload cho BA đọc (ảnh / PDF / Word .docx / Excel-CSV) — `ExtractedText` do `ProjectSourceIngestor` trích; PDF **scan** không có text thì lấy ảnh nhúng từng trang ra `page-{n}.png` cạnh file gốc (`ScannedPageImageCount`) cho model vision | Cascade theo Project |
| `AgentConversations` | Từng lượt hội thoại user ↔ agent trong project | Project FK Cascade, Agent FK **Restrict** (xóa agent không wipe lịch sử) |
| `AgentModelCallLogs` | Log **mỗi lời gọi model**: request/response JSON, token, thời lượng, `Purpose`, `WorkflowRunId` (cột nhóm, cố ý không FK) | Nguồn dữ liệu của trang Usage, popup AI Call Logs, Delivery Quality |

### 5.3. Nhóm workflow

| Bảng | Vai trò | Điểm đáng chú ý |
|---|---|---|
| `WorkflowRuns` | Một lần chạy quy trình cho project: `Status` (Queued/Running/WaitingForHuman/Completed/Failed/Canceled), `CurrentStage` (`WorkflowStageKey`) | Cascade theo Project; index `(ProjectId, Status, CreatedAt)` |
| `AgentTasks` | Một đầu việc giao cho một agent trong run: `Type`, `Status`, `Input`, `Output`, `Error`, `Attempt`, `RevisionFeedback` (null = task thường) | Agent FK `SetNull`, Project FK `Restrict`. **Index `(Status, CreatedAt)` phục vụ worker poll mỗi 2s** — đừng xóa |

### 5.4. Nhóm người dùng & bảo mật

| Bảng | Vai trò | Điểm đáng chú ý |
|---|---|---|
| `AppUsers` | Tài khoản đăng nhập: `Username` (unique), `PasswordHash` (PBKDF2 qua `PasswordHasher`), `Role` (SuperAdmin/Admin/TeamDev/User), `UserMemory` (hồ sơ cá nhân hóa BA học được), tùy chọn thông báo (`NotifyInApp/ByEmail/OnGate/OnCompleted/OnFailed`, `Email`) | Chưa có UI tạo user — seed 4 tài khoản cố định |
| `RolePermissions` | Cấp quyền `(Role, Permission)` — cấu hình runtime ở màn Roles | Unique `(Role, Permission)`. SuperAdmin implicit-all, không có dòng nào |
| `AuditLogs` | Nhật ký thay đổi cấu hình (Settings/Roles/Agent/Model/Prompt): actor, before/after JSON | Ghi qua `IAuditLogger` |

### 5.5. Nhóm vệ tinh

| Bảng | Vai trò |
|---|---|
| `Feedbacks` + `FeedbackAttachments` | Phản hồi người dùng toàn app (bug/góp ý/trải nghiệm) kèm file đính kèm; file gốc lưu đĩa (`Feedback:UploadRootPath`), DB chỉ giữ metadata |
| `OrgUnits` + `Associates` | Dữ liệu tổ chức đồng bộ từ HR_Portal (phòng ban, nhân sự) — nguyên liệu cho `OrganizationContextService` |
| `Notifications` | Thông báo in-app (chuông): index `(RecipientUsername, IsRead, CreatedAt)` |
| `EvalScenarios` / `EvalRuns` / `EvalResults` | Prompt eval harness (golden set + LLM-judge). Model/scenario tham chiếu bằng **Guid + snapshot tên, không FK** — xóa không mất lịch sử điểm |
| `PromptTemplateVersions` | Phiên bản prompt chỉnh runtime (Prompt Studio): snapshot đầy đủ, unique `(PromptKey, VersionNumber)`, tối đa một `IsActive` mỗi key |
| `PocComments` | Ghi chú GHIM trực tiếp lên phần tử trong POC (trang POC Review): màn hình + nhãn + CSS selector + vị trí. `Open` → gom vào "Yêu cầu chỉnh sửa" ở cổng POC → `Sent` (không gửi lặp) |

### 5.6. Migration

- Đổi entity ⇒ `dotnet ef migrations add <Tên>`; `DbInitializer` tự `MigrateAsync` lúc khởi động (SqlServer).
- Migration hiện tại là một **baseline `V1` duy nhất** (đã gộp toàn bộ lịch sử; các migration tiến lẻ tẻ trước đây không còn). Khi cần sinh migration, để `Database:Provider` là `SqlServer` (mặc định) — **đừng** đặt `Database__Provider=Sqlite` — để nó sinh theo provider SqlServer (không phải Sqlite).
- Sqlite **không chạy migration** (dùng `EnsureCreated`) ⇒ đổi schema khi dev Sqlite = xóa file `ICOGenerator.db*` để dựng lại.

---

## 6. Hai động cơ của hệ thống

Phân biệt được hai luồng này là tránh được 90% nhầm lẫn khi đọc code.

### 6.1. Động cơ 1 — Chat với BA (một request xử lý trọn lượt, STREAM kết quả)

Đường chat chính là `POST /Requirements/ChatStream` — cùng một request xử lý trọn lượt chat và trả
**Server-Sent Events**: frame `status` ("BA đang soạn câu trả lời…"), frame `token` (BA "đang gõ" —
đã lọc cú pháp JSON qua `BAChatTokenFilter`, chỉ phần `message` hiển thị được stream), và frame `done`
mang bản chốt (reply + suggestions + cờ mời Write Requirement) để client render tại chỗ **không reload
trang**. Client dùng `fetch` + đọc `ReadableStream` (EventSource không POST được); stream hỏng trước khi
nhận frame nào thì `requirements.js` tự rơi về `POST /Requirements/Chat` (postback cổ điển, reload trang).
Lượt chat chạy với `CancellationToken.None` — người dùng đóng tab giữa chừng thì turn vẫn hoàn tất và lưu
DB, chỉ việc ghi response dừng lại.

**Không lượt nào được phép "treo"** — hội thoại luôn kết thúc bằng một lượt assistant, và UI luôn có
đường thoát. Lượt user được lưu TRƯỚC khi gọi LLM, nên nếu phần sau vỡ mà không ai đóng lượt thì hội
thoại nằm lại ở "lượt cuối là user" và trang kẹt vĩnh viễn ở "BA đang soạn câu trả lời…" (F5 cũng không
thoát, không gửi được tin mới). Bốn chốt chặn:

- **Đóng lượt kiểu gì cũng đóng**: mọi ngoại lệ trong một lượt (`BAChatService.RunTurnGuaranteedAsync`,
  và nhánh catch của `AcknowledgeSourcesAsync`) đều ghi một lượt assistant ⚠️ có nút "Thử lại".
- **Nhịp tim SSE**: frame `ping` mỗi 10s trong lúc lượt chạy — client phân biệt được "BA đang nghĩ lâu"
  với "kết nối đã chết". Không có nó, một lời gọi structured-output dài trông y hệt stream đứt.
- **Đồng hồ canh phía client** (`STREAM_IDLE_TIMEOUT_MS`, 45s không nghe thấy gì ⇒ abort) + kiểm tra
  "stream kết thúc mà THIẾU frame `done`": hai kiểu đứt im lặng mà `fetch` không hề báo lỗi.
- **`GET /Requirements/ChatReplyStatus` trả `{pending, stale}`**: `stale` = lượt đang chờ đã chết —
  không tiến trình nào đang chạy nó (`BAChatTurnTracker`, sổ singleton trong bộ nhớ) và lượt user đã cũ
  hơn `BAChatService.ReplyStaleAfter` (3 phút). UI mở khóa ô nhập và mời "Thử lại"; retry lúc này chạy
  lại đúng lượt user còn "cụt" (`RetryLastTurnAsync`) nên người dùng không phải gõ lại câu hỏi.

```
Browser POST /Requirements/ChatStream (SSE)  [hoặc POST /Requirements/Chat — fallback]
  └► RequirementsController.ChatStream               [Controllers]
       └► ChatWithBAUseCase.ExecuteAsync             [Application/Requirements]
            └► BAChatService.ChatAsync               [Services/Requirements]
                 ├► OrganizationContextService       → system message "bức tranh tổ chức" (cache 1h)
                 ├► UserMemoryService                → hồ sơ user (học dần, xuyên project)
                 ├► ConversationMemoryService        → 20 lượt gần nhất nguyên văn + tóm tắt lượt cũ
                 ├► RequirementCoverageService       → bản đồ bao phủ 12 nhóm thông tin
                 ├► SourceContextBuilder             → ngữ cảnh từ tài liệu user upload (text + ảnh/ảnh trang scan)
                 ├► RequirementPromptBuilder         → dựng prompt (template Prompts/BusinessAnalyst/*)
                 ├► ILlmClient                       → gọi LLM  [Services/Llm]
                 └► BAChatReplyParser                → parse trả lời (+ cổng readiness tất định từ bản đồ bao phủ)
       └► AppDbContext.SaveChanges                   [Data] — lưu lượt hội thoại
```

Các cơ chế trí nhớ (chi tiết đầy đủ ở `ARCHITECTURE.md` §5.11–5.13):

- **Bộ nhớ hội thoại 2 tầng**: 20 lượt gần nhất gửi nguyên văn; lượt cũ gộp dần vào `Project.ConversationSummary` **theo lô ≥10 lượt** (không tóm tắt mỗi lượt — đó là chỗ tiết kiệm token). Fail-open: gọi tóm tắt lỗi thì giữ summary cũ, không mất lượt nào.
- **Bộ nhớ cấp user** (`AppUser.UserMemory`): BA chắt lọc sự thật bền về user (vai trò, lĩnh vực, văn phong...) theo lô, dùng lại ở mọi project của họ.
- **Bản đồ bao phủ yêu cầu** (`Project.RequirementCoverageMap`): 12 nhóm thông tin đánh dấu [RÕ]/[MỘT PHẦN]/[CHƯA HỎI]/[KHÔNG ÁP DỤNG] — NGUỒN CHÂN LÝ DUY NHẤT của độ sẵn sàng: BA chọn câu hỏi kế tiếp dựa vào đây, panel "Tiến độ khai thác" render nó, và cổng "Write Requirement" suy ready TẤT ĐỊNH từ nó (`RequirementReadinessGate.Evaluate`: mọi dòng áp dụng [RÕ] ⇔ cho phép) — không có lời gọi LLM nào chấm lại, nên panel/nút/lời mời không thể vênh nhau.
- **Checklist gap** (`Agent.LearnedChecklistNotes`): sau khi tài liệu sinh thành công, hệ thống rà một lần "user phải tự nêu thông tin gì mà BA chưa từng hỏi" và ghi nhớ **cho mọi project sau**.
- **Bối cảnh tổ chức**: render từ OrgUnits/Associates, chỉ dữ liệu GỘP (không PII), cache 1h. Fail-open toàn tuyến.

**Tài liệu nguồn** (`ProjectSourceIngestor`) — người dùng nghiệp vụ mô tả yêu cầu bằng thứ họ đang có, nên đường vào này quyết định chất lượng phỏng vấn:

| Định dạng | Cách đọc |
|---|---|
| Ảnh (PNG/JPG/WebP/GIF) | gửi thẳng cho model vision |
| PDF có text | bóc text từng trang (PdfPig) |
| PDF **scan** | trang không có text ⇒ lấy ảnh nhúng lớn nhất của trang ra `page-{n}.png` (`PdfScanPageRenderer`), gửi cho model vision theo đúng thứ tự trang. Không lấy được ảnh nào mới cảnh báo "không đọc được" |
| Word `.docx`/`.docm` | đoạn văn + bảng (render `ô \| ô`) theo đúng thứ tự tài liệu (`WordDocumentTextExtractor`) — quy trình/biểu mẫu phòng ban gần như luôn ở dạng này |
| Excel `.xlsx`/`.xlsm` / CSV | tiêu đề cột + vài chục dòng mẫu (`SpreadsheetTextExtractor`) |

Text bóc từ **Excel/Word** còn được nạp vào prompt sinh AI Design Spec làm **dữ liệu mẫu THẬT** (`RequirementDocsService.BuildRealSampleDataAsync`), để POC demo bằng đúng danh mục/tên của đơn vị yêu cầu thay vì "Sản phẩm A / Nguyễn Văn B".

Trang Requirements còn có **stepper 5 chặng** (Trò chuyện → Bản mô tả → Duyệt yêu cầu → Dựng bản demo → Xem & góp ý) suy TẤT ĐỊNH từ tài liệu + workflow run của chính trang, và panel **"Ví dụ đã xác nhận" sửa được tay** (`UpdateWorkedExamplesUseCase`): đây là oracle mà POC bị chấm theo, nên một ví dụ chép sai làm cả tầng tự kiểm chấm theo chuẩn sai. Lượt sửa tay ghi ĐỒNG THỜI cột `Project.WorkedExamples` và một lượt hội thoại — thiếu lượt hội thoại thì `InterviewOutlookService` sẽ viết đè bản sửa về cách hiểu cũ ở lượt chat kế tiếp.

**Lượt hỏi GỘP (2–4 câu hỏi độc lập một lượt).** Phỏng vấn được thiết kế "mỗi lượt một câu hỏi" và cổng readiness chỉ mở khi MỌI nhóm áp dụng đã `[RÕ]` — hai điều đúng về chất lượng nhưng cộng lại thành hàng chục lượt chat, và người dùng nghiệp vụ bận thì bỏ dở chứ không có cách nào rút ngắn. Bản trước rút ngắn bằng cổng **"chốt nhanh phần còn lại"**: BA tự soạn một phương án cho mỗi nhóm còn trống, người dùng duyệt một lần. Cổng đó **đã bỏ**, vì nó rút ngắn ở sai chỗ — phương án do BA soạn được ghi vào hội thoại **như lời của chính người dùng**, nên bản đồ bao phủ đầy lên mà không ai thật sự trả lời câu nào, và mọi tầng phía sau (Product Brief, spec, POC, UAT) tin đó là điều người dùng đã nói. Với hội thoại còn ngắn thì phần lớn phương án là BA phỏng đoán theo thông lệ, tức là tài liệu của BA đoán, ký tên người dùng.

Nay thứ được rút ngắn là **số vòng đi-về**, không phải độ sâu khai thác: BA vẫn là người HỎI, người dùng vẫn là người TRẢ LỜI, nhưng một lượt chở được nhiều câu hỏi.

- **Phép thử để được gộp** (`BusinessAnalyst/requirement-chat.v4.md`): *câu trả lời của câu này có làm ĐỔI câu hỏi kế tiếp không?* Không đổi ⇒ được gộp (các nhóm rời nhau: quy mô sử dụng, thông báo, báo cáo, dữ liệu & danh mục, phân quyền). Có đổi ⇒ **phải hỏi một mình**: xin câu chuyện thật, đào ngoại lệ, chốt ví dụ số, chốt kịch bản luồng, gỡ mâu thuẫn, nhịp tóm tắt kiểm chứng. Gộp mấy câu đó là mất đúng cái phễu mở → đào sâu → chốt.
- **Trần cứng 4 câu/lượt, chặn TẤT ĐỊNH ở `BAChatReplyParser`** — không chỉ dặn trong prompt. Model luôn có xu hướng gộp tối đa để "xong sớm", và một lượt 12 câu hỏi chính là cổng chốt nhanh đội lốt phỏng vấn. Trần áp ở **cả hai** đường vào: `Parse` (model trả text) và `Normalize` (structured output trả thẳng `BAChatReply` — đường mặc định của các model tốt, nếu chỉ chặn trong `Parse` thì đúng những model đó không bị chặn).
- **Contract**: `BAChatReply.Questions` (`BAChatQuestion[]`: nhóm + câu hỏi + gợi ý riêng + cờ chọn-nhiều), lưu ở cột `AgentConversation.Questions` (mã hóa at rest như `Message`/`Suggestions`). Lượt hỏi một câu vẫn đi đường cũ (`message` + `suggestions`) — đó là ca thường gặp nhất VÀ là ca bắt buộc của mọi câu hỏi đào sâu, nên nó không đổi gì. `Normalize` giữ hai đường **loại trừ nhau**: có thẻ hỏi thì không có chip lượt-đơn (chip bấm là GỬI NGAY, để cả hai cùng sống thì một cú bấm cướp lượt trước khi người dùng kịp trả lời các câu còn lại), và một lượt "gộp" chỉ có một câu bị **hạ về** đường một-câu với câu hỏi nối vào `message`.
- **UI**: thẻ nhiều dòng trong khung chat (`.batchq`), mỗi dòng là một câu hỏi + gợi ý bấm + "Ý khác — tôi tự nhập"; nút gửi đếm live số câu đã trả lời và nói rõ **không cần trả lời hết** (câu để trống thì BA hỏi tiếp ở lượt sau). Render ở CẢ hai đường — server lúc tải trang, JS ở frame `done` — vì F5 giữa chừng mà thẻ biến mất thì người dùng mất các câu chưa trả lời, và `message` của lượt gộp chỉ là câu dẫn.
- **Không có endpoint riêng**: cả cụm được soạn thành MỘT tin nhắn `- câu hỏi: trả lời` rồi gửi qua đúng đường chat thường. Nhờ vậy không có đường ghi thứ hai nào lệch khỏi luồng chính, và mọi thứ đã đúng ở lượt chat (cổng readiness, chắt lọc bản đồ bao phủ, decision log) tự khắc đúng ở đây. `ConversationTurnRenderer` render cả các câu hỏi vào transcript — thiếu nó thì reader chỉ thấy câu trả lời mà không biết nó trả lời cho câu nào.

**Chuẩn `[RÕ]` được siết ở `BusinessAnalyst/requirement-coverage.v3.md`.** Lượt gộp làm người dùng dễ trả lời ngắn hơn, nên "giám khảo" của cổng phải khắt khe hơn ở đúng chỗ một câu khẳng định chung chung có thể trôi qua: ngoại lệ phải có **một tình huống hỏng cụ thể kèm cách xử lý**; quy tắc nghiệp vụ phải có **điều kiện và hệ quả**; vòng đời phải **gọi tên các trạng thái** và điều kiện chuyển; thông báo phải rõ **ai nhận, khi nào**; phân quyền phải rõ **vai nào làm/xem được gì** ("phân quyền theo vai trò" là nhắc lại tên nhóm, không phải câu trả lời). Thêm hai điều **không được tính là căn cứ**: (1) lời của BA mà người dùng chưa xác nhận — trích dẫn `{nguồn: …}` phải lấy từ lượt của NGƯỜI DÙNG hoặc tài liệu nguồn, vì một dòng `[RÕ]` sai thì BA sẽ không bao giờ hỏi lại nhóm đó nữa; (2) một tiếng "có/không" trả lời cho một câu hỏi MỞ. Hai chuẩn cũ (định lượng phải có ví dụ số, luồng/trạng thái phải có chuỗi bước xác nhận) giữ nguyên.

**"Write Requirement"** chỉ sinh **Product Brief** (ngôn ngữ đời thường, dạng draft — user sửa đi sửa lại không đốt token bản kỹ thuật). Chạy dưới dạng workflow run một-bước loại `RequirementAnalysis` với tiến độ live (xem 6.3).

**"Approve"** (`ApproveRequirementUseCase`): promote Product Brief lên `V{n}`, rồi khởi động run nền **AiDesignSpec** (một bước, BA sinh bản kỹ thuật từ Product Brief đã duyệt — chạy nền để màn hình không treo chờ LLM).

**Cổng xác nhận giả định** (giữa spec và POC): spec được phép tự quyết những điều Product Brief không nói (mục `## 12. Assumptions`). Nếu có giả định nào, worker **KHÔNG** khởi động Delivery Pipeline mà đánh dấu `Project.PendingAssumptionsVersion` — trang Requirements đổi panel giả định thành cổng có nút bấm:

- **"Tất cả đúng — dựng bản demo"** → `ConfirmSpecAssumptionsUseCase`: gỡ cổng rồi `StartDeliveryWorkflowAsync` (đúng lời gọi worker vẫn tự chạy trước đây).
- **"Sửa các điểm đã đánh dấu"** → `ReviseSpecAssumptionsUseCase`: ghi đính chính vào **cả** hội thoại BA (nguồn sự thật cho bản đồ bao phủ/decision log) **lẫn** `Project.SpecAssumptionCorrections` (đường tất định nạp vào prompt sinh spec — spec sinh từ Brief chứ không đọc transcript), rồi sinh LẠI spec; cổng dựng lại ở lượt sinh mới.

Lý do đặt cổng ở đây chứ không sau POC: một giả định sai chỉ lộ ra khi xem POC là đã tốn trọn lượt dựng đắt nhất tuyến (5–15 phút), trong khi rà vài dòng chữ mất vài giây. Spec không có giả định nào ⇒ chạy thẳng sang Delivery Pipeline như trước (§7).

### 6.2. Động cơ 2 — Pipeline nền (bất đồng bộ, qua hàng đợi)

```
IWorkflowOrchestrator.Start...WorkflowAsync           [Services/Workflows]
   tạo WorkflowRun + AgentTask đầu tiên (Status=Queued)

AgentTaskWorker : BackgroundService                    — dispatch mỗi 2 GIÂY
   lấy các AgentTask Queued cũ nhất (index (Status, CreatedAt)), tối đa
   Workers:MaxConcurrentAgentTasks task chạy SONG SONG — mỗi PROJECT một task
   một thời điểm (chung workspace); claim Queued→Running nguyên tử qua
   concurrency token (AgentTask.Status) nên không bao giờ chạy đôi một task
     └► AgentRunService.RunAsync(projectId, agentId, prompt)   [Services/Agents]
          Microsoft Agent Framework tự lo vòng: LLM ⇄ tool cho tới khi xong
     └► cập nhật Task.Output; còn bước kế → run dừng WaitingForHuman (cổng duyệt)
        hết bước → Completed; lỗi → Failed
```

Điểm cốt lõi: **worker là generic** — nó không biết gì về từng vai. "Ai làm sau ai" nằm ở dữ liệu khai báo `DeliveryPipeline.Steps`; việc enqueue bước kế nằm ở `ApproveStageUseCase` (vì giữa các bước có cổng duyệt). Mặc định `MaxConcurrentAgentTasks = 1` (tuần tự như trước — an toàn cho LLM tự host); tăng lên khi endpoint model chịu tải song song để nhiều project không phải xếp hàng chờ nhau.

**Chống double-submit ở cổng duyệt:** `WorkflowRun.Status` và `AgentTask.Status` là **concurrency token** (EF thêm `AND Status = @original` vào mọi UPDATE — không đổi schema, chạy được cả Sqlite). Hai người cùng bấm Duyệt/Chỉnh sửa/Từ chối một cổng thì chỉ một bên thắng; bên thua nhận `DbUpdateConcurrencyException` và các use case cổng duyệt trả về "không còn bước chờ duyệt" thay vì enqueue task trùng.

### 6.3. Tiến độ realtime

`WorkflowProgressReporter` (singleton, in-memory) nhận event tiến độ từ agent run (bước "thinking", tool call, token) và đẩy ra UI qua:
- `GET /Requirements/WorkflowStatus?projectId=&runId=&afterSeq=` — poll JSON tăng dần theo `afterSeq`;
- `GET /Requirements/WorkflowStream` — Server-Sent Events;
- Agent Dashboard có bộ endpoint tương tự (`/AgentDashboard/WorkflowStatus`, `ActiveAgents`, `AgentActivity`...).

Vì reporter là in-memory, **restart app là mất tiến độ live** (trạng thái bền vẫn nằm trong DB).

---

## 7. Delivery Pipeline chi tiết

Pipeline là **dữ liệu khai báo** ở `Services/Workflows/DeliveryPipeline.cs` — thêm/chèn vai = thêm một dòng, không sửa worker.

### 7.1. Bảng các bước (thứ tự phần tử = thứ tự hand-off)

| # | Stage (`WorkflowStageKey`) | Agent | `AgentTaskType` | Input | MaxSteps | Prompt template |
|---|---|---|---|---|---|---|
| 1 | `PocPreview` | Developer | `PocPreview` | AI Design Spec | 18, **nới theo số màn hình** của spec\*\* | `Developer/poc-preview.v1.md` |
| 2 | `TechnicalDocs` | BA | `TechnicalDocs` | AI Design Spec | (8, không tiêu thụ*) | `BusinessAnalyst/technical-docs.v1.md` |
| 3 | `ArchitectureDesign` | Tech Lead | `ArchitectureDesign` | AI Design Spec | 8 | `TechLead/architecture-design[-bosch].v1.md` |
| 4 | `Implementation` | Developer | `Implementation` | Output bước trước | 40 | `Developer/implementation[-bosch].v1.md` |
| 5 | `CodeReview` | Tech Lead | `CodeReview` | Output bước trước | 12 | `TechLead/code-review.v1.md` |
| 6 | `Testing` | Tester | `Testing` | Output bước trước | 8 | `Tester/testing.v1.md` |
| 7 | `PullRequest` | Developer | `PullRequest` | Output bước trước | 6 | `Developer/pull-request.v1.md` |

\*\* POC dựng qua nhiều call nhỏ (một `AppendPocContent` cho MỖI màn hình), nên ngân sách bước suy từ số màn hình spec khai báo — `DeliveryPipeline.PocStepBudget` (≈ `10 + 1.5×số màn hình`, trần `PocMaxStepsCeiling = 30`). Chỉ NỚI, không siết dưới con số khai báo: `MaxSteps` là trần chứ không phải mức tiêu.

\* Bước TechnicalDocs **không** chạy qua agent + prompt chung: worker xử lý nhánh riêng, gọi `RequirementDocsService.GenerateTechnicalDocsAsync` (BA cần đọc context project) — sinh BRD/SRS/FSD/UserStories từ Product Brief + AI Design Spec đã duyệt.

Ngoài chuỗi tuyến tính còn **`BugFixStep`** (Developer, `BugFix`, MaxSteps 30) — cố tình không nằm trong `Steps` vì nó là chu trình quanh Testing (xem 7.3).

### 7.2. Cổng duyệt (gates) — trạng thái `WaitingForHuman`

Mỗi bước chạy xong, run **dừng** ở `WaitingForHuman`. Trên **Agent Dashboard** (yêu cầu quyền `DeliveryAdvance`), người duyệt có 4 lựa chọn:

| Hành động | Use case | Hệ quả |
|---|---|---|
| **Duyệt & tiếp tục** | `ApproveStageUseCase` | Resolve input theo `InputSource` (spec hoặc output task Completed mới nhất — tức bản đã-sửa nếu có revision) → enqueue bước kế |
| **Yêu cầu chỉnh sửa** (kèm nhận xét) | `RequestStageRevisionUseCase` | Enqueue lại **đúng bước hiện tại**: `Input` giữ NGUYÊN BẢN, nhận xét nằm riêng ở `AgentTask.RevisionFeedback`; prompt gốc + nối khối `Shared/revision.v1.md`. Trần `MaxRevisionRounds = 3` mỗi bước (đếm bằng số task có `RevisionFeedback != null` cùng loại trong run). **Riêng cổng POC**: popup còn gom các ghi chú GHIM trực tiếp trên POC (`PocComments` Open, từ trang POC Review — xem §11.2) vào nhận xét, kèm màn hình + CSS selector từng phần tử để Developer sửa đúng chỗ; ghi chú đã gom chuyển `Sent`, và khi có ghi chú gửi kèm thì nhận xét gõ tay được phép trống |
| **Từ chối** | `RejectStageUseCase` | Hủy run (`Canceled`) — quay về chat BA sửa requirement, Approve lại tạo run phiên bản kế. **Ngoại lệ: cổng POC không Reject được** (`PocGateNotRejectable`) — POC sai nghĩa là requirement sai, việc của user; "Yêu cầu chỉnh sửa" thì vẫn được |
| **Thử lại** | `RetryWorkflowUseCase` | Chạy lại khi task Failed |

Triết lý: *xem trước rẻ (POC) → chốt từng cổng → mới đầu tư bước đắt (full code)*. Kết quả chỉ *gần* đúng thì đừng Reject — dùng "Yêu cầu chỉnh sửa", rẻ hơn nhiều.

### 7.3. Chu trình tự sửa lỗi Testing ↔ BugFix (không cần cổng duyệt)

Tester **bắt buộc** chốt dòng máy-đọc-được `VERDICT: PASS` / `VERDICT: FAIL` cuối báo cáo (`TestVerdictParser` — khoan dung hoa/thường, `**bold**`, `:`/`=`; không rõ ⇒ coi như PASS).

```
Testing ──FAIL──► BugFix (Developer sửa) ──► Testing (kiểm lại) ──► ...
   │                                  (tối đa MaxBugFixAttempts = 3 vòng)
   └──PASS──► sang cổng duyệt bước kế (PullRequest)
```

Worker xử lý chu trình này trong `TryAdvanceTestFixCycleAsync` (set run về `Queued`, tự chạy tiếp — không chờ người). Số vòng đếm bằng số task `BugFix` trong run.

### 7.4. Bước Pull Request

Developer tạo nhánh feature, commit, push (qua GitTools), rồi `OpenPullRequest`:
- Có `PullRequest:GitHubToken` + remote là github.com ⇒ **tạo PR thật** qua GitHub REST API (`GitHubPullRequestPublisher`).
- Không ⇒ fallback trả **link compare** sẵn-mở-PR theo nhà cung cấp Git (GitHub/GitLab/Azure DevOps/Bitbucket — `PullRequestUrlBuilder`).

### 7.5. Vòng đời một AgentTask

```
Queued ──worker nhặt──► Running ──xong──► Completed
                          │                └► (còn bước kế? run = WaitingForHuman : run = Completed)
                          ├─lỗi──► Failed (run Failed; RetryWorkflow enqueue lại)
                          └─app restart──► DbInitializer re-queue (Attempt++ trước đó; quá 3 lần ⇒ Failed)
```

---

## 8. Agent & hệ thống Tool

### 8.1. Vòng lặp agent — `AgentRunService.RunAsync`

Chạy trên **Microsoft Agent Framework** (`ChatClientAgent` + `AgentSession`) — framework tự lo vòng ReAct (gọi model → gọi tool → lặp), code app **không có vòng `for` tự viết**. Ngân sách bước mô phỏng qua `FunctionInvokingChatClient.MaximumIterationsPerRequest` trong **3 pha** trên cùng một session:

1. Chạy trong ngân sách kỳ vọng (`MaxSteps` của bước pipeline).
2. Chưa xong ⇒ nhắc "hoàn tất nốt", cấp thêm tới trần cứng (`maxSteps × AutoContinueFactor`).
3. Vẫn chưa xong ⇒ một lượt **salvage** không-tool để chốt tóm tắt phần đã làm (file đã nằm trên đĩa) thay vì fail trắng.

Cross-cutting concerns là **middleware**, không nằm trong vòng lặp:

- `ModelCallLoggingChatClient` (`DelegatingChatClient`) — mỗi lời gọi model: đặt deadline, tính trần completion-token, dựng `LlmCallResult` + map lỗi API/timeout, ghi log DB (`IModelCallLogger` → `AgentModelCallLogs`), đẩy progress. **Dùng chung** cho cả đường chat BA (`LlmClient`) — logic không viết lặp hai nơi.
- `InvokerBackedAIFunction` (`DelegatingAIFunction`) — bọc mỗi tool: schema/bind/invoke do `AIFunctionFactory` lo; wrapper chồng thêm report tiến độ, `ToolPolicyService` (kiểm tra tool có được cấp cho agent), `IToolExecutionLogger`, và `ToolArgumentValidator` — call thiếu đối số bắt buộc (thường do args bị cắt vì `finish_reason=length`) bị **từ chối** kèm observation yêu cầu gọi lại, thay vì bind null làm hỏng dữ liệu âm thầm.

### 8.2. Tool = một method C# public có `[Description]`

Không có interface `IAgentTool`, không adapter. `ToolDiscoveryService` quét các class trong `ToolDiscoveryService.ToolTypes`, đồng bộ vào bảng `ToolDefinitions`; `AIFunctionFactory` sinh JSON schema từ chữ ký method.

**Danh mục tool hiện có:**

| Nhóm | Tool | Chức năng |
|---|---|---|
| `WorkspaceTools` | `ListFiles` | Liệt kê file trong workspace |
| | `ReadFile(relativePath, offset)` | Đọc file (<200KB trả full; lớn hơn phân trang theo `offset`) |
| | `WriteFile(relativePath, content)` | Ghi một file |
| | `WriteFiles(files[])` | Ghi **nhiều file một lần** — quan trọng cho bước Implementation để không đốt hết budget từng file lẻ |
| | `SearchFiles(keyword)` | Tìm file theo keyword trong đường dẫn |
| | `ReplaceInFile(relativePath, oldText, newText)` | Thay text trong file có sẵn |
| | `SetPocContent` / `AppendPocContent` | Ghi/nối vùng HTML tính năng (`POC_CONTENT`) của `04_Implementation/poc-demo.html` — nối nhiều call nhỏ để không bị cắt token |
| | `SetPocScript` / `AppendPocScript` | Ghi/nối vùng JS nghiệp vụ (`POC_SCRIPT`) — hiện thực business rules thật (tính toán, chuyển trạng thái, mô phỏng vai) |
| | `AuditPocContent` | Tự soát POC: menu thiếu section, id trùng, modal trỏ id không tồn tại, CRUD lệch field, script rỗng, **độ phủ so với AI Design Spec** — agent phải sửa hết ISSUE rồi audit lại (tối đa 3 vòng) |
| `CommandTools` | `RunCommand(command)` | Chạy lệnh shell **trong whitelist `AllowedCommands`**, timeout `Commands:TimeoutSeconds` (120s) |
| `GitTools` | `GitStatus`, `GitDiff` | trạng thái / diff --stat |
| | `CreateBranch(branchName, baseBranch)` | Tạo + checkout nhánh |
| | `GitCommit(message)`, `PushBranch(branchName)` | Commit / push |
| | `OpenPullRequest(branchName, title, body)` | Push + tạo PR thật (có token) hoặc trả link compare |

**Tool mặc định theo vai** (gán trong `DbInitializer.AssignDefaultToolsAsync`):

| Vai | Tools |
|---|---|
| BA | ListFiles, ReadFile, WriteFile, SearchFiles |
| Tech Lead | ListFiles, ReadFile, WriteFile, GitDiff, GitStatus |
| Developer | Tất cả Workspace + POC tools, RunCommand, GitStatus, GitCommit, CreateBranch, PushBranch, OpenPullRequest |
| Tester | ListFiles, ReadFile, WriteFile, RunCommand |
| UI/UX | WriteFile, ReadFile, ListFiles |

**Thêm tool mới** = viết một method public có `[Description]` trong một class `*Tools` (class mới thì thêm vào `ToolDiscoveryService.ToolTypes`), rồi gán cho vai trong `AssignDefaultToolsAsync` (hoặc tick trong UI Agents). Không phải sửa vòng lặp agent.

### 8.3. Rào chắn an toàn của tool

- `AllowedCommands` (appsettings): `RunCommand` chỉ chạy lệnh bắt đầu bằng các entry này (`dotnet`, `git status`, `npm`...).
- `AllowedFileExtensions`: tool file chỉ đụng các đuôi cho phép.
- `WorkspacePathResolver.GetSafeFullPath`: chống path-traversal *và* chống symlink escape (resolve tổ tiên sâu nhất tồn tại rồi kiểm tra lại nằm trong workspace).
- `ToolPolicyService`: kiểm tra tool có nằm trong tập được cấp cho agent đó.
- `ToolExecutionLogger`: ghi log mỗi lần gọi tool.

---

## 9. Tầng LLM

### 9.1. Đường đi của một lời gọi model

```
LlmClient / AgentRunService
  └► IChatClientFactory (OpenAIChatClientFactory) — dựng IChatClient theo AiModel
       ├► HttpClient "direct"  (UseProxy=false)        — cho endpoint localhost
       ├► HttpClient "proxied" (Llm:Proxy — mặc định tắt trong appsettings) — khi ngồi sau proxy công ty
       │     cả hai: Timeout = Infinite (deadline per-call do CancellationToken lo)
       │     + LlmRequestCompatibilityHandler (chèn field "thinking" cho endpoint tương thích; với OpenAI chính thức thì bỏ "thinking" và bỏ "temperature" cho reasoning model o-series/gpt-5)
       └► ChatClientBuilder compose ModelCallLoggingChatClient (middleware chung):
             deadline • trần completion-token (MaxOutputTokenResolver + TokenEstimator)
             • map lỗi API/timeout thành LlmCallResult • ghi AgentModelCallLogs • progress
```

- **`ILlmClient.ChatAsync`** — đường chat thuần (BA). **`ChatStructuredAsync<T>`** — xin API ép JSON, **opt-in theo từng model** qua `AiModel.StructuredOutputMode` (dropdown ở trang Models, mặc định `None` vì nhiều server local từ chối `response_format`): `None` không gửi gì · `JsonObject` gửi `response_format: json_object` và **vẫn stream token** · `JsonSchema` gửi schema sinh từ `T` (chỉ endpoint OpenAI thật, **không stream**). DeepSeek chỉ nhận tới mức `JsonObject`. JSON không khớp kiểu mong đợi ⇒ trả `value=null` để caller fallback về parser tay (`RequirementResponseParser`/`BAChatReplyParser`); endpoint từ chối `response_format` ⇒ tự gọi lại bằng text thuần — không bao giờ fail trắng.
- **`LlmCost`** tính chi phí = token × đơn giá model — cùng công thức cho trang Usage và Budget guard.
- **`IBudgetGuard`** kiểm tra **trước mỗi lời gọi** (cả agent lẫn BA chat): chạm trần (`Budget:*`) ⇒ từ chối gọi, ném `BudgetExceededException` với lý do.
- **`JsonExtractor`/`JsonDefaults`** — tiện ích bóc JSON từ trả lời văn xuôi.

### 9.2. Thêm một model mới

Màn hình **AI Models** → Create: điền `Name`, `Provider`, `ModelId`, `Endpoint` (base URL OpenAI-compatible), `ApiKey`, `ContextWindow`, đơn giá (0 nếu tự host). Model gán cho agent nào là do màn **Agents** quyết định. Không cần đụng code.

Modal Add/Edit có nút **Test Connection**: gọi thử một request chat cực nhỏ (prompt `"ping"`, chặn ở 16 token đầu ra) tới endpoint đang gõ và hiện ngay kết quả (OK + thời gian phản hồi, hoặc lỗi kèm status/nguyên nhân) — không cần lưu model rồi đi chạy agent mới biết cấu hình sai. Lời gọi thử KHÔNG ghi call log, không tính vào budget; trên form Edit để trống `ApiKey` thì nó dùng key đã lưu. Deadline riêng: `Llm:TestConnectionTimeoutSeconds` (mặc định 30s).

---

## 10. Hệ thống Prompt

### 10.1. Nguồn prompt & độ phân giải

Prompt gốc là file `.md` dưới `/Prompts` (copy ra output khi build). `PromptTemplateService.Get(key)` giải theo thứ tự:

1. Hỏi `IPromptOverrideProvider` (`DbPromptOverrideProvider`) — bản **active** trong bảng `PromptTemplateVersions` (sửa runtime qua **Prompt Studio**, cache IMemoryCache 30s, ghi là invalidate ngay). **Fail-open**: DB lỗi ⇒ rơi về file.
2. Không có override ⇒ nội dung file.

Nghĩa là: sửa prompt qua Prompt Studio **có hiệu lực ngay không cần deploy**, và app không bao giờ hỏng vì bảng version.

### 10.2. Danh mục prompt

| File | Dùng cho |
|---|---|
| `BusinessAnalyst/requirement-chat.v4.md` | Lượt chat BA |
| `BusinessAnalyst/product-brief.v3.md` | Sinh Product Brief (Write Requirement) |
| `BusinessAnalyst/product-brief-review.v2.md` | Vòng tự soát Product Brief |
| `BusinessAnalyst/ai-design-spec.v1.md` | Sinh AI Design Spec sau Approve — gồm mục `## 14. Acceptance Criteria` chép NGUYÊN VĂN các dòng "Hoàn thành khi: …" của Product Brief đã duyệt (`BriefAcceptanceCriteria`); `SpecBriefParityChecker` soát ba tầng màn hình/quy tắc/câu nghiệm thu và cho BA sửa một vòng nếu lệch |
| `BusinessAnalyst/uat-scenarios.v1.md` | Sinh kịch bản nghiệm thu (UAT) từ spec TRƯỚC khi dựng POC — mỗi `AC-n` phải có ≥1 kịch bản (`acRefs`), thiếu thì chạy một vòng bổ sung |
| `BusinessAnalyst/technical-docs.v1.md` | Sinh BRD/SRS/FSD/UserStories (bước 2 pipeline) |
| `BusinessAnalyst/conversation-summary.v1.md` | Gộp tóm tắt hội thoại (bộ nhớ dài hạn) |
| `BusinessAnalyst/user-memory.v1.md` | Chắt lọc hồ sơ user |
| `BusinessAnalyst/checklist-gap.v1.md` | Rút "khoảng trống checklist" sau khi sinh tài liệu |
| `BusinessAnalyst/requirement-coverage.v3.md` | Cập nhật bản đồ bao phủ yêu cầu — kiêm "giám khảo" của cổng "Write Requirement" (ready suy tất định từ bản đồ, không có prompt readiness riêng) |
| `BusinessAnalyst/organization-context.v2.md` | Khung render bức tranh tổ chức |
| `TechLead/architecture-design[-bosch].v1.md`, `TechLead/code-review.v1.md`, `Developer/poc-preview.v1.md`, `Developer/implementation[-bosch].v1.md`, `Developer/bugfix.v1.md`, `Developer/pull-request.v1.md`, `Tester/testing.v1.md` | Từng bước pipeline theo vai (`{{input}}` = nội dung theo `InputSource`); bản `-bosch` dùng khi `Project.IsUseBoschTemplate` |
| `Shared/revision.v1.md` | Khối "Yêu cầu chỉnh sửa" nối sau prompt gốc của bước |
| `{BusinessAnalyst,TechLead,Developer,Tester,UiUx}/instruction.md` | **System prompt theo vai** — hành vi sâu của agent nằm ở đây; template task theo vai chỉ mô tả *việc của bước* |
| `Shared/tool-agent-native.v1.md` | Khung prompt chung cho agent chạy tool |
| `Eval/judge.v1.md` | LLM-judge chấm điểm eval 1–5 |
| `Design/poc-template.html` | Shell HTML của POC (sidebar/topbar/Bootstrap + engine `data-crud-*`, hai vùng marker `POC_CONTENT`/`POC_SCRIPT`) |

### 10.3. Prompt Studio (màn hình `Prompts`)

- Danh sách template + nguồn đang dùng (File / DB v{n}); trang chi tiết: editor, "Lưu & kích hoạt", lịch sử, rollback ("Kích hoạt" bản cũ), "Quay về file"; trang **Diff** giữa hai mốc (mốc 0 = file).
- Lần lưu đầu tiên tự chụp nội dung file làm v1 (baseline) nên luôn diff được về gốc; nội dung trùng thì không snapshot.
- **Gắn với eval**: mỗi `EvalResult` snapshot `PromptVersionId/Number` ⇒ trang chi tiết template có bảng "Điểm eval theo phiên bản" — nhìn là biết bản nào tốt hơn. Export ra `.md` để đồng bộ ngược về repo; "Nạp từ file" cho chiều ngược lại.
- Mọi thao tác ghi vào Audit Log (category `Prompt`). Quyền: `PromptView`/`PromptManage`.

---

## 11. Workspace & sản phẩm sinh ra

### 11.1. Bố cục

Mỗi project một thư mục dưới `AgentWorkspace:RootPath`, tên = `{tên-đã-chuẩn-hóa}-{8-ký-tự-đầu-của-Id}` (không đụng nhau khi hai tên chuẩn hóa giống nhau):

```
{RootPath}/{project-key}/
  01_Requirement/     # Product Brief (draft/ + V1, V2...), BRD/SRS/FSD/UserStories
  02_Design/          # AI Design Spec theo V{n}
  03_Architecture/    # Đề xuất kiến trúc của Tech Lead
  04_Implementation/  # poc-demo.html (POC) + src/ (code đa file) + code-review.md
  05_Test/            # Test cases + báo cáo test
```

(Danh sách phase khai báo ở `Services/Artifacts/ProjectWorkspaceLayout.cs`; mỗi phase có `draft/` và các thư mục version `V{n}`.)

### 11.2. POC demo

- File `04_Implementation/poc-demo.html` — seed từ `Prompts/Design/poc-template.html` ở bước PocPreview; hai vùng marker do `PocTemplate.cs` quản: `POC_CONTENT` (HTML) và `POC_SCRIPT` (JS; shell expose `window.pocToast`/`window.pocNavigate`).
- Yêu cầu của bước POC: hiện thực **Business Rules của spec thành hành vi thật** (tính toán, validate, chuyển trạng thái, mô phỏng vai) chứ không chỉ màn hình tĩnh; agent tự soát bằng `AuditPocContent` (`PocAudit.cs` đối chiếu cả độ phủ với "Screens To Generate" + "BR-n" của spec, do `PocSpec.cs` parse).
- **Kiểm ở hai bề rộng**: `PocRuntimeChecker` đi qua từng màn hình ở 1440px rồi mở lại toàn bộ ở **390px** (điện thoại) — tràn ngang ở bề rộng nào cũng thành ISSUE, và ảnh mobile cũng được đưa cho tầng Visual QA. Trước đây mọi thứ chỉ kiểm ở desktop nên lớp lỗi "vỡ trên màn hẹp" không cổng nào thấy.
- **Lượt CLICK MENU** (`PocRuntimeChecker.CheckNavClickRoutingAsync`, chạy sau lượt lái UAT): bấm THẬT từng mục menu đang hiển thị và so màn hình đang mở với nhãn mục đó. Lượt đi màn hình ở trên gọi `window.pocNavigate()` bằng JS nên nó MÙ với lớp lỗi "click menu chết" — script nghiệp vụ dựng lại sidebar (mô phỏng vai) làm mất handler của shell, hoặc gắn handler riêng gọi `pocNavigate()` ngay trong lúc xử lý click của chính mục đó (click tổng hợp lồng nhau bị cờ *click in progress* của DOM nuốt). Người xem demo thấy breadcrumb đổi mà nội dung đứng yên; nay thành ISSUE. Bản thân shell cũng đã sửa: nav bắt click bằng **delegation** và `pocNavigate` gọi thẳng hàm mở màn thay vì `item.click()`.
- **Dữ liệu mẫu THẬT + ngôn ngữ UI** (`PocSampleDataCheck`, chạy trong `PocAudit`): text bóc từ Excel/Word người dùng đính kèm vốn CHỈ được nạp vào prompt sinh spec (`RealSampleDataReader` — cùng hàm cho cả hai đầu) mà không có gì kiểm chứng, nên POC vẫn dễ demo bằng "Sản phẩm A / Nguyễn Văn B" — lớp lỗi rẻ nhất để sửa nhưng đắt nhất về niềm tin: người dùng nghiệp vụ mở demo thấy dữ liệu bịa là mất tin, dù mọi công thức đều đúng. Ba phép scan tất định trên vùng `POC_CONTENT` (không tính shell — shell có chữ mẫu riêng), và cố tình dè dặt (chỉ ISSUE khi bằng chứng rõ ràng):
  - **Không dùng gì từ tài liệu** ⇒ ISSUE (kèm vài giá trị thật để agent seed lại); dùng ít ⇒ WARNING; không có tài liệu nào ⇒ bỏ qua hẳn.
  - **Placeholder kinh điển** ("Nguyễn Văn A", "Product B", "Lorem ipsum", `@example.com`) ⇒ ISSUE khi ĐÃ có tài liệu thật để dùng, WARNING khi không.
  - **Spec tiếng Việt mà chữ HIỂN THỊ của POC không có lấy một dấu** ⇒ ISSUE. Chỉ tính chữ hiển thị: một `data-view="Đăng nhập"` không chứng minh nhãn là tiếng Việt.
- **Lịch sử các vòng dựng** (`PocSnapshots`): mỗi task `PocPreview` xong thì `poc-demo.html` được chụp thành `04_Implementation/poc-history/poc-demo.V{n}.html` (giữ 10 bản mới nhất). Vòng "Yêu cầu chỉnh sửa" ghi đè thẳng lên bản hiện tại, nên không có bản chụp thì người nghiệm thu ở vòng sau chỉ còn bản bàn giao bằng chữ của agent để tin. Trang POC Review liệt kê các vòng (mở lại qua `Mockup?version=n` — cùng quyền + sandbox, số vòng chỉ dùng để tra trong danh sách file có thật) kèm diff **màn hình thêm/bỏ** so với vòng liền trước. Dựng lại POC từ đầu ⇒ `PocSnapshots.Reset` chạy cùng `PocVerification.Reset`.
- **Chống hồi quy giữa các vòng sửa**: `poc-verification.json` giữ vòng kiểm mới nhất, các vòng cũ rơi vào `poc-verification-history.json`. Mỗi lượt audit so với vòng trước (`PocVerification.DetectRegressions`) và báo mục từng PASS mà nay FAIL **hoặc biến mất** (xoá assertion cũng bị tính là hồi quy) — mục `REGRESSIONS` trong báo cáo cho agent, và một khối riêng trên trang POC Review. Khi POC được dựng lại từ đầu, `PocVerification.Reset` xoá cả hai file để không so với một bản POC không còn tồn tại.
- Xem POC: `GET /Projects/Mockup?projectId=` — endpoint **sandbox riêng** (HTML do LLM sinh không được thả vào layout chính).
- **Review POC (ghim ghi chú lên phần tử)**: `GET /Projects/PocReview?projectId=` nhúng POC trong iframe ở chế độ review (`Mockup?review=True` tiêm `wwwroot/js/poc-annotator.js` lúc phục vụ — file trên đĩa không đổi). Người xem bật "chế độ ghim", click phần tử → annotator gửi mô tả (màn hình `data-view`, nhãn, CSS selector, vị trí %) lên trang cha qua postMessage → lưu bảng `PocComments`. Pin đánh số vẽ ngay trên phần tử. Sandbox giữ nguyên (origin opaque, không cookie) — mọi thao tác ghi đều từ trang cha. Các ghi chú `Open` được gom vào "Yêu cầu chỉnh sửa" tại cổng POC (xem §7.2).
- **Hai đường đóng vòng cho người dùng nghiệp vụ** ngay tại trang POC Review (đều cần `RequirementsManage`): `POST /Projects/RequestPocFix` — gom ghi chú Open thành một vòng chỉnh sửa POC cho Developer (`RequestStageRevisionUseCase` với `onlyStage: PocPreview`, đếm chung trần `MaxRevisionRounds`; rào `onlyStage` để quyền "sửa demo" không nới thành quyền điều khiển các bước kỹ thuật phía sau); và `POST /Projects/RoutePocFeedbackToRequirement` — gửi các điểm HIỂU SAI YÊU CẦU về BA sửa tài liệu. Trước đây đường thứ nhất chỉ có ở cổng duyệt trên Agent Dashboard (`DeliveryAdvance`), nên đúng loại lỗi người xem demo hay bắt nhất (nhãn sai, thiếu nút, bảng trống) lại không có đường nào để họ xử lý.
- **Nghiệm thu bản demo** (`POST /Projects/AcceptPoc`, quyền `RequirementsManage`): điểm DỪNG của hành trình phía người yêu cầu. Trước đây trang chỉ có các đường "còn sai chỗ này" (ghim ghi chú / nhờ Dev chỉnh / gửi về Requirement) mà không có đường nào nói "được rồi": cổng duyệt thật nằm ở Agent Dashboard sau quyền `DeliveryAdvance`, nên đội delivery phải đi hỏi miệng, còn chặng cuối stepper ("Xem & góp ý") không bao giờ đóng lại. `AcceptPocUseCase` ghi `Project.PocAcceptedAtUtc/PocAcceptedBy` và báo cho người có quyền duyệt (`NotificationType.PocAccepted`) — **KHÔNG tự đẩy pipeline**: đi tiếp vẫn là quyết định ở cổng POC, để một cú bấm của người dùng nghiệp vụ không âm thầm khởi động các bước đắt tiền. Nghiệm thu xong, stepper trang Requirements đóng cả 5 chặng.
- Khi task là revision, worker **bỏ qua re-seed** POC để không ghi đè sản phẩm cũ về placeholder.

### 11.3. Khung Bosch & tải source

- `Project.IsUseBoschTemplate = true` (mặc định) ⇒ `BoschTemplateSeeder` clone repo khung chuẩn (backend .NET + Angular) từ `BoschTemplate:BackendRepoUrl/FrontendRepoUrl` vào workspace làm skeleton (idempotent; URL trống thì bỏ qua). Pipeline dùng prompt bản `-bosch`.
- **Tải code sinh ra**: `GET /Projects/DownloadSource?projectId=` — `ImplementationSourcePackager` nén `04_Implementation/src/` thành zip.

---

## 12. Các màn hình & endpoint

Route mặc định: `{controller=Projects}/{action=Index}/{id?}`. Mọi endpoint yêu cầu đăng nhập (fallback policy) trừ nơi ghi `[AllowAnonymous]`. Quyền ghi ở cột phải; action ghi thêm quyền riêng nghĩa là *chồng lên* quyền controller.

| Màn hình | Controller | Actions chính | Quyền |
|---|---|---|---|
| **Login** | `Account` | `GET/POST Login` (AllowAnonymous), `POST Logout`, `GET AccessDenied` | — |
| **Projects** (trang chủ) | `Projects` | `Index` (lọc theo chủ nếu không có `ProjectsViewAll`), `POST Create`, `Mockup` (xem POC sandbox; `review=True` tiêm annotator), `PocReview` (review POC + ghim ghi chú), `GET PocComments`, `POST AddPocComment`/`DeletePocComment`, `POST RequestPocFix` (nhờ Dev chỉnh demo), `POST RoutePocFeedbackToRequirement`, `POST AcceptPoc` (nghiệm thu bản demo), `DownloadSource` (zip) | `ProjectsView`; Create: `ProjectsCreate`; thêm ghi chú POC: `ProjectsView` (như Feedback — quyền View đủ để gửi phản hồi của mình); xóa: chủ ghi chú hoặc `DeliveryAdvance` |
| **Requirements** (workspace chat BA) | `Requirements` | `Index`, `POST ChatStream` (SSE — đường chat chính, stream token), `POST Chat` (fallback postback), `POST UploadSource`/`DeleteSource`, `POST WriteRequirement`, `POST Approve`, `POST ConfirmAssumptions`/`ReviseAssumptions` (cổng giả định), `POST UpdateWorkedExamples` (sửa tay oracle), `POST NewChat`, `GET WorkflowStatus`/`WorkflowStream` (SSE), `GET DocumentRevisions`/`DocumentRevisionDiff`/`DocumentPreview`/`DownloadDocument` | `RequirementsView`; mọi thao tác ghi: `RequirementsManage` |
| **Agent Dashboard** (điều phối delivery) | `AgentDashboard` | `Index`, `GET WorkflowStatus`/`ActiveAgents`/`AgentStats`/`AgentActivity`/`AgentCallLogs`/`CallLogDetail`/`DocumentPreview`, `POST ApproveStage`/`RejectStage`/`RequestRevision`/`RetryWorkflow`/`UpdateDeliveryConfig` | `AgentsView`; các POST cổng duyệt: `DeliveryAdvance` |
| **Agents** (cấu hình agent) | `Agents` | `Index`, `POST Update` (model, temperature, tools...) | `AgentsView` / `AgentsManage` |
| **AI Models** | `Models` | `Index`, `POST Create`/`Update`/`Delete`/`TestConnection` | `ModelsView` / `ModelsCreate`/`Edit`/`Delete`; `TestConnection` cần `ModelsCreate` HOẶC `ModelsEdit` |
| **Usage** (chi phí LLM) | `Usage` | `Index(year?)` — theo model/project/tháng + roll-up phòng ban | `UsageView` |
| **Delivery Quality** | `Quality` | `Index(year?)` — thông lượng, rework, độ tin cậy model | `QualityView` |
| **Prompt Evals** | `Evals` | `Index`, `POST CreateScenario`/`UpdateScenario`/`DeleteScenario`/`StartRun`, `GET RunStatus`/`RunDetail`/`Compare` | `EvalView` / `EvalManage` |
| **Prompt Studio** | `Prompts` | `Index`, `Detail`, `Diff`, `Download`, `POST Save`/`Activate`/`RevertToFile` | `PromptView` / `PromptManage` |
| **Feedback** | `Feedback` | `Index`, `POST Submit` (kèm files), `POST UpdateStatus` (triage), `GET Attachment`, `POST Delete` | `FeedbackView` / `FeedbackManage` |
| **Notifications** | `Notifications` | `Index`, `GET Feed` (chuông poll), `GET Open` (đánh dấu đọc + đi tới link), `POST MarkAllRead`, `GET/POST Preferences` | chỉ cần đăng nhập (dữ liệu tự lọc theo username) |
| **Settings** | `Settings` | `Index`, `POST Update` — sửa `AllowedCommands`, `AllowedFileExtensions`... ghi ngược vào appsettings qua `AppSettingsFileStore` | `SettingsView` / `SettingsManage` |
| **Roles & Permissions** | `Roles` | `Index` (ma trận), `POST Update` | `AdministrationManageRoles` (mặc định chỉ Admin) |
| **Audit Log** | `Audit` | `Index` (lọc category/thời gian) | `AuditView` |
| — | `Home` | `Error` (AllowAnonymous) | — |

---

## 13. Bảo mật: đăng nhập, phân quyền, rào chắn

### 13.1. Xác thực (cookie, secure-by-default)

- Cookie auth: `LoginPath=/Account/Login`, hết hạn 8h **sliding**, `HttpOnly`, `SameSite=Lax`, `Secure=Always` (Development thì `SameAsRequest` để chạy HTTP local).
- **Fallback authorization policy**: *mọi* endpoint đòi đăng nhập trừ khi gắn `[AllowAnonymous]` — controller mới quên `[Authorize]` vẫn an toàn.
- **Antiforgery tự động**: `AutoValidateAntiforgeryTokenAttribute` global — mọi POST đều được CSRF-protect kể cả khi quên attribute.
- Security headers trên mọi response: `X-Content-Type-Options: nosniff`, `X-Frame-Options: SAMEORIGIN`, `Referrer-Policy: no-referrer`. Không đặt CSP global (inline script hiện có); HTML do LLM sinh được sandbox ở endpoint `Projects/Mockup` riêng.
- Mật khẩu băm PBKDF2 (`PasswordHasher<AppUser>`).

### 13.2. Phân quyền (4 role người dùng × quyền mức hành động)

- `UserRole`: **SuperAdmin / Admin / TeamDev / User** — *khác hẳn* `AgentRoleKey` (vai của AI). Một người giữ được **nhiều role** (bảng nối `AppUserRole`); quyền hiệu lực = **hợp quyền** của tất cả vai trò, vì quyền giữa các vai trò giao nhau chứ không lồng nhau.
- Quyền mức hành động: enum `AppPermission` (24 quyền — xem bảng §12). `PermissionCatalog` (Domain/Security) gom quyền theo màn hình để render ma trận + lọc menu sidebar.
- **Một nguồn sự thật**: `IPermissionService` (cache MemoryCache; **SuperAdmin implicit-all** nên không tự khóa được, **Admin nay cấu hình được** như TeamDev/User), dùng bởi filter `[RequirePermission(...)]` và `_Layout.cshtml`.
- Cấu hình runtime ở màn Roles; lưu xong `InvalidateCache()` ⇒ **hiệu lực ngay, không cần đăng nhập lại**. Thiếu quyền ⇒ `/Account/AccessDenied`.
- **Phân quyền THEO PROJECT** (`IProjectAccessGuard` — Services/Security): người không có `ProjectsViewAll` chỉ thao tác được project **mình tạo**, khai báo bằng attribute **`[RequireProjectAccess]`** trên mọi controller action nhận `projectId`/document id (Requirements, Projects/Mockup·PocReview·DownloadSource, Agent Dashboard) — chặn truy cập chéo bằng GUID đoán/lộ qua URL/log. "Không phải của bạn" trả về y hệt "không tồn tại" (redirect về danh sách / 404) để không rò rỉ sự tồn tại của project. Ai có `ProjectsViewAll` (TeamDev/Admin mặc định) pass ngay, không tốn thêm query. `ProjectAccessCoverageTests` fail build nếu có action nhận `projectId` mà quên khai báo.
- **Thêm quyền mới**: thêm giá trị `AppPermission` → khai báo vào `PermissionCatalog.Screens` → gắn `[RequirePermission]` → (nếu là menu) thêm nhánh `@if` trong `_Layout.cshtml` → cân nhắc seed/backfill trong `DbInitializer`.
- **Thêm endpoint theo project**: action mới nhận `projectId` (hoặc id tài nguyên con) phải gắn `[RequireProjectAccess]` — chọn `Denial` khớp thứ client đang chờ (404 mặc định / `RedirectToProjects` cho trang / `JsonError` cho endpoint fetch); id nằm trong view model thì dùng đường dẫn `"vm.ProjectId"`. Xem các action hiện có trong `RequirementsController` làm mẫu.

### 13.3. Bảo vệ bí mật & dữ liệu

- `AiModel.ApiKey` mã hóa AES trong DB (`AesApiKeyProtector`); khóa từ `Encryption__ApiKeyKey` (fail-fast nếu thiếu). Giá trị không có prefix mã hóa được passthrough (tiện seed/test).
- Bí mật chỉ nạp qua env/user-secrets: GitHub PAT, SMTP password, URL repo Bosch private.
- Prompt BA **không chứa PII** của Associates — chỉ dữ liệu gộp, tên thật chỉ ở vai trò HoD/manager.
- `AuditLogger` ghi nhật ký thay đổi cấu hình (Settings/Roles/Agent/Model/Prompt) kèm actor + before/after JSON.

---

## 14. Tham chiếu cấu hình appsettings.json

Mọi key, ý nghĩa và mặc định. Override bằng biến môi trường theo cú pháp `Section__Key` (hai gạch dưới).

| Key | Mặc định | Ý nghĩa |
|---|---|---|
| `Database:Provider` | `SqlServer` | `SqlServer` hoặc `Sqlite`. Chạy Sqlite bằng env var `Database__Provider=Sqlite`. Sqlite mà connection string vẫn dạng SQL Server ⇒ tự fallback file `ICOGenerator.db` |
| `ConnectionStrings:DefaultConnection` | `Server=SONGLONG;...` | Chuỗi kết nối. SqlServer bật `EnableRetryOnFailure` |
| `AgentWorkspace:RootPath` | `C:\Study App\ICOGeneratorWorkspaces` | Thư mục gốc workspace agent. **Phải đổi theo máy** |
| `AllowedCommands` | dotnet, git status/diff/add/commit/push/checkout/remote get-url, dir, npm, node | Whitelist lệnh cho `RunCommand` |
| `AllowedFileExtensions` | .cs .cshtml .css .scss .ts .js .json .md .txt .sln .csproj .html .sql .yml .yaml | Whitelist đuôi file cho tool file |
| `BoschTemplate:BackendRepoUrl/FrontendRepoUrl/Branch` | trống | Repo khung Bosch để clone skeleton; trống = bỏ qua. Nạp URL private qua env |
| `Commands:TimeoutSeconds` | 120 | Timeout mỗi lệnh RunCommand |
| `Workers:MaxConcurrentAgentTasks` | 1 | Số agent task chạy song song tối đa của `AgentTaskWorker` (trần cứng 16). Mặc định 1 = tuần tự như trước (an toàn cho LLM tự host); tăng 2–4 khi endpoint model chịu tải song song. Mỗi project luôn chỉ một task một thời điểm |
| `Feedback:UploadRootPath` | trống ⇒ `{ContentRoot}/FeedbackUploads` | Nơi lưu file đính kèm feedback |
| `Feedback:MaxFileBytes` / `MaxFilesPerFeedback` | 50MB / 8 | Trần file đính kèm |
| `PullRequest:RemoteName/BaseBranch/GitHubToken` | origin / main / trống | Bước PR: token trống hoặc remote không phải GitHub ⇒ fallback link compare |
| `Notifications:BaseUrl` | trống | URL gốc app để dựng link tuyệt đối trong Teams/email |
| `Notifications:Teams:{Enabled,WebhookUrl}` | tắt | Incoming Webhook Teams. Fail-open |
| `Notifications:Email:{Enabled,Host,Port,UseStartTls,Username,Password,From,To}` | tắt / 587 STARTTLS | SMTP. Password qua env. Fail-open |
| `Notifications:BoschEmail:{Enabled,BaseUrl,SendMailApi,ApiKey,FromEmail,To,OnlySendToTesterEmail,TesterEmail}` | tắt / `api/Email` | Email Server API nội bộ Bosch (HTTP) thay SMTP. ApiKey qua env. `OnlySendToTesterEmail` = chốt an toàn non-prod. Fail-open |
| `Llm:Proxy:{Enabled,Address}` | false / `http://127.0.0.1:3128` | Proxy công ty cho lời gọi LLM ra ngoài (client "proxied"); code mặc định coi Enabled=true nếu **thiếu key** — appsettings hiện đặt tường minh false |
| `Budget:{Enabled,Period,SystemUsdLimit,PerProjectUsdLimit}` | true / Monthly / 0 / 0 | Trần chi phí USD. 0 = không giới hạn scope đó (opt-in thực tế) |
| `Encryption:ApiKeyKey` | ⚠️ có giá trị commit sẵn | **Bắt buộc nạp qua env**; khóa cũ trong git history coi như đã lộ — xoay khóa trên môi trường thật |
| `Serilog:*` | Console + File `Logs/ico-.log` xoay ngày, giữ 14 ngày, 50MB/ngày | Mức log/sink đổi không cần build |
| `Otel:{Enabled,ServiceName,OtlpEndpoint}` | false / ICOGenerator / trống ⇒ gRPC `localhost:4317` | OpenTelemetry opt-in. Đừng bật khi chưa có collector — dev/demo chạy `docker compose -f docker-compose.otel.yml up -d` là có sẵn |

> Màn hình **Settings** trong app sửa được một phần cấu hình này lúc runtime (qua `AppSettingsFileStore` ghi ngược vào file) — vì vậy trang Settings được bảo vệ chặt (`SettingsManage`, mặc định chỉ Admin).

---

## 15. Logging & Observability

- **Serilog** thay logging mặc định. `Program.cs` dựng **bootstrap logger** trước khi host build để bắt cả lỗi khởi động (đọc config, build DI, migrate DB) — toàn bộ thân `Program.cs` nằm trong `try/catch(Log.Fatal)/finally(Log.CloseAndFlush)`.
- `UseSerilogRequestLogging()`: một dòng tóm tắt có cấu trúc cho mỗi HTTP request.
- Sink: **Console** (stdout — Docker/k8s gom được) + **File** `Logs/ico-{ngày}.log` (gitignored). Production muốn JSON nén cho Seq/Loki/ELK: đổi formatter qua `appsettings.Production.json`, không sửa code.
- **OpenTelemetry** (opt-in): bật `Otel:Enabled` ⇒ trace ASP.NET Core + HttpClient (lời gọi LLM tự thành span — dựng lại được chuỗi agent → model → tool) + metric runtime/HTTP, xuất OTLP. Tắt = không đăng ký gì, zero overhead.
  - **Collector cục bộ để "bật là chạy"**: `docker compose -f docker-compose.otel.yml up -d` dựng **.NET Aspire Dashboard** (OTLP endpoint + UI) nghe sẵn ở `localhost:4317` — đúng default của `Otel:OtlpEndpoint`, nên chỉ cần set `Otel:Enabled=true` là có trace ngay. UI: `http://localhost:18888`. Collector chạy **tách riêng** app (đúng triết lý OTel), file compose chỉ dành cho dev/demo (dashboard anonymous, đừng dùng cho production).
- **Log nghiệp vụ riêng**: `AgentModelCallLogs` (mỗi lời gọi model — xem popup AI Call Logs ở Agent Dashboard), `ToolExecutionLogger` (mỗi lần gọi tool), `AuditLogs` (thay đổi cấu hình).

---

## 16. Các tính năng vệ tinh

### 16.1. Notifications
- **In-app (chuông)**: luôn chạy. `NotificationService` ghi bảng `Notifications` tại các sự kiện workflow (cổng chờ duyệt / hoàn tất / thất bại); client poll `GET /Notifications/Feed`.
- **Kênh ngoài (Teams webhook, SMTP email, Bosch Email Server API)**: opt-in qua config, fail-open (lỗi gửi chỉ log warning, không gãy workflow). Kiến trúc plugin: hiện thực `INotificationChannel` mới + đăng ký DI là xong. `BoschEmailServerNotificationChannel` gửi qua Email Server API nội bộ (HTTP + header `ApiKey`, giống các app Bosch khác) — dùng khi hạ tầng chỉ mở API thay vì SMTP; kèm chốt an toàn `OnlySendToTesterEmail` lọc người nhận về danh sách tester cho môi trường non-prod.
- **Tùy chọn theo user**: `/Notifications/Preferences` — bật/tắt kênh, chọn loại sự kiện, email cá nhân.

### 16.2. Budget guard
`IBudgetGuard` chặn **trước** mỗi lời gọi model khi tổng chi phí trong kỳ (`Monthly`/`Daily`/`Total`) chạm trần hệ thống hoặc trần mỗi-project. Chi phí tính y hệt trang Usage. Chỉ chính xác khi model khai báo đơn giá. Bản tổng chi phí được **cache 15 giây** (IMemoryCache) và query tổng đi qua index `AgentModelCallLogs(CreatedAt)` — một agent run 40 bước không còn quét bảng log 40 lần; đổi lại trần có thể bị vượt thêm đúng lượng chi tiêu của cửa sổ cache đó (chấp nhận được cho một chốt chặn đo theo kỳ).

### 16.3. Usage & Delivery Quality
- **Usage**: token & USD theo model/project/tháng, kèm "Usage by department" (roll-up `OrgUnitCode` về department gần nhất).
- **Delivery Quality**: thông lượng pipeline, tỉ lệ rework (revision/bugfix), độ tin cậy model; có card trỏ sang Prompt Evals.

### 16.4. Prompt Evals (trả lời "sửa prompt/đổi model xong, chất lượng lên hay xuống?")
- `EvalScenario` = golden set (template + input mô phỏng + tiêu chí). Run chạy **nền** (`EvalRunWorker` poll 3s) với model MỤC TIÊU, rồi model JUDGE chấm 1–5 (`Eval/judge.v1.md` + `EvalJudgeParser`).
- So sánh 2 run theo từng scenario; nhãn phiên bản prompt mỗi bên (cùng nhãn = so model, khác nhãn = so prompt).
- Eval dùng lại middleware LLM nhưng với `NullModelCallLogger` (không ghi `AgentModelCallLogs`, không qua budget theo-project) — token/lỗi nằm ngay trên `EvalResult`.

### 16.5. Feedback
Người dùng gửi bug/góp ý kèm tối đa 8 file × 50MB (ảnh, PDF, Office, video — whitelist trong `FeedbackAttachmentStore`). TeamDev/Admin triage bằng `FeedbackManage`.

---

## 17. Test & xác minh end-to-end

### 17.1. Unit test

```bash
dotnet test          # xUnit; EF chạy Sqlite — không cần SQL Server/LLM
```

Bố cục test khớp bố cục code — sửa ở đâu, tìm test ở thư mục cùng tên. Các parser (verdict, judge, chat reply...), cổng readiness tất định, use case cổng duyệt, budget, notification, prompt studio... đều có test.

### 17.2. Xác minh end-to-end không cần hạ tầng thật — skill `verify`

`.claude/skills/verify/SKILL.md` (dùng được cả như tài liệu chạy tay):

1. Build rồi **chạy DLL trực tiếp** với env Development (Sqlite) — nhớ `Encryption__ApiKeyKey` bất kỳ và `AgentWorkspace__RootPath` hợp lệ.
2. Dựng **LLM stub OpenAI-compatible** — **bắt buộc hỗ trợ SSE streaming** (`stream:true`); stub trả JSON thường thì agent "chạy xong" nhưng Output rỗng. Trỏ model vào stub bằng UPDATE bảng `AiModels` (ApiKey plaintext vẫn đọc được nhờ passthrough).
3. Seed trạng thái workflow bằng SQL nếu cần (enum lưu TEXT; **datetime format EF: `YYYY-MM-DD HH:MM:SS.ffffff`, dấu cách không phải 'T'**).
4. Lái UI bằng Playwright; selector cổng duyệt: `#delivery-gate`, `#dg-approve-form`, `#dg-revise-btn`, `#revise-modal`... Gate poll ~2.5s, worker nhặt task ~2s.

---

## 18. Công thức làm việc: thêm tính năng, quy ước code

### 18.1. Thêm một tính năng mới (checklist chuẩn)

1. **Domain/Contracts**: cần kiểu dữ liệu mới → entity vào `Domain/` (nhớ migration) hoặc DTO vào `Contracts/`.
2. **Application**: một class `ExecuteAsync` — `Get...Query` (đọc) / `...UseCase` (ghi) trong đúng thư mục khu vực.
3. **Services** (nếu có logic kỹ thuật tái dùng): gọi LLM, sinh file... đặt ở `Services/...`.
4. **Controller**: action mỏng gọi use case; gắn `[RequirePermission]` phù hợp.
5. **View/JS/CSS**: `.cshtml` + file js/css theo màn hình trong `wwwroot/`.
6. **DI**: đăng ký vào đúng nhóm `AddXxx()` — quên là "Unable to resolve service" lúc chạy.
7. **Test**: thêm ở `tests/` đúng thư mục khu vực.

Các công thức chuyên biệt: thêm **tool** (§8.2), thêm **bước pipeline** (thêm dòng vào `DeliveryPipeline.Steps` + stage enum + prompt template — worker/orchestrator không đổi), thêm **quyền/màn hình** (§13.2), thêm **kênh thông báo** (§16.1), thêm **model** (§9.2 — không cần code).

### 18.2. Quy ước phải giữ

- Một file = một kiểu công khai; namespace = đường dẫn thư mục.
- Controller luôn mỏng; Services không `using` ngược lên trên.
- `Tools/Abstractions` chỉ chứa interface/record; hiện thực ở `Tools/Execution`.
- Enum đã lưu DB dạng chuỗi ⇒ **không đổi tên giá trị enum**.
- Prompt đổi được runtime — nhưng bản "chín" nên export đồng bộ ngược về repo.
- Đăng ký lifetime cẩn thận: các policy/store config-bound stateless = Singleton; thứ gì đụng `DbContext` = Scoped; `IApiKeyProtector` **bắt buộc** Singleton.

### 18.3. Cạm bẫy đã biết (đọc trước khi sửa sâu)

- **Chat BA chạy đồng bộ trong request** — luồng job `AgentJob/AgentJobRunner` cũ đã gỡ hẳn (bảng đã drop); đừng dựng lại trừ khi nối vào UI. Pipeline nền dùng `WorkflowRun` + `AgentTask`.
- **Đường fallback prompt-based cho agent đã gỡ** — chỉ còn native tool-calling; đừng tìm `AgentActionParser`/`ToolSchemaBuilder` (không còn tồn tại).
- **Worker generic** — muốn đổi hành vi hand-off, sửa `ApproveStageUseCase`/`DeliveryPipeline`, không nhét if/else theo stage vào worker (ngoại lệ duy nhất được phép: chu trình BugFix và nhánh TechnicalDocs, đã cô lập sẵn).
- **`MaxSteps` = số lời gọi LLM của một task** — bước sinh nhiều file phải khuyến khích `WriteFiles`; hết budget có pha salvage nhưng đừng dựa vào nó.
- **`WorkflowProgressReporter` in-memory** — nhiều instance app (scale-out) sẽ không chia sẻ tiến độ live; kiến trúc hiện tại giả định single instance (worker nền cũng vậy).

---

## 19. Troubleshooting — lỗi thường gặp

| Triệu chứng | Nguyên nhân & cách xử lý |
|---|---|
| App chết ngay khi khởi động, log Fatal `Encryption...` | Thiếu `Encryption__ApiKeyKey` — cố ý fail-fast. Đặt biến môi trường rồi chạy lại |
| App cố kết nối SQL Server dù bạn muốn Sqlite | Thiếu env var `Database__Provider=Sqlite` (mặc định `appsettings.json` là SqlServer). Đặt biến này khi chạy DLL trực tiếp (§3.3-B) |
| `Unable to resolve service for type ...` | Quên đăng ký DI trong `ApplicationServiceCollectionExtensions` — thêm vào đúng nhóm `AddXxx()` |
| `dotnet build` fail `MSB3552: **/*.resx cannot be found` (Linux) | Lần chạy trước tạo thư mục literal `C:\Study App\...` trong repo (root path Windows). Xóa thư mục rác đó + `Logs/`; lần sau set `AgentWorkspace__RootPath` |
| ApiKey model giải mã lỗi / gọi LLM báo key sai sau khi đổi máy/khóa | `Encryption__ApiKeyKey` khác với khóa lúc mã hóa. Dùng lại khóa cũ, hoặc nhập lại ApiKey ở màn AI Models |
| Lỗi `Value cannot be an empty string (Parameter 'key')` khi agent chạy | Model đang chọn có ApiKey rỗng (model seed DeepSeek để trống) — điền ApiKey hoặc trỏ agent sang model khác |
| Agent chạy "thành công" nhưng Output rỗng (khi dùng stub/proxy) | Endpoint không hỗ trợ **SSE streaming** — app đọc stream. Stub phải trả `text/event-stream` |
| Task đứng `Running` mãi sau khi app restart | Bình thường: `DbInitializer` sẽ re-queue ở lần khởi động kế (tối đa 3 lần thử rồi Failed). Không tự sửa tay Status trong DB khi app đang chạy |
| Đổi quyền ở màn Roles mà user kêu không thấy thay đổi | Không thể — cache được invalidate ngay khi lưu. Kiểm tra lại đúng role, và nhớ **SuperAdmin luôn full quyền** bất kể ma trận (Admin thì theo ma trận) |
| Sinh tài liệu ném `FileNotFoundException` trên bản publish | Thiếu thư mục `Templates/` — csproj đã cấu hình copy; nếu tự đóng gói tay phải mang theo `Templates/*.docx` + `Prompts/**` |
| Đổi schema khi dev Sqlite không thấy cột mới | Sqlite dùng `EnsureCreated` (không migration) — xóa `ICOGenerator.db*` để dựng lại |
| Muốn reset sạch lịch sử migration | Xóa `Migrations/` → `dotnet ef migrations add V1` với env ≠ Development (để sinh theo SqlServer) — xem ARCHITECTURE §9 |
| Bật Otel xong log đầy lỗi exporter | Chưa có OTLP collector — tắt `Otel:Enabled` hoặc dựng collector trước (nhanh nhất: `docker compose -f docker-compose.otel.yml up -d`, nghe sẵn `localhost:4317`) |
| Cổng duyệt POC không có nút Từ chối | Cố ý (`PocGateNotRejectable`) — POC sai = requirement sai, user sửa qua chat BA; TeamDev chỉ được "Yêu cầu chỉnh sửa" |

---

## 20. Từ điển thuật ngữ

| Thuật ngữ | Nghĩa trong dự án |
|---|---|
| **Agent** | Một "nhân sự AI" (bản ghi bảng `Agents`): vai + model + tools. Khác **AppUser** (người thật) |
| **AgentRoleKey** | Vai của AI: BusinessAnalyst, TechLead, Developer, Tester, UiUx |
| **UserRole** | Vai của người: Admin, TeamDev, User |
| **Product Brief** | Tài liệu yêu cầu ngôn ngữ đời thường cho user duyệt (draft → V{n}) |
| **AI Design Spec** | Bản đặc tả kỹ thuật sinh từ Product Brief đã duyệt — input của POC/Architecture |
| **AC-n (câu nghiệm thu)** | Dòng "Hoàn thành khi: …" người dùng đã duyệt trong Product Brief, chép nguyên văn vào spec § 14 và là đích của bộ kịch bản UAT |
| **POC** | Demo HTML một-file (`poc-demo.html`) có hành vi thật, để user "thấy" trước khi đầu tư code |
| **Technical Docs** | Bộ BRD/SRS/FSD/UserStories — sinh ở bước 2 pipeline, không phải lúc Write Requirement |
| **WorkflowRun / AgentTask** | "Vé" theo dõi một lần chạy quy trình / một đầu việc trong đó |
| **Gate (cổng duyệt)** | Run dừng `WaitingForHuman` chờ người bấm Duyệt/Chỉnh sửa/Từ chối trên Agent Dashboard |
| **Hand-off** | Output bước trước thành Input bước sau khi qua cổng |
| **Revision (cổng)** | "Yêu cầu chỉnh sửa" — agent sửa đúng bước đó theo nhận xét, tối đa 3 vòng/bước |
| **BugFix cycle** | Chu trình tự động Testing↔BugFix khi Tester trả `VERDICT: FAIL`, tối đa 3 vòng |
| **Workspace** | Thư mục file thật của project dưới `AgentWorkspace:RootPath` (5 phase 01→05) |
| **Tool** | Method C# public có `[Description]` mà agent gọi được qua native tool-calling |
| **Prompt key** | Đường dẫn tương đối file prompt dưới `/Prompts` — khóa dùng bởi PromptTemplateService/Studio/Evals |
| **Golden set** | Bộ `EvalScenario` chuẩn để chấm chất lượng prompt/model bằng LLM-judge |
| **Fail-open** | Nguyên tắc thiết kế lặp lại khắp app: tính năng phụ (memory, org context, notification, prompt override) lỗi thì âm thầm rơi về hành vi cơ bản, không bao giờ làm gãy luồng chính |
| **Opt-in** | Nguyên tắc cấu hình: tính năng có phụ thuộc ngoài (Proxy, Otel, Budget limits, Teams/Email) mặc định TẮT; structured output opt-in **theo từng model**, 3 mức (`AiModel.StructuredOutputMode`) |

---

*Tài liệu này mô tả đúng trạng thái code tại thời điểm viết (07/2026). Khi thấy lệch giữa tài liệu và code, hãy tin code — và sửa tài liệu trong cùng PR.*
