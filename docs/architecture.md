# Kiến trúc

## Tổng quan

- **Loại ứng dụng:** ASP.NET Core MVC (.NET 8), EF Core (SqlServer).
- **Bài toán:** một hệ thống AI agent — nhận yêu cầu (requirement) từ người dùng, để các "agent"
  dùng LLM + công cụ (tool) tạo ra tài liệu/đặc tả và chạy các workflow nền.
- **Kiểu kiến trúc:** **Layered Architecture (kiến trúc phân lớp) thực dụng**, kết hợp pattern
  **Use Case / Command–Query mỗi thao tác một class** ở tầng Application.

> Đây *không* phải Clean Architecture "sách giáo khoa" (tầng Application ở đây phụ thuộc trực tiếp
> vào `Data`/EF Core và các service cụ thể, thay vì chỉ phụ thuộc abstraction). Cách làm hiện tại
> đơn giản và đủ dùng cho quy mô này — tài liệu mô tả đúng cái đang có, không tô vẽ.

---

## Kiến trúc phân tầng: Layered + "một use case một class"

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

## Chiều phụ thuộc (dependency rule)

Mũi tên = "được phép phụ thuộc vào". Phụ thuộc chỉ đi **một chiều, từ trên xuống**:

```
Controllers ─► Application ─► Services ─► Data ─► Domain
                   │              │                  ▲
                   └──────────────┴──────────────────┘
                  (đều có thể dùng Domain, Contracts, Configuration & Localization)
```

Luật bất di bất dịch:
- **Domain** không phụ thuộc gì (chỉ tự tham chiếu `Domain.Enums`). Đây là tầng ổn định nhất.
- **Contracts** thuần POCO, không phụ thuộc layer khác.
- **Configuration** POCO cấu hình, chỉ phụ thuộc `Domain`. Là lá dùng chung nên **mọi** layer đọc
  được: `Extensions` bind DI, `Services` đọc khi gọi API ngoài, `Controllers`/`Views` đổi hành vi
  hiển thị. Để ở đây thay vì `Application/` chính vì `Services` cũng cần đọc — đặt trong
  `Application/` sẽ ép `Services` `using` ngược lên (đúng lỗi từng xảy ra với `Application/Account`).
- **Services** *không bao giờ* `using` ngược lên `Application` hay `Controllers`.
- **Application** điều phối: được phép gọi `Data`, `Domain`, `Services`.
- **Controllers** gọi `Application` cho nghiệp vụ, không tự viết logic.

> Đã kiểm chứng: không layer nào dưới `Application` `using` ngược lên `Application`/`Controllers`.
> Ngoại lệ *có chủ đích* ở chiều `Controllers → Services`: service cắt ngang không thuộc về một use
> case cụ thể — chủ yếu `Services.Security` (`IPermissionService`/`[RequirePermission]`, 16 controller), thêm vài chỗ lẻ
> (`Artifacts`, `Workflows`, `Identity`, `Feedback`, `Budget`, `Data`). Đây là nợ đã biết, không
> phải mẫu để nhân rộng: logic nghiệp vụ mới vẫn phải đi qua `Application`.

---

## Bản đồ thư mục

```
Program.cs               # Điểm vào: Serilog bootstrap, middleware pipeline, gọi DbInitializer
Extensions/              # ApplicationServiceCollectionExtensions — NƠI DUY NHẤT đăng ký DI
Domain/                  # Entity nghiệp vụ + Enums/ + Security/PermissionCatalog. Không phụ thuộc gì.
Contracts/               # DTO hợp đồng dữ liệu (BrdDto, FsdDto, ProductBriefDto...) — POCO thuần
Configuration/           # POCO cấu hình bind từ appsettings (IdentityServerSettings — gồm cả công tắc đăng nhập cục bộ LocalRole;
                         #   EmployeeApiSettings — đồng bộ dữ liệu tổ chức)
Localization/            # Loc (lấy câu chữ viết cho NGƯỜI theo ngôn ngữ) + Text.resx — mọi tầng đều dùng được
Data/                    # AppDbContext, DbInitializer (migrate+seed), UtcDateTimeConverter, seed data
Migrations/              # EF migrations (SQL-Server-specific, tự sinh — không sửa tay)
Application/             # Use case theo khu vực màn hình:
  Account/ Agents/ Audit/ Evals/ Feedback/ Models/ Notifications/
  Projects/ Prompts/ Quality/ Requirements/ Roles/ Settings/ SystemLogs/ Usage/
Services/
  Agents/                # Vòng lặp agent: AgentRunService, AgentInstructionProvider, AgentPromptBuilder,
                         #   InvokerBackedAIFunction (middleware bọc tool)
  Artifacts/             # Workspace & sản phẩm: WorkspacePathResolver, LocalArtifactStorage,
                         #   PocTemplate/PocAudit/PocSpec, ImplementationSourcePackager,
                         #   ProjectRepositoryLayout/ProjectRepositorySeeder + BoschTemplateSeeder (repo đích + skeleton)
  Budget/                # BudgetGuard/BudgetPolicy — trần chi phí LLM theo USD
  Evals/                 # Prompt eval harness: EvalRunnerService, EvalRunWorker, EvalJudgeParser
  Feedback/              # FeedbackAttachmentStore (lưu file đính kèm)
  Logging/               # DatabaseLogSink (sink Serilog → hàng đợi) + DatabaseLogWriter (ghi lô vào SystemLogs)
  Llm/                   # LlmClient, OpenAIChatClientFactory (+ AnthropicVertexChatClient, ClaudeCodeChatClient), ModelCallLoggingChatClient,
                         #   TokenEstimator + ImageDimensions, MaxOutputTokenResolver,
                         #   LlmCost/ModelPriceBook, JsonExtractor...
  Organization/          # OrgChart (cây orgUnit, department = TypeOrganize A/L) + đồng bộ từ employee-api:
                         #   OrganizationSyncJob (Hangfire), OrgUnitSyncService, AssociateSyncService, EmployeeApiClient
  Notifications/         # NotificationService + Channels/ (Teams webhook, SMTP email, Bosch Email Server API)
  Prompts/               # PromptTemplateService, DbPromptOverrideProvider, PromptFileCatalog
  Requirements/          # BAChatService (+ BAChatPromptBlocks, BAChatTurnDraft, BASourceAckPrompt),
                         #   ProductBriefDraftService, RequirementDocsService + trí nhớ/parser/generator của luồng BA
    Templates/           # RequirementTemplateService, DocxTemplateWriter (điền template .docx),
                         #   MarkdownDocxWriter (Markdown -> .docx có bìa/mục lục/heading/bảng)
  Security/              # PermissionService, RequirePermissionAttribute, AesApiKeyProtector, AuditLogger
  Settings/              # AppSettingsFileStore (đọc/ghi appsettings từ màn hình Settings)
  Tools/                 # Tool cho agent: WorkspaceTools, CommandTools, GitTools
    Abstractions/        #   interface/record hợp đồng (IToolExecutionLogger)
    Execution/           #   ToolPolicyService, ToolExecutionLogger
    Registry/            #   ToolDiscoveryService, ToolRegistry, ToolArgumentValidator
    PullRequests/        #   GitHubPullRequestPublisher, PullRequestUrlBuilder, GitRemoteUrl
  Workflows/             # WorkflowOrchestrator, AgentTaskWorker (BackgroundService), DeliveryPipeline,
                         #   WorkflowTaskPromptBuilder, TestVerdictParser, WorkflowProgressReporter,
                         #   PocVariantRunner + PocVariantWorker (biến thể POC thử model, ngoài pipeline)
Controllers/             # 19 MVC controller mỏng (xem docs/screens-and-permissions.md)
Views/                   # Razor views (.cshtml) — mỗi màn hình một thư mục
wwwroot/                 # css/ + js/ thuần theo màn hình (requirements.js, agent-dashboard.js...)
Prompts/                 # Template prompt .md (copy ra output khi build) — xem docs/llm-and-prompts.md
Templates/               # BRD_Template.docx, SRS_Template.docx, FSD_Template.docx
tests/ICOGenerator.Tests # xUnit
tools/TeamExport/        # Công cụ xuất bản cho repo team (không biên dịch vào app) — xem docs/team-export.md
.claude/skills/verify/   # Skill chạy end-to-end không cần SQL Server / LLM thật
```

---

## Luồng xử lý một request (ví dụ: tạo bản nháp requirement)

```
Browser
  └► RequirementsController.WriteRequirement(projectId)     [Controllers] - mỏng
       └► GenerateRequirementDraftUseCase.ExecuteAsync(...)  [Application] - điều phối
            ├► ProductBriefDraftService                      [Services/Requirements]
            │     ├► RequirementPromptBuilder  (dựng prompt)
            │     ├► ILlmClient                 (gọi LLM)      [Services/Llm]
            │     ├► RequirementResponseParser  (parse JSON)
            │     └► RequirementDocumentGenerator -> Templates/MarkdownDocxWriter
            └► AppDbContext.SaveChanges                        [Data]
```

Controller không chứa logic; nó chỉ map HTTP ⇄ use case. Toàn bộ "việc thật" nằm ở
Application (điều phối) và Services (chi tiết kỹ thuật).

---

## Các pattern chính

### Use Case / Command–Query mỗi thao tác một class (tầng Application)
Mỗi hành động người dùng = **một class, một file**, có đúng một method công khai `ExecuteAsync`.

- Class **đọc** đặt tên `...Query`   → `GetProjectListQuery`, `ListAiModelsQuery`.
- Class **ghi/đổi trạng thái** đặt tên `...UseCase` → `CreateProjectUseCase`, `UpdateAgentUseCase`.
- ViewModel của form đặt tên `...Vm` → `ProjectCreateVm`, `AgentEditVm`.

Lợi ích: dễ tìm, dễ test, dễ thêm mới mà không đụng class cũ (Open/Closed).

### Thin Controller
Controller chỉ: nhận tham số → gọi 1 use case → trả `View`/`Json`/`Redirect`.
Không truy vấn DB, không gọi LLM trực tiếp.

### Background processing (Hosted Service + Orchestrator)
- `AgentTaskWorker` là `BackgroundService` chạy nền (poll `AgentTask` ở trạng thái `Queued`).
- `WorkflowOrchestrator` (ẩn sau `IWorkflowOrchestrator`) điều phối các bước workflow.

### Prompt as template
Prompt nằm ở file `.md` trong `/Prompts` (được copy ra output khi build) và nạp/render qua
`PromptTemplateService`. Đổi nội dung prompt không cần build lại logic.

### Đăng ký DI tập trung
Mọi đăng ký dịch vụ nằm ở `Extensions/ApplicationServiceCollectionExtensions.cs`, chia thành các
method nhỏ `AddXxx()` — **mỗi nhóm tương ứng một thư mục/layer**. `Program.cs` chỉ gọi
`AddIcoGeneratorApplication(...)`.

### Job chạy theo lịch đi qua Hangfire
Việc chạy theo **giờ** (khác với các worker nhặt hàng đợi trong DB như `AgentTaskWorker`) dùng Hangfire.
Hiện chỉ có một job: `OrganizationSyncJob`, đồng bộ `OrgUnits`/`Associates` từ employee-api mỗi ngày.
Cơ chế đồng bộ nằm ở [data-model.md](data-model.md#orgunits--associates--đồng-bộ-từ-employee-api), cấu hình
ở [configuration.md](configuration.md).

- **Lưu job ở đâu:** chung DB SQL Server, schema `HangFire`. Hangfire tự tạo bảng của nó, **không** qua EF
  migration. Ở chế độ Sqlite (dev/CI) thì dùng bộ nhớ trong; job mất khi tắt app, chấp nhận được vì lịch
  được đăng ký lại mỗi lần khởi động.
- **Đăng ký lịch:** `Program.cs` gọi `OrganizationSyncJob.ScheduleAsync` ngay sau `DbInitializer`. DI của
  Hangfire nằm ở `AddOrganizationSyncServices` như mọi nhóm khác.
- **Dashboard:** `/hangfire`, chỉ Admin (`HangfireDashboardAuthorizationFilter`) — xem
  [screens-and-permissions.md](screens-and-permissions.md).

## Câu chữ viết cho người dùng nằm ở resource, không nằm trong code

`Localization/Loc.cs` là chỗ **duy nhất** backend lấy câu chữ mà NGƯỜI sẽ đọc: lượt BA do cơ chế dựng sẵn
(câu chặn của cổng, lời xin file, câu dẫn bảng), thông báo lỗi của controller và use case, dòng trạng thái
của worker, tiêu đề thông báo, chữ trong file xuất. Câu chữ nằm ở `Localization/Text.resx` (tiếng Việt —
bản gốc, `Loc.SourceLanguage`) và `Localization/Text.en.resx` (tiếng Anh); thêm một ngôn ngữ là thêm
`Text.<mã>.resx` và một phần tử ở `Loc.SupportedLanguages`. Code chỉ giữ **khoá**:

```csharp
TempData["Error"] = Loc.T("ProjectsController.Create.3");                 // không tham số
Report("final", Loc.T("RequirementDocsService.GenerateAiDesignSpecAsync.15", versionName));  // string.Format
```

| Quy tắc | Vì sao |
|---|---|
| `Loc` là **static**, ngôn ngữ đi theo `AsyncLocal` | Phần lớn cổng/guard/builder phía yêu cầu là hàm thuần static; bắt chúng nhận `IStringLocalizer` qua DI là đổi chữ ký của cả tầng chỉ để đọc một chuỗi |
| Ngôn ngữ chỉ đổi khi có người gọi `Loc.Use(mã)`; không ai gọi ⇒ `Loc.DefaultLanguage` (`en`) | Culture của tiến trình là của máy chủ. Đọc thẳng `CurrentUICulture` thì tiếng của app đi theo máy chủ đang chạy nó, không theo màn hình đang phục vụ |
| Khoá không có bản dịch ⇒ rơi về bản gốc tiếng Việt ở `Text.resx` | Đó là cách các khoá chỉ thuộc phía yêu cầu giữ tiếng Việt dù đang ở tiếng Anh — xem mục dưới |
| Thiếu khoá ⇒ trả **chính khoá**, không ném | Một lượt chat không được chết vì một chuỗi. `LocTests` quét source và fail build khi một khoá dùng trong code không có trong `Text.resx` |
| Chữ gửi cho **MODEL** (prompt, khối ngữ cảnh, báo cáo tự kiểm trả cho agent) **không** đi qua `Loc` | Model đọc được mọi ngôn ngữ; dịch prompt là đổi hành vi đã đo |
| Hằng chuỗi cho người đọc là `static string X => Loc.T(...)`, không phải `const` / `static readonly` | `const` và field tĩnh bị đóng băng lúc nạp kiểu, tức đóng băng luôn ngôn ngữ |
| Tham số attribute (`[RequireProjectAccess(Message = "Project.NotFound")]`) ghi **khoá** | Tham số attribute phải là hằng; filter tra khoá lúc trả lỗi |
| Tên hiển thị của enum nằm ở resource `Enum.<Kiểu>.<Giá trị>` | `EnumDisplay.GetTitle` tra resource trước, rơi về `[Description]` cho enum chỉ có nhãn kỹ thuật |

**Định danh không được là chữ người dùng đọc.** Thứ gì code dùng để NHẬN RA (nhóm của bản đồ bao phủ, loại
luồng, nấc phạm vi phân quyền) là một **mã** không phụ thuộc ngôn ngữ — `CoverageGroupKey`, `FlowKind`,
`PermissionScope` — còn nhãn của nó lấy qua `Loc`. Model được dặn trả về mã; khi model lỡ viết nhãn thay
cho mã, lưới đỡ so với nhãn và các cụm gợi ý ở **mọi** ngôn ngữ (`Loc.InAllLanguages`, resource `*.Cues.*`
đọc qua `LocList`) chứ không so với một bảng cụm tiếng Việt trong code.

### Ngôn ngữ giao diện: tiếng Anh, trừ màn Requirements

**Mọi chữ người dùng đọc trên app — nút, nhãn, placeholder, tiêu đề, tooltip (`title`, `data-tip`,
`aria-label`), tên cột, lựa chọn của dropdown, thông báo flash/toast, `confirm()`, chữ JS vẽ ra — là tiếng
Anh.** Ngoại lệ duy nhất là **màn Requirements** (chat BA với người dùng nghiệp vụ): nó vẫn nói tiếng Việt.
Màn mới, partial mới, component JS mới **mặc định tiếng Anh**; không có cơ chế chọn ngôn ngữ theo người dùng.

| Nguồn chữ | Màn thường (tiếng Anh) | Màn Requirements (tiếng Việt) |
|---|---|---|
| View (`.cshtml`), JS, CSS | Viết thẳng tiếng Anh trong file | `Views/Requirements/**`, `requirements.js`, `requirement-workflow.js`, `table-submission.js`, `requirements.css` |
| Backend qua `Loc` | `Loc.DefaultLanguage = "en"` ⇒ đọc `Text.en.resx` — cả request, worker nền, thông báo | `RequirementsController` gắn `[UiLanguage(Loc.SourceLanguage)]` ⇒ cả request (gồm lúc render View và luồng SSE chat) đọc `Text.resx` |
| Khoá chỉ thuộc phía yêu cầu (`BAChatService.*`, `RequirementDocsService.*`, `CoverageGroup.*`, …) | — | **Không** có bản ở `Text.en.resx` ⇒ luôn ra tiếng Việt, kể cả khi worker nền chạy ở tiếng Anh |

`UiLanguageAttribute` là **resource filter**, không phải action filter: action filter chỉ bọc action, View
render sau đó và sẽ quay về tiếng Anh.

`UiLanguageTests` chốt luật này lúc build:

- View/JS/CSS ngoài danh sách file của màn Requirements không được có chữ tiếng Việt **ngoài comment**
  (comment vẫn viết tiếng Việt theo quy ước repo).
- Mọi khoá ở `Text.resx` phải có bản ở `Text.en.resx`, trừ các tiền tố trong `RequirementsOnlyKeyPrefixes`
  (phía yêu cầu, và chữ nối vào bàn giao cho model như `PocUatGate.Handoff.*`). `Text.en.resx` không được
  chứa khoá thừa hay chữ tiếng Việt.
- Chỉ `RequirementsController` được gắn `[UiLanguage]`.

Thêm một thông báo cho màn thường = thêm khoá vào **cả** `Text.resx` lẫn `Text.en.resx`. Thêm một khoá chỉ
phục vụ màn Requirements = chỉ `Text.resx`, và nếu tiền tố là mới thì thêm nó vào `RequirementsOnlyKeyPrefixes`.

**Những chữ KHÔNG thuộc luật này:** dữ liệu người dùng nhập và nội dung do model sinh (tên dự án, Product
Brief, nhãn màn hình trong bản thiết kế UX, kết quả tự kiểm POC gửi lại cho agent) giữ nguyên ngôn ngữ của nó;
chữ gửi cho model không đi qua `Loc` như bảng trên đã nói.

