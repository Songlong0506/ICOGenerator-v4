# Database Design — ICOGenerator v4

## 1. Tổng quan database

Database được quản lý bằng Entity Framework Core qua `AppDbContext`. Provider mặc định là SQL Server; SQLite được hỗ trợ để chạy end-to-end trong môi trường không có SQL Server.

```mermaid
flowchart TB
    DB[(ICOGenerator DB)]
    DB --> Core[Project & Documents]
    DB --> Workflow[Workflow & Agent Tasks]
    DB --> AI[Agents, Models, Tools, LLM Logs]
    DB --> Security[Users, Roles, Audit]
    DB --> Ops[Notifications, Feedback]
    DB --> Eval[Prompt Versions & Evals]
    DB --> Org[OrgUnits & Associates]
```

## 2. Entity groups

| Group | Tables | Mục đích |
|---|---|---|
| Project core | `Projects`, `ProjectDocuments`, `ProjectDocumentRevisions`, `ProjectSourceFiles`, `AgentConversations` | Dữ liệu project, tài liệu, upload, chat BA |
| Workflow | `WorkflowRuns`, `AgentTasks` | Điều phối các run/task nền |
| AI config/runtime | `Agents`, `AiModels`, `ToolDefinitions`, `AgentTools`, `AgentModelCallLogs` | Cấu hình agent/model/tool và log LLM |
| Security | `AppUsers`, `RolePermissions`, `AuditLogs` | Login, RBAC, audit cấu hình |
| Notifications/Feedback | `Notifications`, `Feedbacks`, `FeedbackAttachments` | Thông báo và phản hồi người dùng |
| Prompt/eval | `PromptTemplateVersions`, `EvalScenarios`, `EvalRuns`, `EvalResults` | Prompt override và benchmark prompt/model |
| Organization | `OrgUnits`, `Associates` | Dữ liệu tổ chức seed từ HR_Portal |

## 3. ERD mức cao

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

## 4. Core project schema

```mermaid
erDiagram
    Project {
        Guid Id PK
        string Name
        string Description
        ProjectStatus Status
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

## 5. Workflow schema

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

## 6. AI config/runtime schema

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
        string LearnedChecklistNotes
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
- `AgentTool` là bảng many-to-many explicit với composite key.
- `ToolDefinition` unique theo `(ServiceType, MethodName)` để đồng bộ discovery không tạo trùng.
- `Agent.RoleKey` là unique: mỗi role đúng một agent — mọi lookup agent trong hệ thống đều theo `RoleKey`.

## 7. Security/RBAC/Audit schema

```mermaid
erDiagram
    AppUser {
        Guid Id PK
        string Username UK
        string PasswordHash
        string DisplayName
        UserRole Role
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
| `RolePermission(Role, Permission)` unique | Một permission chỉ được cấp một lần cho role |
| `AuditLog.CreatedAt`, `(Category, CreatedAt)` | Lọc/sắp xếp audit log |

## 8. Notifications và Feedback schema

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

## 9. Prompt/eval schema

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

## 10. Organization schema

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

## 11. Cascade/delete behavior

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

## 12. Seed data

Khi DB khởi tạo rỗng, `DbInitializer` seed:

| Data | Nội dung |
|---|---|
| Users | `admin/Admin@123`, `teamdev/TeamDev@123`, `user/User@123` |
| Role permissions | TeamDev gần đủ quyền vận hành; User quyền project/requirement/feedback cơ bản; Admin implicit-all |
| Org/Associates | Dữ liệu mẫu HR_Portal |
| Tool definitions | Đồng bộ từ tool discovery |
| AI models | LM Studio local + DeepSeek mẫu |
| Agents | BA, Tech Lead, Developer, Tester, UI/UX |
| Agent tools | Tool mặc định theo role |

> Lưu ý: mật khẩu seed chỉ phù hợp dev/internal, cần đổi ở môi trường thật.
