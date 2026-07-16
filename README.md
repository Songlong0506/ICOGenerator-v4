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
  └► Chat với agent BA (hỏi đáp làm rõ yêu cầu, có thể upload tài liệu nguồn)
       └► "Write Requirement" → BA sinh Product Brief (ngôn ngữ đời thường, dạng draft, sửa được nhiều lần)
            └► User bấm "Approve"
                 ├► Product Brief được chốt thành V{n}
                 ├► BA sinh AI Design Spec (bản kỹ thuật) ở một run nền riêng
                 └► Delivery Pipeline tự khởi động, chạy nền với CỔNG DUYỆT giữa mỗi bước:
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
| Đọc PDF | **PdfPig 0.1.15** | Trích text từ tài liệu nguồn user upload |
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

Các bí mật *tùy chọn* khác (chỉ khi dùng tính năng tương ứng): `PullRequest__GitHubToken`, `Notifications__Email__Password`, `BoschTemplate__BackendRepoUrl` / `BoschTemplate__FrontendRepoUrl`.

### 3.3. Ba kịch bản chạy

**Kịch bản A — máy dev "đầy đủ" (Windows + SQL Server + LM Studio):**

1. Sửa `appsettings.json`:
   - `ConnectionStrings:DefaultConnection` → SQL Server của bạn.
   - `AgentWorkspace:RootPath` → một thư mục **tồn tại** trên máy (nơi agent đọc/ghi file sinh ra).
2. Đặt `Encryption__ApiKeyKey`.
3. `dotnet run` → app nghe tại `https://localhost:55356` / `http://localhost:55357` (theo `Properties/launchSettings.json`).

> ⚠️ `launchSettings.json` ép `ASPNETCORE_ENVIRONMENT=Production` — nghĩa là `dotnet run` mặc định dùng **SqlServer** theo `appsettings.json`, *không* đọc `appsettings.Development.json`.

**Kịch bản B — không có SQL Server (Sqlite):**

`appsettings.Development.json` đã đặt sẵn `Database:Provider=Sqlite` (DB file `ICOGenerator.db`, đã `.gitignore`). Vì `dotnet run` bị launchSettings ép Production, cách chắc chắn nhất là chạy DLL trực tiếp:

```bash
dotnet build -v q
ASPNETCORE_ENVIRONMENT=Development \
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
4. **Seed ma trận quyền** (khi bảng trống): TeamDev = mọi thứ trừ Settings/Roles; User = xem Projects/Requirements + gửi Feedback. Admin không cần dòng nào (implicit-all).
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
                         #   TokenEstimator, MaxOutputTokenResolver, LlmCost, StructuredOutputPolicy...
  Notifications/         # NotificationService + Channels/ (Teams webhook, SMTP email)
  Prompts/               # PromptTemplateService, DbPromptOverrideProvider, PromptFileCatalog
  Requirements/          # BARequirementService + toàn bộ trí nhớ/parser/generator của luồng BA
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
| `Projects` | Dự án — gốc nối tới tài liệu, hội thoại, workflow | Ngoài metadata còn mang **bộ nhớ của luồng BA**: `ConversationSummary` + `SummarizedTurnCount` (tóm tắt hội thoại dài), `UserMemoryHarvestedTurnCount`, `RequirementCoverageMap` + `CoverageHarvestedTurnCount` (bản đồ bao phủ 12 nhóm thông tin), `ChecklistGapHarvested`. `CreatedByUsername` để lọc "chỉ thấy project mình tạo"; `OrgUnitCode` (không FK) gắn đơn vị yêu cầu; `IsUseBoschTemplate` (mặc định true) do TeamDev đổi ở Agent Dashboard |
| `Agents` | "Nhân sự AI": `RoleKey` (BusinessAnalyst/TechLead/Developer/Tester/UiUx), `AiModelId`, `Temperature`, `Color`, `LearnedChecklistNotes` | System prompt **không lưu DB** — nạp từ `Prompts/{RoleKey}/instruction.md` qua `AgentInstructionProvider`. FK sang AiModel là `Restrict` (không xóa được model đang dùng) |
| `AiModels` | Danh mục model LLM: `ModelId`, `Endpoint`, `ApiKey` (mã hóa), `ContextWindow`, đơn giá Input/Output per-1M-token (decimal 18,6) | Đơn giá là đầu vào của trang Usage + Budget guard. Model tự host giá 0 ⇒ chi phí 0 |
| `ToolDefinitions` | Danh mục tool (đồng bộ từ code khi khởi động) | Unique index `(ServiceType, MethodName)` |
| `AgentTools` | Bảng nối agent ↔ tool được phép dùng | Khóa chính kép `(AgentId, ToolDefinitionId)` |

### 5.2. Nhóm tài liệu & hội thoại

| Bảng | Vai trò | Điểm đáng chú ý |
|---|---|---|
| `ProjectDocuments` | Tài liệu sinh ra (ProductBrief/AIDesignSpec/BRD/SRS/FSD/UserStories...): `Folder`, `VersionName`, `FileName`, `FilePath`, `Content`, `IsApproved` | Cascade theo Project |
| `ProjectDocumentRevisions` | **Lịch sử nội dung** mỗi lần document bị ghi đè CÓ thay đổi — snapshot đầy đủ + `ChangeNote` nguồn gốc | Chốt chặn duy nhất tạo revision là `RequirementDocumentGenerator.UpsertDocument`. Diff tính lúc xem bằng `DocumentDiffService` (LCS theo dòng). Unique `(DocumentId, RevisionNumber)` |
| `ProjectSourceFiles` | Tài liệu nguồn user upload cho BA đọc (PDF/ảnh/text...) — `ExtractedText` do `ProjectSourceIngestor` trích (PdfPig cho PDF) | Cascade theo Project |
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
| `AppUsers` | Tài khoản đăng nhập: `Username` (unique), `PasswordHash` (PBKDF2 qua `PasswordHasher`), `Role` (Admin/TeamDev/User), `UserMemory` (hồ sơ cá nhân hóa BA học được), tùy chọn thông báo (`NotifyInApp/ByEmail/OnGate/OnCompleted/OnFailed`, `Email`) | Chưa có UI tạo user — seed 3 tài khoản cố định |
| `RolePermissions` | Cấp quyền `(Role, Permission)` — cấu hình runtime ở màn Roles | Unique `(Role, Permission)`. Admin implicit-all, không có dòng nào |
| `AuditLogs` | Nhật ký thay đổi cấu hình (Settings/Roles/Agent/Model/Prompt): actor, before/after JSON | Ghi qua `IAuditLogger` |

### 5.5. Nhóm vệ tinh

| Bảng | Vai trò |
|---|---|
| `Feedbacks` + `FeedbackAttachments` | Phản hồi người dùng toàn app (bug/góp ý/trải nghiệm) kèm file đính kèm; file gốc lưu đĩa (`Feedback:UploadRootPath`), DB chỉ giữ metadata |
| `OrgUnits` + `Associates` | Dữ liệu tổ chức đồng bộ từ HR_Portal (phòng ban, nhân sự) — nguyên liệu cho `OrganizationContextService` |
| `Notifications` | Thông báo in-app (chuông): index `(RecipientUsername, IsRead, CreatedAt)` |
| `EvalScenarios` / `EvalRuns` / `EvalResults` | Prompt eval harness (golden set + LLM-judge). Model/scenario tham chiếu bằng **Guid + snapshot tên, không FK** — xóa không mất lịch sử điểm |
| `PromptTemplateVersions` | Phiên bản prompt chỉnh runtime (Prompt Studio): snapshot đầy đủ, unique `(PromptKey, VersionNumber)`, tối đa một `IsActive` mỗi key |

### 5.6. Migration

- Đổi entity ⇒ `dotnet ef migrations add <Tên>`; `DbInitializer` tự `MigrateAsync` lúc khởi động (SqlServer).
- Migration hiện tại là một **baseline `V1` duy nhất** (đã gộp toàn bộ lịch sử; các migration tiến lẻ tẻ trước đây không còn). Khi cần sinh migration, đặt `ASPNETCORE_ENVIRONMENT` khác `Development` để nó sinh theo provider SqlServer (không phải Sqlite).
- Sqlite **không chạy migration** (dùng `EnsureCreated`) ⇒ đổi schema khi dev Sqlite = xóa file `ICOGenerator.db*` để dựng lại.

---

## 6. Hai động cơ của hệ thống

Phân biệt được hai luồng này là tránh được 90% nhầm lẫn khi đọc code.

### 6.1. Động cơ 1 — Chat với BA (đồng bộ theo request)

```
Browser POST /Requirements/Chat
  └► RequirementsController.Chat                     [Controllers]
       └► ChatWithBAUseCase.ExecuteAsync             [Application/Requirements]
            └► BARequirementService.ChatAsync        [Services/Requirements]
                 ├► OrganizationContextService       → system message "bức tranh tổ chức" (cache 1h)
                 ├► UserMemoryService                → hồ sơ user (học dần, xuyên project)
                 ├► ConversationMemoryService        → 20 lượt gần nhất nguyên văn + tóm tắt lượt cũ
                 ├► RequirementCoverageService       → bản đồ bao phủ 12 nhóm thông tin
                 ├► SourceContextBuilder             → ngữ cảnh từ tài liệu user upload
                 ├► RequirementPromptBuilder         → dựng prompt (template Prompts/BusinessAnalyst/*)
                 ├► ILlmClient                       → gọi LLM  [Services/Llm]
                 └► BAChatReplyParser                → parse trả lời (+ readiness gate)
       └► AppDbContext.SaveChanges                   [Data] — lưu lượt hội thoại
```

Các cơ chế trí nhớ (chi tiết đầy đủ ở `ARCHITECTURE.md` §5.11–5.13):

- **Bộ nhớ hội thoại 2 tầng**: 20 lượt gần nhất gửi nguyên văn; lượt cũ gộp dần vào `Project.ConversationSummary` **theo lô ≥10 lượt** (không tóm tắt mỗi lượt — đó là chỗ tiết kiệm token). Fail-open: gọi tóm tắt lỗi thì giữ summary cũ, không mất lượt nào.
- **Bộ nhớ cấp user** (`AppUser.UserMemory`): BA chắt lọc sự thật bền về user (vai trò, lĩnh vực, văn phong...) theo lô, dùng lại ở mọi project của họ.
- **Bản đồ bao phủ yêu cầu** (`Project.RequirementCoverageMap`): 12 nhóm thông tin đánh dấu [RÕ]/[MỘT PHẦN]/[CHƯA HỎI]/[KHÔNG ÁP DỤNG] — BA chọn câu hỏi kế tiếp dựa vào đây, và cổng readiness không phải đoán lại từ đầu.
- **Checklist gap** (`Agent.LearnedChecklistNotes`): sau khi tài liệu sinh thành công, hệ thống rà một lần "user phải tự nêu thông tin gì mà BA chưa từng hỏi" và ghi nhớ **cho mọi project sau**.
- **Bối cảnh tổ chức**: render từ OrgUnits/Associates, chỉ dữ liệu GỘP (không PII), cache 1h. Fail-open toàn tuyến.

**"Write Requirement"** chỉ sinh **Product Brief** (ngôn ngữ đời thường, dạng draft — user sửa đi sửa lại không đốt token bản kỹ thuật). Chạy dưới dạng workflow run một-bước loại `RequirementAnalysis` với tiến độ live (xem 6.3).

**"Approve"** (`ApproveRequirementUseCase`): promote Product Brief lên `V{n}`, rồi khởi động run nền **AiDesignSpec** (một bước, BA sinh bản kỹ thuật từ Product Brief đã duyệt — chạy nền để màn hình không treo chờ LLM); xong tự khởi động Delivery Pipeline (§7).

### 6.2. Động cơ 2 — Pipeline nền (bất đồng bộ, qua hàng đợi)

```
IWorkflowOrchestrator.Start...WorkflowAsync           [Services/Workflows]
   tạo WorkflowRun + AgentTask đầu tiên (Status=Queued)

AgentTaskWorker : BackgroundService                    — poll mỗi 2 GIÂY
   lấy AgentTask Queued cũ nhất (index (Status, CreatedAt))
     └► AgentRunService.RunAsync(projectId, agentId, prompt)   [Services/Agents]
          Microsoft Agent Framework tự lo vòng: LLM ⇄ tool cho tới khi xong
     └► cập nhật Task.Output; còn bước kế → run dừng WaitingForHuman (cổng duyệt)
        hết bước → Completed; lỗi → Failed
```

Điểm cốt lõi: **worker là generic** — nó không biết gì về từng vai. "Ai làm sau ai" nằm ở dữ liệu khai báo `DeliveryPipeline.Steps`; việc enqueue bước kế nằm ở `ApproveStageUseCase` (vì giữa các bước có cổng duyệt).

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
| 1 | `PocPreview` | Developer | `PocPreview` | AI Design Spec | 16 | `Developer/poc-preview.v1.md` |
| 2 | `TechnicalDocs` | BA | `TechnicalDocs` | AI Design Spec | (8, không tiêu thụ*) | `BusinessAnalyst/technical-docs.v1.md` |
| 3 | `ArchitectureDesign` | Tech Lead | `ArchitectureDesign` | AI Design Spec | 8 | `TechLead/architecture-design[-bosch].v1.md` |
| 4 | `Implementation` | Developer | `Implementation` | Output bước trước | 40 | `Developer/implementation[-bosch].v1.md` |
| 5 | `CodeReview` | Tech Lead | `CodeReview` | Output bước trước | 12 | `TechLead/code-review.v1.md` |
| 6 | `Testing` | Tester | `Testing` | Output bước trước | 8 | `Tester/testing.v1.md` |
| 7 | `PullRequest` | Developer | `PullRequest` | Output bước trước | 6 | `Developer/pull-request.v1.md` |

\* Bước TechnicalDocs **không** chạy qua agent + prompt chung: worker xử lý nhánh riêng, gọi `BARequirementService.GenerateTechnicalDocsAsync` (BA cần đọc context project) — sinh BRD/SRS/FSD/UserStories từ Product Brief + AI Design Spec đã duyệt.

Ngoài chuỗi tuyến tính còn **`BugFixStep`** (Developer, `BugFix`, MaxSteps 30) — cố tình không nằm trong `Steps` vì nó là chu trình quanh Testing (xem 7.3).

### 7.2. Cổng duyệt (gates) — trạng thái `WaitingForHuman`

Mỗi bước chạy xong, run **dừng** ở `WaitingForHuman`. Trên **Agent Dashboard** (yêu cầu quyền `DeliveryAdvance`), người duyệt có 4 lựa chọn:

| Hành động | Use case | Hệ quả |
|---|---|---|
| **Duyệt & tiếp tục** | `ApproveStageUseCase` | Resolve input theo `InputSource` (spec hoặc output task Completed mới nhất — tức bản đã-sửa nếu có revision) → enqueue bước kế |
| **Yêu cầu chỉnh sửa** (kèm nhận xét) | `RequestStageRevisionUseCase` | Enqueue lại **đúng bước hiện tại**: `Input` giữ NGUYÊN BẢN, nhận xét nằm riêng ở `AgentTask.RevisionFeedback`; prompt gốc + nối khối `Shared/revision.v1.md`. Trần `MaxRevisionRounds = 3` mỗi bước (đếm bằng số task có `RevisionFeedback != null` cùng loại trong run) |
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
       │     + ThinkingDisabledHandler (chèn lại field "thinking" mà SDK OpenAI typed không diễn đạt được)
       └► ChatClientBuilder compose ModelCallLoggingChatClient (middleware chung):
             deadline • trần completion-token (MaxOutputTokenResolver + TokenEstimator)
             • map lỗi API/timeout thành LlmCallResult • ghi AgentModelCallLogs • progress
```

- **`ILlmClient.ChatAsync`** — đường chat thuần (BA). **`ChatStructuredAsync<T>`** — structured output (`response_format: json_schema`), **opt-in** theo `StructuredOutputPolicy` (`Llm:StructuredOutput`, mặc định TẮT vì nhiều server local từ chối `response_format`); JSON không khớp schema ⇒ trả `value=null` để caller fallback về parser tay (`RequirementResponseParser`/`BAChatReplyParser`/`RequirementReadinessParser`) — không bao giờ fail trắng.
- **`LlmCost`** tính chi phí = token × đơn giá model — cùng công thức cho trang Usage và Budget guard.
- **`IBudgetGuard`** kiểm tra **trước mỗi lời gọi** (cả agent lẫn BA chat): chạm trần (`Budget:*`) ⇒ từ chối gọi, ném `BudgetExceededException` với lý do.
- **`JsonExtractor`/`JsonDefaults`** — tiện ích bóc JSON từ trả lời văn xuôi.

### 9.2. Thêm một model mới

Màn hình **AI Models** → Create: điền `Name`, `Provider`, `ModelId`, `Endpoint` (base URL OpenAI-compatible), `ApiKey`, `ContextWindow`, đơn giá (0 nếu tự host). Model gán cho agent nào là do màn **Agents** quyết định. Không cần đụng code.

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
| `BusinessAnalyst/requirement-chat.v3.md` | Lượt chat BA |
| `BusinessAnalyst/requirement-readiness.v3.md` | Cổng kiểm tra "đủ thông tin để viết requirement chưa" |
| `BusinessAnalyst/product-brief.v3.md` | Sinh Product Brief (Write Requirement) |
| `BusinessAnalyst/product-brief-review.v2.md` | Vòng tự soát Product Brief |
| `BusinessAnalyst/ai-design-spec.v1.md` | Sinh AI Design Spec sau Approve |
| `BusinessAnalyst/technical-docs.v1.md` | Sinh BRD/SRS/FSD/UserStories (bước 2 pipeline) |
| `BusinessAnalyst/conversation-summary.v1.md` | Gộp tóm tắt hội thoại (bộ nhớ dài hạn) |
| `BusinessAnalyst/user-memory.v1.md` | Chắt lọc hồ sơ user |
| `BusinessAnalyst/checklist-gap.v1.md` | Rút "khoảng trống checklist" sau khi sinh tài liệu |
| `BusinessAnalyst/requirement-coverage.v1.md` | Cập nhật bản đồ bao phủ yêu cầu |
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
- Xem POC: `GET /Projects/Mockup?projectId=` — endpoint **sandbox riêng** (HTML do LLM sinh không được thả vào layout chính).
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
| **Projects** (trang chủ) | `Projects` | `Index` (lọc theo chủ nếu không có `ProjectsViewAll`), `POST Create`, `Mockup` (xem POC sandbox), `DownloadSource` (zip) | `ProjectsView`; Create: `ProjectsCreate` |
| **Requirements** (workspace chat BA) | `Requirements` | `Index`, `POST Chat`, `POST UploadSource`/`DeleteSource`, `POST WriteRequirement`, `POST Approve`, `POST NewChat`, `GET WorkflowStatus`/`WorkflowStream` (SSE), `GET DocumentRevisions`/`DocumentRevisionDiff`/`DocumentPreview`/`DownloadDocument` | `RequirementsView`; mọi thao tác ghi: `RequirementsManage` |
| **Agent Dashboard** (điều phối delivery) | `AgentDashboard` | `Index`, `GET WorkflowStatus`/`ActiveAgents`/`AgentStats`/`AgentActivity`/`AgentCallLogs`/`CallLogDetail`/`DocumentPreview`, `POST ApproveStage`/`RejectStage`/`RequestRevision`/`RetryWorkflow`/`UpdateDeliveryConfig` | `AgentsView`; các POST cổng duyệt: `DeliveryAdvance` |
| **Agents** (cấu hình agent) | `Agents` | `Index`, `POST Update` (model, temperature, tools...) | `AgentsView` / `AgentsManage` |
| **AI Models** | `Models` | `Index`, `POST Create`/`Update`/`Delete` | `ModelsView` / `ModelsCreate`/`Edit`/`Delete` |
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

### 13.2. Phân quyền (3 role người dùng × quyền mức hành động)

- `UserRole`: **Admin / TeamDev / User** — *khác hẳn* `AgentRoleKey` (vai của AI).
- Quyền mức hành động: enum `AppPermission` (24 quyền — xem bảng §12). `PermissionCatalog` (Domain/Security) gom quyền theo màn hình để render ma trận + lọc menu sidebar.
- **Một nguồn sự thật**: `IPermissionService` (cache MemoryCache; **Admin implicit-all** nên không tự khóa được), dùng bởi filter `[RequirePermission(...)]` và `_Layout.cshtml`.
- Cấu hình runtime ở màn Roles; lưu xong `InvalidateCache()` ⇒ **hiệu lực ngay, không cần đăng nhập lại**. Thiếu quyền ⇒ `/Account/AccessDenied`.
- **Thêm quyền mới**: thêm giá trị `AppPermission` → khai báo vào `PermissionCatalog.Screens` → gắn `[RequirePermission]` → (nếu là menu) thêm nhánh `@if` trong `_Layout.cshtml` → cân nhắc seed/backfill trong `DbInitializer`.

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
| `Database:Provider` | `SqlServer` | `SqlServer` hoặc `Sqlite`. Development.json đặt sẵn `Sqlite`. Sqlite mà connection string vẫn dạng SQL Server ⇒ tự fallback file `ICOGenerator.db` |
| `ConnectionStrings:DefaultConnection` | `Server=SONGLONG;...` | Chuỗi kết nối. SqlServer bật `EnableRetryOnFailure` |
| `AgentWorkspace:RootPath` | `C:\Study App\ICOGeneratorWorkspaces` | Thư mục gốc workspace agent. **Phải đổi theo máy** |
| `AllowedCommands` | dotnet, git status/diff/add/commit/push/checkout/remote get-url, dir, npm, node | Whitelist lệnh cho `RunCommand` |
| `AllowedFileExtensions` | .cs .cshtml .css .scss .ts .js .json .md .txt .sln .csproj .html .sql .yml .yaml | Whitelist đuôi file cho tool file |
| `BoschTemplate:BackendRepoUrl/FrontendRepoUrl/Branch` | trống | Repo khung Bosch để clone skeleton; trống = bỏ qua. Nạp URL private qua env |
| `Commands:TimeoutSeconds` | 120 | Timeout mỗi lệnh RunCommand |
| `Feedback:UploadRootPath` | trống ⇒ `{ContentRoot}/FeedbackUploads` | Nơi lưu file đính kèm feedback |
| `Feedback:MaxFileBytes` / `MaxFilesPerFeedback` | 50MB / 8 | Trần file đính kèm |
| `PullRequest:RemoteName/BaseBranch/GitHubToken` | origin / main / trống | Bước PR: token trống hoặc remote không phải GitHub ⇒ fallback link compare |
| `Notifications:BaseUrl` | trống | URL gốc app để dựng link tuyệt đối trong Teams/email |
| `Notifications:Teams:{Enabled,WebhookUrl}` | tắt | Incoming Webhook Teams. Fail-open |
| `Notifications:Email:{Enabled,Host,Port,UseStartTls,Username,Password,From,To}` | tắt / 587 STARTTLS | SMTP. Password qua env. Fail-open |
| `Llm:Proxy:{Enabled,Address}` | false / `http://127.0.0.1:3128` | Proxy công ty cho lời gọi LLM ra ngoài (client "proxied"); code mặc định coi Enabled=true nếu **thiếu key** — appsettings hiện đặt tường minh false |
| `Llm:StructuredOutput:{Enabled,ModelIds}` | false / [] | Opt-in `response_format: json_schema` cho các lời gọi BA trả JSON, chỉ với ModelId liệt kê |
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
- **Kênh ngoài (Teams webhook, SMTP email)**: opt-in qua config, fail-open (lỗi gửi chỉ log warning, không gãy workflow). Kiến trúc plugin: hiện thực `INotificationChannel` mới + đăng ký DI là xong.
- **Tùy chọn theo user**: `/Notifications/Preferences` — bật/tắt kênh, chọn loại sự kiện, email cá nhân.

### 16.2. Budget guard
`IBudgetGuard` chặn **trước** mỗi lời gọi model khi tổng chi phí trong kỳ (`Monthly`/`Daily`/`Total`) chạm trần hệ thống hoặc trần mỗi-project. Chi phí tính y hệt trang Usage. Chỉ chính xác khi model khai báo đơn giá.

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

Bố cục test khớp bố cục code — sửa ở đâu, tìm test ở thư mục cùng tên. Các parser (verdict, judge, readiness, chat reply...), use case cổng duyệt, budget, notification, prompt studio... đều có test.

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
| App cố kết nối SQL Server dù bạn muốn Sqlite | Env đang là `Production` (launchSettings ép vậy khi `dotnet run`). Chạy DLL trực tiếp với `ASPNETCORE_ENVIRONMENT=Development` (§3.3-B) |
| `Unable to resolve service for type ...` | Quên đăng ký DI trong `ApplicationServiceCollectionExtensions` — thêm vào đúng nhóm `AddXxx()` |
| `dotnet build` fail `MSB3552: **/*.resx cannot be found` (Linux) | Lần chạy trước tạo thư mục literal `C:\Study App\...` trong repo (root path Windows). Xóa thư mục rác đó + `Logs/`; lần sau set `AgentWorkspace__RootPath` |
| ApiKey model giải mã lỗi / gọi LLM báo key sai sau khi đổi máy/khóa | `Encryption__ApiKeyKey` khác với khóa lúc mã hóa. Dùng lại khóa cũ, hoặc nhập lại ApiKey ở màn AI Models |
| Lỗi `Value cannot be an empty string (Parameter 'key')` khi agent chạy | Model đang chọn có ApiKey rỗng (model seed DeepSeek để trống) — điền ApiKey hoặc trỏ agent sang model khác |
| Agent chạy "thành công" nhưng Output rỗng (khi dùng stub/proxy) | Endpoint không hỗ trợ **SSE streaming** — app đọc stream. Stub phải trả `text/event-stream` |
| Task đứng `Running` mãi sau khi app restart | Bình thường: `DbInitializer` sẽ re-queue ở lần khởi động kế (tối đa 3 lần thử rồi Failed). Không tự sửa tay Status trong DB khi app đang chạy |
| Đổi quyền ở màn Roles mà user kêu không thấy thay đổi | Không thể — cache được invalidate ngay khi lưu. Kiểm tra lại đúng role, và nhớ **Admin luôn full quyền** bất kể ma trận |
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
| **Opt-in** | Nguyên tắc cấu hình: tính năng có phụ thuộc ngoài (Proxy, StructuredOutput, Otel, Budget limits, Teams/Email) mặc định TẮT |

---

*Tài liệu này mô tả đúng trạng thái code tại thời điểm viết (07/2026). Khi thấy lệch giữa tài liệu và code, hãy tin code — và sửa tài liệu trong cùng PR.*
