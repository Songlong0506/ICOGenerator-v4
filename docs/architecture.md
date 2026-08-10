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
                  (đều có thể dùng Domain, Contracts & Configuration)
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
> case cụ thể — chủ yếu `Services.Security` (`IPermissionService`, 14 controller), thêm vài chỗ lẻ
> (`Artifacts`, `Workflows`, `Identity`, `Feedback`, `Budget`, `Data`). Đây là nợ đã biết, không
> phải mẫu để nhân rộng: logic nghiệp vụ mới vẫn phải đi qua `Application`.

---

## Bản đồ thư mục

```
Program.cs               # Điểm vào: Serilog bootstrap, middleware pipeline, gọi DbInitializer
Extensions/              # ApplicationServiceCollectionExtensions — NƠI DUY NHẤT đăng ký DI
Domain/                  # Entity nghiệp vụ + Enums/ + Security/PermissionCatalog. Không phụ thuộc gì.
Contracts/               # DTO hợp đồng dữ liệu (BrdDto, FsdDto, ProductBriefDto...) — POCO thuần
Configuration/           # POCO cấu hình bind từ appsettings (AuthenticationSettings, IdentityServerSettings)
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
Controllers/             # 18 MVC controller mỏng (xem docs/screens-and-permissions.md)
Views/                   # Razor views (.cshtml) — mỗi màn hình một thư mục
wwwroot/                 # css/ + js/ thuần theo màn hình (requirements.js, agent-dashboard.js...)
Prompts/                 # Template prompt .md (copy ra output khi build) — xem docs/llm-and-prompts.md
Templates/               # BRD_Template.docx, SRS_Template.docx, FSD_Template.docx
tests/ICOGenerator.Tests # xUnit
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
            │     └► RequirementDocumentGenerator -> Templates/DocxTemplateWriter
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

### Dữ liệu seed lớn là RESOURCE, không phải code
Bộ dữ liệu HR_Portal (1549 `Associate` + 195 `OrgUnit`) từng là mảng khởi tạo viết tay trong C#
(`AssociatesSeedData.All`, ~38k dòng — chiếm hơn một phần ba codebase). Nó là **dữ liệu** nhưng vẫn
phải qua trình biên dịch: assembly phình lên và Roslyn/IDE parse lại toàn bộ mỗi lần gõ trong thư mục
`Data`. Nay nội dung nằm ở `Data/SeedData/*.ndjson`, nhúng vào assembly qua `<EmbeddedResource>`
(`LogicalName` đặt tường minh) và đọc bằng `SeedDataResource.Load<T>()`. Rebuild dự án giảm từ
**~16s xuống ~5s**.

Hai lựa chọn có chủ đích:
- **NDJSON (mỗi bản ghi một dòng)** thay vì một mảng JSON: mảng nằm trọn trên một dòng 800KB thì
  `git diff` vô dụng — lần đồng bộ lại từ HR_Portal sau này sẽ không review được. Tách dòng thì thấy
  đúng bản ghi nào đổi, mà dung lượng y hệt.
- **`Load()` thay cho `static readonly`**: seed chỉ chạy một lần lúc khởi động, xong thì mảng được GC
  thu hồi thay vì nằm lại trong bộ nhớ suốt vòng đời tiến trình.

Rủi ro mới của dạng nhúng là hỏng **âm thầm** (quên khai báo `<EmbeddedResource>`, sai `LogicalName`,
file bị cắt) — app vẫn build, tới lúc chạy mới lộ. `HrPortalSeedDataTests` chốt số bản ghi và giá trị
một bản ghi mốc để bắt cả ba trường hợp đó ở CI.
