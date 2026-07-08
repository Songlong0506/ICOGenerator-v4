# Architecture Overview — ICOGenerator v4

## 1. Kiến trúc tổng thể

ICOGenerator v4 dùng kiến trúc nhiều lớp theo kiểu **MVC + Application Use Cases + Domain + Services + EF Core**.

```mermaid
flowchart TB
    subgraph Client[Browser]
        UI[Razor Views]
        JS[wwwroot JS/CSS]
    end

    subgraph Web[ASP.NET Core Web]
        C[Controllers]
        Auth[Cookie Auth + RBAC]
        SSE[Progress/JSON endpoints]
    end

    subgraph App[Application Layer]
        Q[Queries]
        U[Use Cases]
        VM[ViewModels/Results]
    end

    subgraph Domain[Domain Layer]
        E[Entities]
        En[Enums]
    end

    subgraph Services[Service Layer]
        Req[Requirement Services]
        WF[Workflow Services]
        Agent[Agent Runtime]
        LLM[LLM Services]
        Tools[Tool Registry + Tools]
        Artifact[Artifact Storage]
        Noti[Notification Services]
        Sec[Security/Audit]
    end

    subgraph Infra[Infrastructure]
        DB[(SQL Server / SQLite)]
        FS[(Local Workspace / Uploads)]
        Provider[(OpenAI-compatible LLM)]
        Git[(Git/GitHub)]
        OTEL[(OTLP Collector optional)]
    end

    UI --> C
    JS --> C
    C --> Auth
    C --> Q
    C --> U
    Q --> Domain
    U --> Domain
    Q --> Services
    U --> Services
    Services --> DB
    Services --> FS
    Agent --> LLM
    Agent --> Tools
    Tools --> FS
    Tools --> Git
    Web --> OTEL
```

## 2. Layer responsibilities

| Layer | Trách nhiệm | Không nên làm |
|---|---|---|
| `Controllers` | Nhận request, authorize, validate model cơ bản, gọi use case/query, trả view/json/file | Không chứa business logic dài, không gọi LLM trực tiếp |
| `Application/*` | Use case/query theo feature, dựng ViewModel/Result, điều phối DB/service ở mức nghiệp vụ | Không chứa logic hạ tầng phức tạp như tool execution/LLM protocol |
| `Domain/*` | Entity, enum, navigation, trạng thái nghiệp vụ | Không phụ thuộc ASP.NET, controller, service cụ thể |
| `Services/*` | Logic nghiệp vụ/hạ tầng reusable: LLM, workflow, requirement generation, artifacts, notification, security | Không trả Razor view |
| `Data/*` | EF DbContext, mapping, seed, migration bootstrap | Không chứa flow nghiệp vụ UI |
| `Prompts/*` | Prompt template theo domain/agent/workflow | Không hardcode prompt dài trong use case nếu đã có file prompt |

## 3. Request/response path chuẩn

```mermaid
sequenceDiagram
    autonumber
    participant B as Browser
    participant C as Controller
    participant P as PermissionService
    participant A as UseCase/Query
    participant S as Domain Service
    participant DB as AppDbContext

    B->>C: HTTP request
    C->>P: kiểm tra permission attribute/policy
    P->>DB: đọc RolePermission nếu cần
    C->>A: ExecuteAsync(...)
    A->>DB: query/update entity
    A->>S: gọi service nếu logic phức tạp
    S->>DB: đọc/ghi dữ liệu liên quan
    A-->>C: ViewModel/Result
    C-->>B: View/JSON/File
```

## 4. Dependency Injection composition root

`AddIcoGeneratorApplication` là composition root của ứng dụng. Các nhóm service chính:

```mermaid
mindmap
  root((DI Registration))
    Web MVC
      ControllersWithViews
      CSRF auto validation
    Auth
      Cookie auth
      PasswordHasher
      PermissionService
      AuditLogger
    DB
      AppDbContext
      SQL Server
      SQLite fallback
    UseCases
      Projects
      Requirements
      Agents
      Models
      Prompts
      Evals
      Feedback
      Audit
      Notifications
    AI Runtime
      LlmClient
      ChatClientFactory
      ModelCallLogger
      AgentRunService
      PromptTemplateService
    Workflow
      WorkflowOrchestrator
      AgentTaskWorker
      EvalRunWorker
    Tools
      WorkspaceTools
      CommandTools
      GitTools
      ToolRegistry
    Artifacts
      LocalArtifactStorage
      WorkspacePathResolver
    Observability
      Serilog
      OpenTelemetry optional
```

## 5. Background workers

### 5.1 AgentTaskWorker

`AgentTaskWorker` là worker trung tâm cho delivery/requirement workflow:

```mermaid
stateDiagram-v2
    [*] --> PollQueued
    PollQueued --> Idle: không có task
    Idle --> PollQueued: delay 2s
    PollQueued --> Running: lấy task Queued cũ nhất
    Running --> RequirementDraft: task RequirementAnalysis
    Running --> AiDesignSpec: task AiDesignSpec
    Running --> TechnicalDocs: task TechnicalDocs
    Running --> GenericAgent: POC/Architecture/Implementation/Review/Testing/PR
    GenericAgent --> CompletedTask
    RequirementDraft --> CompletedTask
    AiDesignSpec --> CompletedTask
    TechnicalDocs --> CompletedTask
    CompletedTask --> WaitingForHuman: stage tuyến tính còn bước kế
    CompletedTask --> NextAutoTask: Testing FAIL / BugFix xong
    CompletedTask --> RunCompleted: stage cuối hoặc hết vòng
    Running --> Failed: exception / thiếu agent / max steps
    Failed --> PollQueued
    WaitingForHuman --> [*]
    RunCompleted --> [*]
```

### 5.2 EvalRunWorker

Eval worker poll `EvalRun` trạng thái `Queued`, chạy các scenario, gọi target model và judge model, lưu `EvalResult`, cập nhật điểm trung bình/token/duration.

## 6. Agent/tool architecture

```mermaid
flowchart LR
    A[AgentTask] --> B[AgentRunService]
    B --> C[AgentPromptBuilder]
    B --> D[ILlmClient]
    D --> E[IChatClientFactory]
    E --> F[OpenAI-compatible endpoint]
    B --> G[ToolRegistry]
    G --> H[WorkspaceTools]
    G --> I[CommandTools]
    G --> J[GitTools]
    H --> K[Project workspace]
    I --> K
    J --> L[Git remote / GitHub]
    D --> M[ModelCallLogger]
    M --> N[(AgentModelCallLogs)]
```

Tool access không phải global: `AgentTool` nối `Agent` với `ToolDefinition`. Khi app khởi động, `ToolDiscoveryService` đồng bộ method tool vào DB; seed mặc định cấp tool theo vai trò.

## 7. LLM call path

```mermaid
sequenceDiagram
    autonumber
    participant Worker as AgentTaskWorker
    participant Agent as AgentRunService
    participant Llm as LlmClient
    participant Budget as BudgetGuard
    participant Factory as OpenAIChatClientFactory
    participant Provider as LLM Endpoint
    participant Log as ModelCallLogger
    participant DB as DB

    Worker->>Agent: RunAsync(projectId, agentId, prompt, maxSteps)
    Agent->>Llm: chat completion/tool loop
    Llm->>Budget: kiểm tra budget trước/sau call
    Llm->>Factory: tạo IChatClient từ AiModel
    Factory->>Provider: HTTP request
    Provider-->>Factory: response/tool call/text
    Llm->>Log: ghi request/response/token/cost context
    Log->>DB: AgentModelCallLog
    Llm-->>Agent: output hoặc tool request
    Agent-->>Worker: final output
```

## 8. Security architecture

```mermaid
flowchart TB
    Login[LoginUseCase] --> User[(AppUser)]
    User --> Cookie[Cookie Authentication]
    Cookie --> Request[Authenticated Request]
    Request --> Permission[PermissionService]
    Permission --> RolePerm[(RolePermission)]
    Request --> Audit[AuditLogger]
    Audit --> AuditDb[(AuditLog)]
```

Các điểm quan trọng:

- Fallback authorization policy yêu cầu mọi endpoint phải authenticated trừ khi `[AllowAnonymous]`.
- Cookie auth dùng `HttpOnly`, `SameSite=Lax`, HTTPS-only ngoài development.
- CSRF được bật mặc định cho unsafe HTTP verbs bằng `AutoValidateAntiforgeryTokenAttribute`.
- Security headers baseline: `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`.
- `Admin` implicit-all; role khác đọc quyền từ `RolePermission`.

## 9. Storage architecture

| Storage | Nội dung |
|---|---|
| Database | Entity nghiệp vụ, workflow/task, model config, prompt versions, logs, eval results, notifications |
| Local artifact storage | Workspace project, mockup HTML, source generated, feedback/source uploads |
| Prompt files | Prompt gốc versioned trong repo; Prompt Studio có thể override bằng DB |
| Templates | DOCX templates dùng để xuất BRD/SRS/FSD/User Stories |

## 10. Observability

```mermaid
flowchart LR
    App[ASP.NET App] --> Serilog[Serilog request/error logs]
    App --> Logs[(Console/File sinks theo appsettings)]
    App -. Otel Enabled .-> Traces[ASP.NET + HttpClient traces]
    App -. Otel Enabled .-> Metrics[Runtime + HTTP metrics]
    Traces --> OTLP[OTLP exporter]
    Metrics --> OTLP
```

Ngoài log hệ thống, app còn có log nghiệp vụ rất quan trọng:

- `AgentModelCallLog`: request/response/token/duration/error từng LLM call.
- `AuditLog`: thay đổi cấu hình quan trọng.
- `AgentTask`/`WorkflowRun`: trạng thái delivery.
- `EvalRun`/`EvalResult`: chất lượng prompt/model theo scenario.
