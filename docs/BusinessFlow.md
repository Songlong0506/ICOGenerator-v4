# Business Flow — ICOGenerator v4

## 1. Big picture

ICOGenerator tổ chức công việc theo một chuỗi có kiểm soát:

```mermaid
journey
    title Hành trình từ ý tưởng tới Pull Request
    section Khởi tạo
      Tạo project: 5: User
      Upload source/reference: 4: User
      Chat với BA: 5: User, BA Agent
    section Requirement
      Sinh Product Brief/Requirement: 4: BA Agent
      User review và approve: 5: User
      Sinh AI Design Spec: 4: BA Agent
    section Delivery
      POC Preview: 4: Developer Agent
      Gate duyệt POC: 5: TeamDev
      Technical Docs: 4: BA Agent
      Gate duyệt docs: 5: TeamDev
      Architecture Design: 4: TechLead Agent
      Gate duyệt architecture: 5: TeamDev
      Implementation: 3: Developer Agent
      Code Review: 4: TechLead Agent
      Testing + BugFix: 3: Tester, Developer
      Pull Request: 5: Developer Agent
```

## 2. Flow 1 — Login và phân quyền

```mermaid
sequenceDiagram
    autonumber
    participant U as User
    participant A as AccountController
    participant L as LoginUseCase
    participant DB as AppDbContext
    participant C as Auth Cookie

    U->>A: GET /Account/Login
    A-->>U: Login form
    U->>A: POST username/password
    A->>L: Authenticate
    L->>DB: tìm AppUser active
    L->>L: verify PasswordHash
    L-->>A: claims Username/Role
    A->>C: SignInAsync
    A-->>U: redirect Projects
```

Sau login, mọi request đi qua authorization fallback. Permission chi tiết dựa vào role + `RolePermission`.

## 3. Flow 2 — Tạo project

```mermaid
flowchart TD
    A[User mở Projects] --> B[Create project]
    B --> C[Nhập name, description, repo URLs, org unit]
    C --> D[CreateProjectUseCase]
    D --> E[(Projects)]
    E --> F[Tạo workspace key theo ProjectId + name]
    F --> G[Redirect requirement workspace]
```

Dữ liệu cốt lõi tạo ra:

- `Project`: metadata project, owner, org unit, repo URL, status.
- Workspace local: nơi agent ghi mockup/source/artefact.

## 4. Flow 3 — Requirement discovery với BA

```mermaid
sequenceDiagram
    autonumber
    participant U as User
    participant UI as Requirements UI
    participant UC as ChatWithBAUseCase
    participant BA as BAChatService
    participant LLM as LLM
    participant DB as DB

    U->>UI: nhập câu trả lời / yêu cầu
    UI->>UC: gửi message
    UC->>DB: lưu AgentConversation role=user
    UC->>BA: tạo prompt gồm transcript + memory + org context + sources
    BA->>LLM: gọi BA model
    LLM-->>BA: reply + suggestions/readiness
    BA->>DB: lưu AgentConversation role=assistant
    BA->>DB: cập nhật conversation summary/memory/coverage nếu cần
    UC-->>UI: assistant reply + next questions
```

BA không chỉ trả lời chat; service còn duy trì ngữ cảnh dài hạn:

| Context | Lưu ở đâu | Mục đích |
|---|---|---|
| Conversation transcript | `AgentConversation` | Lịch sử trao đổi chi tiết |
| Conversation summary | `Project.ConversationSummary` | Rút gọn hội thoại dài |
| User memory | `AppUser.UserMemory` | Ghi nhớ preference/đặc thù người dùng |
| Checklist gap notes | `Agent.LearnedChecklistNotes` | Học các điểm BA thường hỏi thiếu |
| Requirement coverage | `Project.RequirementCoverageMap` | Theo dõi coverage requirement |
| Source files | `ProjectSourceFile` | Bối cảnh từ PDF/image user upload |

## 5. Flow 4 — Sinh draft requirement

```mermaid
sequenceDiagram
    autonumber
    participant U as User
    participant R as RequirementsController
    participant UC as GenerateRequirementDraftUseCase
    participant O as WorkflowOrchestrator
    participant DB as DB
    participant W as AgentTaskWorker
    participant BA as ProductBriefDraftService

    U->>R: Click Generate/Update Requirement
    R->>UC: execute(projectId)
    UC->>O: StartRequirementDraftWorkflowAsync
    O->>DB: tạo WorkflowRun Write Requirement
    O->>DB: tạo AgentTask RequirementAnalysis Queued
    W->>DB: poll task
    W->>BA: GenerateOrUpdateDraftAsync
    BA->>DB: tạo/cập nhật ProjectDocument + Revision
    W->>DB: task completed, run completed
```

Kết quả có thể là:

- Đủ thông tin: tạo/cập nhật requirement docs.
- Chưa đủ thông tin: worker trả marker `NeedsMoreInfo`, BA đặt câu hỏi tiếp trong chat.

## 6. Flow 5 — Approve requirement và sinh AI Design Spec

```mermaid
flowchart TD
    A[User review Product Brief/Requirement] --> B{Approve?}
    B -- Không --> C[Chat tiếp / sửa draft]
    B -- Có --> D[ApproveRequirementUseCase]
    D --> E[Mark ProjectDocument approved + version]
    E --> F[StartAiDesignSpecWorkflowAsync]
    F --> G[AgentTask AiDesignSpec Queued]
    G --> H[AgentTaskWorker gọi RequirementDocsService.GenerateAiDesignSpecAsync]
    H --> I[Lưu AI Design Spec]
    I --> J[Tự khởi động Delivery Workflow]
```

Điểm quan trọng: sinh AI Design Spec chạy nền để UI không bị treo trong lúc đợi LLM.

## 7. Flow 6 — Delivery pipeline có gate duyệt

Pipeline delivery được khai báo tập trung trong `DeliveryPipeline`:

```mermaid
flowchart LR
    A[POC Preview] --> G1{Gate}
    G1 --> B[Technical Docs]
    B --> G2{Gate}
    G2 --> C[Architecture Design]
    C --> G3{Gate}
    G3 --> D[Implementation]
    D --> G4{Gate}
    G4 --> E[Code Review]
    E --> G5{Gate}
    G5 --> F[Testing]
    F --> G6{Gate nếu PASS/không FAIL}
    G6 --> H[Pull Request]
    H --> Done[Completed]
```

Mỗi bước tuyến tính có pattern:

```mermaid
stateDiagram-v2
    [*] --> QueuedTask
    QueuedTask --> Running
    Running --> CompletedTask
    CompletedTask --> WaitingForHuman: còn bước kế
    WaitingForHuman --> NextQueuedTask: Approve stage
    WaitingForHuman --> RevisionQueued: Request revision
    WaitingForHuman --> Rejected: Reject stage
    RevisionQueued --> Running
    NextQueuedTask --> Running
```

## 8. Flow 7 — Request revision tại gate

```mermaid
sequenceDiagram
    autonumber
    participant Reviewer as TeamDev/User
    participant UC as RequestStageRevisionUseCase
    participant DB as DB
    participant W as AgentTaskWorker
    participant Agent as Agent

    Reviewer->>UC: nhập feedback cần chỉnh
    UC->>DB: kiểm tra số vòng revision của stage
    UC->>DB: tạo AgentTask cùng Type/Stage, RevisionFeedback != null
    UC->>DB: set WorkflowRun Queued
    W->>DB: poll revision task
    W->>Agent: prompt gồm input + revision feedback + previous output
    Agent-->>W: sản phẩm đã sửa
    W->>DB: completed, quay lại WaitingForHuman
```

Giới hạn mặc định: tối đa 3 vòng revision cho mỗi bước để tránh đốt token vô hạn.

## 9. Flow 8 — Testing và BugFix loop tự động

```mermaid
flowchart TD
    A[Testing task completed] --> B{Parse verdict}
    B -- PASS / Unknown --> C[Đi tiếp gate tuyến tính]
    B -- FAIL --> D{BugFix attempts < 3?}
    D -- Có --> E[Enqueue BugFix cho Developer]
    E --> F[BugFix completed]
    F --> G[Enqueue Testing lại]
    G --> A
    D -- Không --> H[Complete run với báo cáo còn lỗi]
```

Điểm khác với các stage khác: Testing↔BugFix là chu trình tự động, không chờ gate giữa BugFix và retest.

## 10. Flow 9 — Pull Request

```mermaid
sequenceDiagram
    autonumber
    participant W as AgentTaskWorker
    participant Dev as Developer Agent
    participant Tools as GitTools
    participant Git as Git Remote
    participant GH as GitHub API optional
    participant DB as DB

    W->>Dev: task PullRequest
    Dev->>Tools: GitStatus/GitCommit/CreateBranch/PushBranch/OpenPullRequest
    Tools->>Git: commit + push branch
    alt GitHub token configured and remote is github.com
        Tools->>GH: create PR via REST API
        GH-->>Tools: PR URL
    else fallback
        Tools-->>Dev: compare URL
    end
    Dev-->>W: PR/compare link
    W->>DB: task completed, run completed
```

## 11. Flow 10 — Prompt Studio và Eval

```mermaid
flowchart TB
    subgraph PromptStudio[Prompt Studio]
        A[Prompt files in /Prompts] --> B[PromptFileCatalog]
        B --> C[View/Edit prompt]
        C --> D[PromptTemplateVersion]
        D --> E[Activate version]
        E --> F[DbPromptOverrideProvider cache]
    end

    subgraph Eval[Prompt Evals]
        S[EvalScenario] --> R[EvalRun Queued]
        R --> W[EvalRunWorker]
        W --> T[Target model output]
        T --> J[Judge model scoring]
        J --> ER[EvalResult]
    end

    F --> W
```

Prompt file trong repo là source gốc; version active trong DB override prompt tại runtime.

## 12. Notification flow

```mermaid
flowchart LR
    A[Workflow gate/completed/failed] --> B[NotificationService]
    B --> C[(Notification in-app)]
    B -. nếu bật .-> D[Teams channel]
    B -. nếu bật .-> E[Email channel]
    U[User notification preferences] --> B
```

Thông báo xuất hiện ở chuông in-app; Teams/email chỉ hoạt động khi được cấu hình.
