# Mô hình dữ liệu

`Data/AppDbContext.cs` khai báo **28 DbSet**. Điểm chung cần biết trước:

- **Mọi cột `DateTime` được chuẩn hóa `Kind=Utc` khi đọc** (`UtcDateTimeConverter`) để JSON trả ra có hậu tố `Z` — tránh lệch múi giờ trên client.
- **Hầu hết enum lưu dạng chuỗi** (tên enum, ví dụ `'WaitingForHuman'`) — dễ đọc trong DB và bền khi chèn giá trị enum mới. ⚠️ Vì vậy **đừng đổi tên giá trị enum đã có dữ liệu**.
- **`AiModel.ApiKey` được mã hóa AES** bằng value-converter gắn `AesApiKeyProtector`. Protector **bắt buộc là Singleton** (EF cache model toàn cục, converter capture instance đầu tiên) — đừng đổi lifetime, đừng bật `AddDbContextPool`.

## Bản đồ các bảng

### Nhóm lõi: Project & Agent

| Bảng | Vai trò | Điểm đáng chú ý |
|---|---|---|
| `AgentChecklistItem` | Một bài học trong "checklist BA học được": `DomainKey` (null = mọi dự án), `Text` (phần duy nhất vào prompt), `Rationale`/`Evidence` (vì sao rút ra — chỉ cho trang quản trị), `SourceKind`/`SourceProjectId`, `Status` (Active/DisabledByUser/DisabledByOverflow) | Trần 25 mục đang dùng mỗi bucket; vượt thì mục cũ nhất tự chuyển `DisabledByOverflow` (vẫn thấy, bật lại được). FK dự án nguồn là `SetNull` — xóa dự án không xóa bài học |
| `Projects` | Dự án — gốc nối tới tài liệu, hội thoại, workflow | Ngoài metadata còn mang **bộ nhớ của luồng BA**: `ConversationSummary` + `SummarizedTurnCount` (tóm tắt hội thoại dài), `UserMemoryHarvestedTurnCount`, `RequirementCoverageMap` + `CoverageHarvestedTurnCount` (bản đồ bao phủ 12 nhóm thông tin), `ChecklistGapHarvested`; và **nghiệm thu bản demo** `PocAcceptedAtUtc` + `PocAcceptedBy` (null = người yêu cầu chưa xác nhận POC đạt). `CreatedByUsername` để lọc "chỉ thấy project mình tạo"; `OrgUnitCode` (không FK) gắn đơn vị yêu cầu; `IsUseBoschTemplate` (mặc định true) do TeamDev đổi ở Agent Dashboard |
| `Agents` | "Nhân sự AI": `RoleKey` (BusinessAnalyst/TechLead/Developer/Tester/UiUx), `AiModelId`, `Temperature`, `Color` | System prompt **không lưu DB** — nạp từ `Prompts/{RoleKey}/instruction.md` qua `AgentInstructionProvider`. FK sang AiModel là `Restrict` (không xóa được model đang dùng) |
| `AiModels` | Danh mục model LLM: `ModelId`, `Endpoint`, `ApiKey` (mã hóa), `ContextWindow`, đơn giá Input/Output per-1M-token (decimal 18,6) | Đơn giá là đầu vào của trang Usage + Budget guard. Model tự host giá 0 ⇒ chi phí 0 |
| `ToolDefinitions` | Danh mục tool (đồng bộ từ code khi khởi động) | Unique index `(ServiceType, MethodName)` |
| `AgentTools` | Bảng nối agent ↔ tool được phép dùng | Khóa chính kép `(AgentId, ToolDefinitionId)` |

### Nhóm tài liệu & hội thoại

| Bảng | Vai trò | Điểm đáng chú ý |
|---|---|---|
| `ProjectDocuments` | Tài liệu sinh ra (ProductBrief/AIDesignSpec/BRD/SRS/FSD/UserStories...): `Folder`, `VersionName`, `FileName`, `FilePath`, `Content`, `IsApproved` | Cascade theo Project |
| `ProjectDocumentRevisions` | **Lịch sử nội dung** mỗi lần document bị ghi đè CÓ thay đổi — snapshot đầy đủ + `ChangeNote` nguồn gốc | Chốt chặn duy nhất tạo revision là `RequirementDocumentGenerator.UpsertDocument`. Diff tính lúc xem bằng `DocumentDiffService` (LCS theo dòng). Unique `(DocumentId, RevisionNumber)` |
| `ProjectSourceFiles` | Tài liệu nguồn user upload cho BA đọc (ảnh / PDF / Word .docx / Excel-CSV) — `ExtractedText` do `ProjectSourceIngestor` trích; PDF **scan** không có text thì lấy ảnh nhúng từng trang ra `page-{n}.png`, còn trang PDF **có text** cũng như Word có **hình nhúng** (screenshot, sơ đồ) thì lấy các hình đủ lớn ra `figure-{n}.png` cạnh file gốc (`ScannedPageImageCount` đếm TỔNG cả hai loại) cho model vision | Cascade theo Project |
| `AgentConversations` | Từng lượt hội thoại user ↔ agent trong project | Project FK Cascade, Agent FK **Restrict** (xóa agent không wipe lịch sử) |
| `AgentModelCallLogs` | Log **mỗi lời gọi model**: request/response JSON, token, thời lượng, `Purpose`, `WorkflowRunId` (cột nhóm, cố ý không FK). Ảnh đã gửi kèm chỉ được **mô tả** trong `RequestJson` (tên/kiểu/dung lượng/số thứ tự), bytes nằm trên đĩa — xem ["Ảnh trong call log"](requirement-flow.md#tài-liệu-nguồn-ảnh-và-call-log) | Nguồn dữ liệu của trang Usage, popup AI Call Logs, Delivery Quality |

### Nhóm workflow

| Bảng | Vai trò | Điểm đáng chú ý |
|---|---|---|
| `WorkflowRuns` | Một lần chạy quy trình cho project: `Status` (Queued/Running/WaitingForHuman/Completed/Failed/Canceled), `CurrentStage` (`WorkflowStageKey`) | Cascade theo Project; index `(ProjectId, Status, CreatedAt)` |
| `AgentTasks` | Một đầu việc giao cho một agent trong run: `Type`, `Status`, `Input`, `Output`, `Error`, `Attempt`, `RevisionFeedback` (null = task thường) | Agent FK `SetNull`, Project FK `Restrict`. **Index `(Status, CreatedAt)` phục vụ worker poll mỗi 2s** — đừng xóa |

### Nhóm người dùng & bảo mật

| Bảng | Vai trò | Điểm đáng chú ý |
|---|---|---|
| `AppUsers` | Tài khoản đăng nhập: `Username` (unique), `DisplayName`, `OrgUnitName` (đồng bộ từ claim `department` của SSO), `UserMemory` (hồ sơ cá nhân hóa BA học được), tùy chọn thông báo (`NotifyInApp/ByEmail/OnGate/OnCompleted/OnFailed`, `Email`) | **Không có cột mật khẩu** — đăng nhập do provider ngoài quyết định (xem [screens-and-permissions.md](screens-and-permissions.md#xác-thực--hai-provider-không-có-mật-khẩu-trong-app)). Chưa có UI tạo user — seed 4 tài khoản cố định |
| `AppUserRoles` | Bảng nối user ↔ vai trò — **một user giữ NHIỀU `UserRole`** | Khóa chính kép `(AppUserId, Role)`; index trên `Role` cho chiều "ai đang giữ vai trò X". Quyền hiệu lực = HỢP quyền của mọi vai trò |
| `RolePermissions` | Cấp quyền `(Role, Permission)` — cấu hình runtime ở màn Roles | Unique `(Role, Permission)`. SuperAdmin implicit-all, không có dòng nào |
| `AuditLogs` | Nhật ký thay đổi cấu hình (Settings/Roles/Agent/Model/Prompt): actor, before/after JSON | Ghi qua `IAuditLogger` |

### Nhóm vệ tinh

| Bảng | Vai trò |
|---|---|
| `Feedbacks` + `FeedbackAttachments` | Phản hồi người dùng toàn app (bug/góp ý/trải nghiệm) kèm file đính kèm; file gốc lưu đĩa (`Feedback:UploadRootPath`), DB chỉ giữ metadata |
| `OrgUnits` + `Associates` | Dữ liệu tổ chức đồng bộ từ HR_Portal (phòng ban, nhân sự) — nguyên liệu cho `OrganizationContextService` |
| `Notifications` | Thông báo in-app (chuông): index `(RecipientUsername, IsRead, CreatedAt)` |
| `EvalScenarios` / `EvalRuns` / `EvalResults` | Prompt eval harness (golden set + LLM-judge). Model/scenario tham chiếu bằng **Guid + snapshot tên, không FK** — xóa không mất lịch sử điểm |
| `PromptTemplateVersions` | Phiên bản prompt chỉnh runtime (Prompt Studio): snapshot đầy đủ, unique `(PromptKey, VersionNumber)`, tối đa một `IsActive` mỗi key |
| `PocComments` | Ghi chú GHIM trực tiếp lên phần tử trong POC (trang POC Review): màn hình + nhãn + CSS selector + vị trí. `Open` → gom vào "Yêu cầu chỉnh sửa" ở cổng POC → `Sent` (không gửi lặp) |
| `PocShareLinks` | Link chia sẻ bản demo cho người **không có tài khoản** (sếp, người dùng cuối): `Token` (base64url 32 byte, unique), `Label`, `ExpiresAtUtc` (**bắt buộc**), `RevokedAtUtc`. Ba lớp giới hạn: chỉ mở đúng demo của một project, luôn có hạn dùng, thu hồi được. Token lưu nguyên văn để copy lại link — đánh đổi có chủ ý, chấp nhận được vì thứ nó mở ra là demo dữ liệu giả |

## Migration

- Đổi entity ⇒ `dotnet ef migrations add <Tên>`; `DbInitializer` tự `MigrateAsync` lúc khởi động (SqlServer).
- Migration hiện tại là một **baseline `V1` duy nhất** (đã gộp toàn bộ lịch sử; các migration tiến lẻ tẻ trước đây không còn). Khi cần sinh migration, để `Database:Provider` là `SqlServer` (mặc định) — **đừng** đặt `Database__Provider=Sqlite` — để nó sinh theo provider SqlServer (không phải Sqlite).
- Sqlite **không chạy migration** (dùng `EnsureCreated`) ⇒ đổi schema khi dev Sqlite = xóa file `ICOGenerator.db*` để dựng lại.

---

## ERD mức cao

```mermaid
erDiagram
    Project ||--o{ ProjectDocument : has
    ProjectDocument ||--o{ ProjectDocumentRevision : has
    Project ||--o{ ProjectSourceFile : has
    Project ||--o{ AgentConversation : has
    Project ||--o{ WorkflowRun : has
    Project ||--o{ AgentTask : has
    Project ||--o{ AgentModelCallLog : has

    WorkflowRun ||--o{ AgentTask : contains
    Agent ||--o{ AgentTask : assigned
    Agent ||--o{ AgentConversation : writes
    Agent ||--o{ AgentModelCallLog : logs

    AiModel ||--o{ Agent : powers
    Agent ||--o{ AgentTool : has
    ToolDefinition ||--o{ AgentTool : assigned

    Feedback ||--o{ FeedbackAttachment : has
    EvalRun ||--o{ EvalResult : has
```

## Core project schema

```mermaid
erDiagram
    Project {
        Guid Id PK
        string Name
        string Description
        string BackendGitUrl
        string FrontendGitUrl
        bool IsUseBoschTemplate
        string CreatedByUsername
        string OrgUnitCode
        string ConversationSummary
        int SummarizedTurnCount
        int UserMemoryHarvestedTurnCount
        bool ChecklistGapHarvested
        string RequirementCoverageMap
        int CoverageHarvestedTurnCount
        DateTime CreatedAt
    }

    ProjectDocument {
        Guid Id PK
        Guid ProjectId FK
        Guid AgentId FK "optional"
        string Folder
        string VersionName
        bool IsApproved
        string FileName
        string Content
        string FilePath
        int TokenUsed
        DateTime CreatedAt
    }

    ProjectDocumentRevision {
        Guid Id PK
        Guid ProjectDocumentId FK
        int RevisionNumber
        string Content
        string ChangeNote
        string VersionName
        DateTime CreatedAt
    }

    ProjectSourceFile {
        Guid Id PK
        Guid ProjectId FK
        SourceFileKind Kind
        string FileName
        string ContentType
        long SizeBytes
        string StoredPath
        string ExtractedText
        string ColumnMap
        string PageImagePaths
        int PageCount
        bool IsVisionSource
        string UploadedByUserId
        DateTime CreatedAt
    }

    AgentConversation {
        Guid Id PK
        Guid ProjectId FK
        Guid AgentId FK
        string Role
        string Message
        string Suggestions
        string Questions
        string ColumnMap
        string PermissionMatrix
        string FlowMap
        string ScreenScopeMap
        string EntityMap
        string ReportMap
        string NotificationMap
        int TokenUsed
        DateTime CreatedAt
    }

    Project ||--o{ ProjectDocument : Documents
    ProjectDocument ||--o{ ProjectDocumentRevision : Revisions
    Project ||--o{ ProjectSourceFile : SourceFiles
    Project ||--o{ AgentConversation : Conversations
```

### Ghi chú thiết kế

- `Project.OrgUnitCode` không FK tới `OrgUnits` để project cũ vẫn giữ nhãn lịch sử nếu dữ liệu HR bị đồng bộ lại/xóa.
- `ProjectDocumentRevision` có unique index `(ProjectDocumentId, RevisionNumber)` để bảo toàn thứ tự version.
- `ProjectSourceFile.ExtractedText` và `PageImagePaths` là LOB, dùng cho context BA/vision.
- `ProjectSourceFile.ColumnMap` (JSON `SourceColumnNote[]`) là **bảng cột đã được người dùng chốt** cho nguồn bảng tính: cột nào ứng dụng mới dùng và nghĩa của nó. `SourceContextBuilder` gắn nó vào ngữ cảnh mọi lượt chat, `RealSampleDataReader` lọc dữ liệu mẫu theo nó — xem [requirement-flow.md](requirement-flow.md#bảng-cột-chốt-phạm-vi-cột-của-file-bảng-tính). **Không** mã hóa at rest, cùng lý do với `ExtractedText` nằm cạnh nó dưới dạng plaintext.
- `AgentConversation.ColumnMap` giữ **bản đề xuất** của BA ở lượt đọc file (để F5 không mất bảng chưa tích); nó là nội dung hội thoại nên **có** mã hóa at rest như `Message`/`Suggestions`/`Questions`.
- `Project.PermissionMatrix` (JSON `PermissionMatrixRow[]`) là **bảng phân quyền đã được người dùng chốt**: màn hình × chức năng × vai trò, mỗi ô mang **phạm vi dữ liệu** (`của mình` / `của đơn vị` / `tất cả`, rỗng = không có quyền). Nó là nguồn bằng chứng RIÊNG của nhóm «Phân quyền theo nghiệp vụ» trong bản đồ bao phủ, và là đường duy nhất đưa phân quyền tới POC ở dạng máy đọc được — xem [requirement-flow.md](requirement-flow.md#bảng-phân-quyền-chốt-nhóm-phân-quyền-ở-cuối-buổi). `AgentConversation.PermissionMatrix` giữ **bản đề xuất** của BA ở lượt bày bảng (để F5 không mất các ô chưa chọn), mã hóa at rest như `ColumnMap`.
- `Project.FlowMap` / `ScreenScopeMap` / `EntityMap` / `ReportMap` / `NotificationMap` là **năm bảng chốt còn lại của buổi phỏng vấn** (cùng `PermissionMatrix` là sáu), cùng khuôn với `PermissionMatrix` (BA điền sẵn → người dùng sửa/bỏ tích → chốt một lần → khối "đã chốt" đi vào ngữ cảnh chat, lượt distill bản đồ bao phủ và prompt sinh AI Design Spec). Cột tương ứng trên `AgentConversation` giữ bản đề xuất của lượt bày bảng, mã hóa at rest như `ColumnMap`. Xem [requirement-flow.md](requirement-flow.md#sáu-bảng-chốt-của-buổi-phỏng-vấn).
  - `FlowMap` (JSON `FlowMapRow[]`): luồng nghiệp vụ theo vai trò, luồng chính + ngoại lệ, mỗi luồng là chuỗi bước `{actor, action, outcome}`.
  - `ScreenScopeMap` (JSON `ScreenScopeRow[]`): màn hình dự kiến + việc của từng màn + **các chức năng trên màn** (`ScreenFunction[]`, mỗi chức năng có cờ tích riêng và các **bước luồng** nó phục vụ) + `Covers` (các mục `PlannedScope` đã được gộp vào màn này thay vì đứng thành dòng riêng). Là nguồn DÒNG của bảng phân quyền (`PermissionMatrixGate.EffectiveScreens`) thay cho `PlannedScope` thô, nên dòng của nó chỉ được là MÀN HÌNH — xem [requirement-flow.md](requirement-flow.md#ba-cột-của-bảng-màn-hình-và-vì-sao-cột-màn-hình-chỉ-được-chứa-màn-hình). Bản cũ (`Functions` là một chuỗi ngăn phẩy, `FlowSteps` ở cấp màn hình) vẫn đọc lại được: `ScreenScopeMapBuilder.Parse` nâng cấp tại chỗ.
  - `EntityMap` (JSON `EntityMapRow[]`): đối tượng nghiệp vụ + thông tin cần lưu + vòng đời trạng thái. Mỗi thông tin chở thêm HAI TRỤC — `Input` (nhập thế nào: `text`/`number`/`date`/`choice-one`/`choice-many`/`auto`) và `Source` (danh sách lấy ở đâu, chỉ có nghĩa với hai kiểu chọn: `inline`/`app`/`external`) — cùng `Required` và ba ô của ba nhánh nguồn (`Options`, `SourceSystem`, `Rule`). Chuỗi, không phải enum: dữ liệu đến từ model và từ trình duyệt nên giá trị lạ phải chuẩn hoá được về mặc định an toàn thay vì làm hỏng cả lượt deserialize; bản ghi lưu TRƯỚC khi hai trục tồn tại đọc ra `Input = text` nhờ giá trị khởi tạo của contract. Thông tin có `Source = app` còn gieo một mục màn hình vào `Project.PlannedScope` lúc chốt. Vòng đời của nó là nguồn DÒNG của bảng thông báo ngay dưới. Trường `EntityLifecycleState.Notify` là DI SẢN của bản trước (khi thông báo còn là một ô text cạnh từng trạng thái): không còn được hỏi, không còn được render, chỉ được `NotificationMapBuilder.SeedRows` kéo qua làm giá trị điền sẵn cột To cho các dự án đã chốt bảng đối tượng trước khi bảng thông báo tồn tại.
  - `ReportMap` (JSON `ReportMapRow[]`): **bảng báo cáo / thống kê** — mỗi báo cáo một dòng: `Report` (tên, đọc được như một màn hình), `Question` (báo cáo đó trả lời câu hỏi gì, bằng lời người dùng), `Source` (lấy số từ đối tượng nào — chỉ giữ khi khớp một tên trong `EntityMap` đã chốt, so khớp theo cụm chứa nhau), `Breakdown` (gộp/lọc theo gì), `Included`. Mỗi dòng còn `Included` gieo một mục màn hình vào `Project.PlannedScope` lúc chốt (`ReportMapBuilder.ReportScreens`) — đó là lý do bảng này đứng TRƯỚC bảng phân quyền và **không có cột "ai xem"**: quyền xem của một báo cáo được chốt ở bảng phân quyền như mọi màn hình khác. Không có `Locked`/`Evidence`, cùng lý do với `FlowMap`/`ScreenScopeMap`. Xem [requirement-flow.md](requirement-flow.md#bảng-báo-cáo-mỗi-báo-cáo-là-một-màn-hình).
  - `NotificationMap` (JSON `NotificationMapRow[]`): **bảng cuối cùng** — mỗi sự kiện một dòng (`Entity` + `Event` + `Trigger`), người nhận chính `To` và đồng gửi `Cc` là mảng các mục của **danh sách người nhận** của dự án (`NotificationRecipients` ngay dưới). Cờ `Needed` phân biệt "không gửi" với "có gửi": một dòng đã lưu chỉ có HAI trạng thái đó, vì đường gửi (`ConfirmNotificationMapUseCase`) **không lưu** bảng nào còn dòng `Needed = true` mà `To` rỗng. Xem [requirement-flow.md](requirement-flow.md#bảng-thông-báo-bảng-cuối-cùng).
  - `NotificationRecipients` (JSON `string[]`): **danh sách người nhận** của dự án — nguồn DUY NHẤT của hai ô `To`/`Cc`, và là một bảng người dùng thêm/sửa/xóa ngay trên panel bảng thông báo. `null` = chưa chốt lần nào ⇒ danh sách bày ra là bản gieo tất định (`NotificationMapBuilder.SeedRecipients`: bốn quan hệ với bản ghi + các vai trò của bảng phân quyền đã chốt, nguyên tên). Lưu **cùng lượt** với `NotificationMap` (cùng một `SaveChangesAsync`) vì đường gửi đối chiếu `To`/`Cc` theo đúng nó — xem [requirement-flow.md](requirement-flow.md#bảng-thông-báo-bảng-cuối-cùng).
  - `NotificationMap` theo luật KHẮT KHE MỘT CHIỀU của `PermissionMatrix` (nhóm của nó KHÔNG BAO GIỜ `[RÕ]` khi chưa có bảng, vì nó cũng không còn được hỏi bằng câu hỏi); **bốn** bảng còn lại thì chỉ **xác nhận lại** thứ hội thoại đã trả lời. Áp luật một chiều cho bốn bảng đó là dựng một vòng khóa kín — cổng bày bảng đòi nhóm `[RÕ]`, bản đồ đòi có bảng. Với `ReportMap` điều đó còn là điều kiện MỞ CỔNG: bảng chỉ bày ra khi nhóm «Báo cáo / thống kê» đã `[RÕ]`, vì một bảng báo cáo trống thu về ít hơn cả ô kể tự do nó thay thế.

## Workflow schema

```mermaid
erDiagram
    WorkflowRun {
        Guid Id PK
        Guid ProjectId FK
        string Name
        WorkflowRunStatus Status
        WorkflowStageKey CurrentStage
        DateTime CreatedAt
        DateTime StartedAt
        DateTime FinishedAt
    }

    AgentTask {
        Guid Id PK
        Guid WorkflowRunId FK
        Guid ProjectId FK
        Guid AgentId FK "optional"
        AgentTaskType Type
        AgentTaskStatus Status
        string Title
        string Input
        string RevisionFeedback
        string Output
        string Error
        int Attempt
        DateTime CreatedAt
        DateTime StartedAt
        DateTime FinishedAt
    }

    Project ||--o{ WorkflowRun : WorkflowRuns
    WorkflowRun ||--o{ AgentTask : AgentTasks
    Project ||--o{ AgentTask : AgentTasks
    Agent ||--o{ AgentTask : assigned
```

### Status model

```mermaid
stateDiagram-v2
    [*] --> Queued
    Queued --> Running
    Running --> Completed
    Running --> Failed
    Running --> Queued: startup recovery nếu app restart
```

`WorkflowRunStatus` có thêm `WaitingForHuman` để biểu diễn gate duyệt giữa các stage.

### Index quan trọng

| Entity | Index | Lý do |
|---|---|---|
| `WorkflowRun` | `(ProjectId, Status, CreatedAt)` | Query status theo project |
| `AgentTask` | `(ProjectId, Status, CreatedAt)` | Query task theo project/status |
| `AgentTask` | `(Status, CreatedAt)` | Worker poll task queued cũ nhất mỗi ~2 giây |

## AI config/runtime schema

```mermaid
erDiagram
    AiModel {
        Guid Id PK
        string ModelId
        string Endpoint
        string ApiKey_encrypted
        int ContextWindow
        decimal InputPricePerMillionTokens
        decimal OutputPricePerMillionTokens
        bool IsActive
        bool SupportsVision
        StructuredOutputMode StructuredOutputMode
        string CreatedByUsername
        DateTime CreatedAt
    }

    Agent {
        Guid Id PK
        AgentRoleKey RoleKey UK
        string Description
        string Color
        double Temperature
        Guid AiModelId FK
        string CreatedByUsername
        DateTime CreatedAt
    }

    ToolDefinition {
        Guid Id PK
        string Name
        string DisplayName
        string Description
        string ServiceType
        string MethodName
        bool IsActive
    }

    AgentTool {
        Guid AgentId PK, FK
        Guid ToolDefinitionId PK, FK
    }

    AgentModelCallLog {
        Guid Id PK
        Guid ProjectId FK
        Guid AgentId FK
        Guid WorkflowRunId "nullable, index only"
        string AgentName
        string ModelId
        string RequestJson
        string ResponseText
        string ErrorMessage
        int PromptTokens
        int CompletionTokens
        int TotalTokens
        long DurationMs
        int HttpStatusCode
        bool IsSuccess
        int Step
        string Purpose
        DateTime CreatedAt
    }

    AiModel ||--o{ Agent : powers
    Agent ||--o{ AgentTool : has
    ToolDefinition ||--o{ AgentTool : is_assigned
    Project ||--o{ AgentModelCallLog : logs
    Agent ||--o{ AgentModelCallLog : logs
```

### Ghi chú thiết kế

- `AiModel.ApiKey` được encrypt/decrypt bằng EF value converter. `IApiKeyProtector` phải là singleton vì EF cache model toàn cục.
- `AgentModelCallLog.WorkflowRunId` có index nhưng không khai FK để tránh multiple cascade path; truy vấn join thủ công khi cần.
- `AgentModelCallLog.Step` là **lượt gọi model thứ mấy trong một task agent**, do `ModelCallLoggingChatClient`
  đếm từ 1 theo instance. Đường agent (`AgentRunService`, purpose `AgentRun`) dùng chung một instance cho cả
  task nên thấy 1, 2, 3… — đọc cùng `MaxSteps` của bước pipeline để biết task có tiêu hết ngân sách bước không.
  Mọi lời gọi qua `LlmClient` (các purpose `BA*`, review POC) dựng đường ống mới mỗi lần nên **luôn bằng 1**;
  popup AI Call Logs vì thế chỉ hiện nhãn "Step" khi `Step > 1`.
- `AgentTool` là bảng many-to-many explicit với composite key.
- `ToolDefinition` unique theo `(ServiceType, MethodName)` để đồng bộ discovery không tạo trùng.
- `Agent.RoleKey` là unique: mỗi role đúng một agent — mọi lookup agent trong hệ thống đều theo `RoleKey`.

## Security/RBAC/Audit schema

```mermaid
erDiagram
    AppUser {
        Guid Id PK
        string Username UK
        string DisplayName
        string OrgUnitName
        string UserMemory
        string Email
        bool NotifyInApp
        bool NotifyByEmail
        bool NotifyOnGate
        bool NotifyOnCompleted
        bool NotifyOnFailed
        DateTime CreatedAt
    }

    AppUserRole {
        Guid AppUserId PK_FK
        UserRole Role PK
    }

    AppUser ||--o{ AppUserRole : "giữ"

    RolePermission {
        Guid Id PK
        UserRole Role
        AppPermission Permission
    }

    AuditLog {
        Guid Id PK
        AuditCategory Category
        AuditAction Action
        string EntityId
        string Summary
        string ActorUsername
        string ActorRole
        string BeforeJson
        string AfterJson
        DateTime CreatedAt
    }
```

| Constraint/index | Ý nghĩa |
|---|---|
| `AppUser.Username` unique | Không trùng tài khoản đăng nhập |
| `AppUserRole(AppUserId, Role)` PK | Một user giữ NHIỀU vai trò, mỗi vai trò chỉ gán một lần. Xóa user cascade sang bảng này |
| `AppUserRole.Role` index | Chiều ngược: "ai đang giữ vai trò X" (đăng nhập Local tìm SuperAdmin) |
| `RolePermission(Role, Permission)` unique | Một permission chỉ được cấp một lần cho role |
| `AuditLog.CreatedAt`, `(Category, CreatedAt)` | Lọc/sắp xếp audit log |

## Notifications và Feedback schema

```mermaid
erDiagram
    Notification {
        Guid Id PK
        string RecipientUsername
        NotificationType Type
        string Title
        string Message
        Guid ProjectId
        string ProjectName
        Guid WorkflowRunId
        string Link
        bool IsRead
        DateTime CreatedAt
        DateTime ReadAt
    }

    Feedback {
        Guid Id PK
        FeedbackType Type
        FeedbackStatus Status
        string Title
        string Message
        string CreatedByUsername
        string SubmittedByName
        DateTime CreatedAt
        DateTime UpdatedAt
    }

    FeedbackAttachment {
        Guid Id PK
        Guid FeedbackId FK
        FeedbackAttachmentKind Kind
        string FileName
        string ContentType
        long SizeBytes
        string StoredPath
        DateTime CreatedAt
    }

    Feedback ||--o{ FeedbackAttachment : Attachments
```

`Notification` index `(RecipientUsername, IsRead, CreatedAt)` phục vụ chuông thông báo: đếm unread và lấy danh sách mới nhất.

## Prompt/eval schema

```mermaid
erDiagram
    PromptTemplateVersion {
        Guid Id PK
        string PromptKey
        int VersionNumber
        string Content
        string ChangeNote
        bool IsActive
        string CreatedByUsername
        DateTime CreatedAt
    }

    EvalScenario {
        Guid Id PK
        string Name
        string PromptKey
        string UserInput
        string Criteria
        bool IsActive
        string CreatedByUsername
        DateTime CreatedAt
        DateTime UpdatedAt
    }

    EvalRun {
        Guid Id PK
        string Note
        string PromptKey
        Guid TargetModelId "no fk"
        string TargetModelName
        Guid JudgeModelId "no fk"
        string JudgeModelName
        EvalRunStatus Status
        int ScenarioCount
        int CompletedCount
        double AverageScore
        long TotalTokens
        string Error
        string CreatedByUsername
        DateTime CreatedAt
        DateTime StartedAt
        DateTime FinishedAt
    }

    EvalResult {
        Guid Id PK
        Guid EvalRunId FK
        Guid EvalScenarioId "no fk"
        string ScenarioName
        string Output
        Guid PromptVersionId "no fk"
        int PromptVersionNumber
        int Score
        string JudgeReasoning
        bool IsSuccess
        string ErrorMessage
        int TargetTokens
        int JudgeTokens
        long DurationMs
        DateTime CreatedAt
    }

    EvalRun ||--o{ EvalResult : Results
```

### Index/constraint quan trọng

| Entity | Index | Lý do |
|---|---|---|
| `PromptTemplateVersion` | `(PromptKey, VersionNumber)` unique | Version history không trùng |
| `PromptTemplateVersion` | `(PromptKey, IsActive)` | Lấy bản active nhanh |
| `EvalScenario` | `(IsActive, CreatedAt)` | Lọc scenario active |
| `EvalRun` | `(Status, CreatedAt)` | Worker poll queued + UI list |
| `EvalResult` | `EvalRunId`, `EvalScenarioId` | Chi tiết run và so sánh scenario |

## Organization schema

```mermaid
erDiagram
    OrgUnit {
        Guid Id PK
        string OrgUnitCode
        string DisplayName
        string Description
        string CostCenter
        string DisciplinaryResponsible
        string TargetResponsible
        bool IsDepartment
        bool IsDelete
    }

    Associate {
        Guid Id PK
        string PersonalNumber
        string GlobalId
        string DisplayName
        string OrgUnitCode
        string OrganizationUnit
        string Email
        string Position
        decimal StandardWorkingHour
        bool IsIndirect
        bool IsDelete
    }
```

Hai bảng này được seed từ dữ liệu HR_Portal mẫu. Index chính:

- `OrgUnit.OrgUnitCode`
- `Associate.OrgUnitCode`
- `Associate.GlobalId`

## Cascade/delete behavior

| Relationship | Delete behavior | Lý do |
|---|---|---|
| `Project -> ProjectDocuments` | Cascade theo convention/relationship | Xóa project dọn tài liệu |
| `ProjectDocument -> ProjectDocumentRevisions` | Cascade | Xóa document dọn revision |
| `Project -> ProjectSourceFiles` | Cascade | Xóa project dọn source upload metadata |
| `Project -> WorkflowRuns` | Cascade | Xóa project dọn workflow |
| `WorkflowRun -> AgentTasks` | Cascade | Xóa run dọn task |
| `AgentTask -> Project` | Restrict | Tránh multiple cascade path |
| `AgentTask -> Agent` | SetNull | Xóa agent vẫn giữ task history |
| `AgentModelCallLog -> Project` | Cascade | Xóa project dọn call logs |
| `AgentModelCallLog -> Agent` | Restrict | Xóa agent không wipe audit lịch sử |
| `AgentConversation -> Project` | Cascade | Xóa project dọn chat |
| `AgentConversation -> Agent` | Restrict | Giữ lịch sử theo agent |
| `Agent -> AiModel` | Restrict | Không xóa model đang được agent dùng |
| `Feedback -> FeedbackAttachment` | Cascade | Xóa feedback dọn attachment metadata |
| `EvalRun -> EvalResult` | Cascade | Xóa run dọn result |

## Seed data

Khi DB khởi tạo rỗng, `DbInitializer` seed:

| Data | Nội dung |
|---|---|
| Users | `superadmin`, `admin`, `teamdev`, `user` — mỗi tài khoản seed một dòng `AppUserRoles` tương ứng (không mật khẩu: Local tự đăng nhập bằng SuperAdmin, SSO đồng bộ từ IdentityServer) |
| Role permissions | SuperAdmin implicit-all; Admin mặc định toàn bộ quyền (cấu hình được); TeamDev gần đủ quyền vận hành; User quyền project/requirement/feedback cơ bản |
| Org/Associates | Dữ liệu mẫu HR_Portal |
| Tool definitions | Đồng bộ từ tool discovery |
| AI models | LM Studio local + DeepSeek mẫu |
| Agents | BA, Tech Lead, Developer, Tester, UI/UX |
| Agent tools | Tool mặc định theo role |

> Lưu ý: mật khẩu seed chỉ phù hợp dev/internal, cần đổi ở môi trường thật.
