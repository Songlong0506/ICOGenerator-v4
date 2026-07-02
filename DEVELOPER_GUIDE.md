# Hướng dẫn cho Developer — ICOGenerator

Đọc tài liệu này để: hiểu app làm gì, chạy được nó, nắm các khái niệm cốt lõi, và **thêm tính năng mới** một cách đúng kiến trúc. Phần cuối là một ví dụ chạy thật: mở rộng từ "BA → Dev" hiện tại thành cả team **BA → Tech Lead → Dev → Tester** phối hợp ra sản phẩm.

> Tài liệu này tập trung vào *cách làm việc với code*. Nếu cần hiểu sâu về tầng và luật phụ thuộc, đọc kèm `ARCHITECTURE.md`.

---

## 1. App này làm gì

ICOGenerator là một hệ thống **multi-agent dùng LLM**. Người dùng chat với agent **BA** để sinh ra tài liệu requirement; sau khi requirement được duyệt, nó được giao cho agent **Dev** để sinh ra một demo (HTML/POC).

Tầm nhìn dài hạn: mở rộng thành một *team agent* (BA, Tech Lead, Dev, Tester, UI/UX) tự phối hợp qua các bước hand-off để build ra cả ứng dụng. Tin tốt cho người mới: **phần lớn "bộ xương" cho việc này đã có sẵn** trong domain (xem mục 4 và mục 7).

---

## 2. Chạy app lần đầu

Yêu cầu: .NET 8 SDK, SQL Server (hoặc đổi provider), và một endpoint LLM tương thích OpenAI (mặc định trỏ tới LM Studio ở `http://127.0.0.1:1234/v1`).

Cấu hình ở `appsettings.json`:
- `ConnectionStrings:DefaultConnection` — đổi `Server=...` sang SQL Server của bạn.
- `AgentWorkspace:RootPath` — thư mục agent dùng làm workspace để đọc/ghi/đặt file sinh ra. **Đổi sang đường dẫn tồn tại trên máy bạn.**
- `AllowedCommands` / `AllowedFileExtensions` — "rào chắn an toàn" giới hạn lệnh shell và loại file mà tool của agent được phép đụng tới. Mở rộng có cân nhắc.
- Đăng nhập (cookie) bảo vệ **toàn bộ** app. App dùng **bảng `AppUser`** với 3 tài khoản seed sẵn (`admin`/`teamdev`/`user`), mỗi tài khoản gắn một `UserRole` (Admin / TeamDev / User). Khi DB còn rỗng, `DbInitializer` seed 3 tài khoản này với **mật khẩu mặc định** (`Admin@123` / `TeamDev@123` / `User@123`) và ghi cảnh báo — **đổi ngay sau lần đăng nhập đầu** trên môi trường thật.

Bí mật cần đặt trước khi chạy (qua biến môi trường hoặc `dotnet user-secrets`, không commit):
```
Encryption__ApiKeyKey            # khóa mã hóa ApiKey trong DB (app fail-fast nếu thiếu)
```

Chạy:
```
dotnet run
```
Mở app sẽ vào trang `/Account/Login`; đăng nhập bằng một trong các tài khoản seed rồi mới dùng được các màn hình. Mỗi role chỉ thấy/được thao tác trên các màn hình theo cấu hình quyền (xem §Phân quyền).
Khi khởi động, `DbInitializer.InitializeAsync` sẽ tự: chạy migration, đồng bộ danh mục tool (`ToolDiscoveryService`), và **seed sẵn 5 agent** (BA, Tech Lead, Developer, Tester, UI/UX) cùng AI model + vài project mẫu. App mặc định mở vào `ProjectsController`.

---

## 3. Bản đồ kiến trúc trong 60 giây

App theo **Layered Architecture** + pattern **một use case một class**. Phụ thuộc chỉ đi một chiều, từ trên xuống:

```
Controllers  ─►  Application  ─►  Services  ─►  Data  ─►  Domain
(mỏng)           (use case)       (LLM/tool)    (EF)      (entity)
```

Quy tắc cần nhớ khi code:
- **Controller mỏng** — chỉ gọi một use case rồi trả View/JSON. Không truy vấn DB, không gọi LLM trực tiếp.
- **Application = điều phối** — mỗi thao tác là một class với một `ExecuteAsync`. Đặt tên `...Query` (đọc) / `...UseCase` (ghi).
- **Services = việc kỹ thuật tái dùng** — gọi LLM, chạy tool, sinh file. *Không bao giờ* `using` ngược lên Application/Controllers.
- **Domain** không phụ thuộc tầng nào. Đăng ký DI chỉ nằm ở `Extensions/ApplicationServiceCollectionExtensions.cs`.

Chi tiết đầy đủ: `ARCHITECTURE.md`.

---

## 4. Từ vựng cốt lõi (đọc kỹ phần này)

Đây là các khái niệm domain mà mọi tính năng đều xoay quanh. Hiểu được chúng là hiểu được app.

| Khái niệm | Ý nghĩa | File |
|---|---|---|
| `Project` | Một dự án phần mềm đang được team agent xây. Là gốc nối tới tài liệu, hội thoại, workflow. | `Domain/Project.cs` |
| `Agent` | Một "nhân sự AI". Mang `RoleKey` (vai trò), model, và tập tool được phép dùng. Instruction (system prompt) được nạp từ file `Prompts/Agents/Instructions/{RoleKey}.md` theo `RoleKey`, không còn lưu trong DB. | `Domain/Agent.cs`, `Services/Agents/AgentInstructionProvider.cs` |
| `AgentRoleKey` | Vai trò của agent: `BusinessAnalyst`, `TechLead`, `Developer`, `Tester`, `UiUx`. **Đã định nghĩa đủ cho cả team.** | `Domain/Enums/AgentRoleKey.cs` |
| `ProjectDocument` | Tài liệu sinh ra trong dự án (BRD/SRS/FSD, design spec…), có `Folder`, `VersionName`, `IsApproved`. | `Domain/ProjectDocument.cs` |
| `AgentConversation` | Một dòng hội thoại user ↔ agent trong một project. | `Domain/AgentConversation.cs` |
| `WorkflowRun` | Một lần chạy *quy trình giao hàng* cho project, có `CurrentStage` và tập `AgentTask`. Đây là "vé" theo dõi cả pipeline. | `Domain/WorkflowRun.cs` |
| `WorkflowStageKey` | Giai đoạn hiện tại của workflow. **Hiện chỉ có** `RequirementApproved`, `Implementation`, `Completed`, `Failed`. Đây là chỗ cần mở rộng cho team. | `Domain/Enums/WorkflowStageKey.cs` |
| `AgentTask` | Một đầu việc giao cho một agent trong một `WorkflowRun`: có `Type`, `Status`, `Input`, `Output`, `Attempt`. | `Domain/AgentTask.cs` |
| `AgentTaskType` | Loại việc: `RequirementAnalysis`, `ArchitectureDesign`, `Implementation`, `CodeReview`, `Testing`, `BugFix`… **Đã có đủ loại cho cả team.** | `Domain/Enums/AgentTaskType.cs` |
| `ToolDefinition` + `AgentTool` | Danh mục tool (đọc/ghi file, chạy lệnh, git…) và bảng nối agent ↔ tool được phép dùng. | `Domain/ToolDefinition.cs` |

Điểm mấu chốt: **`AgentRoleKey` và `AgentTaskType` đã liệt kê sẵn cả team**, và `DbInitializer` đã seed đủ 6 agent kèm tool phù hợp từng vai. Cái còn thiếu để "cả team làm việc với nhau" *không phải* dữ liệu, mà là **logic điều phối nhiều bước** (mục 7).

---

## 5. Hai luồng chạy của hệ thống

App có hai "động cơ" chạy song song. Phân biệt được chúng là tránh được rất nhiều nhầm lẫn.

### 5a. Luồng tương tác (chat với BA) — đồng bộ theo request
```
RequirementsController          (Controllers)   nhận message từ user
  └► *UseCase / *Query          (Application)    điều phối
       └► BARequirementService  (Services/Requirements)
            ├► RequirementPromptBuilder  → dựng prompt
            ├► ILlmClient                → gọi LLM (Services/Llm)
            ├► RequirementResponseParser → parse kết quả
            └► RequirementDocumentGenerator → sinh file (Templates)
       └► AppDbContext           (Data)           lưu hội thoại / document
```

### 5b. Luồng nền (agent tự chạy việc) — bất đồng bộ, qua hàng đợi
```
IWorkflowOrchestrator.StartDeliveryWorkflowAsync(...)   (Services/Workflows)
   tạo 1 WorkflowRun + enqueue AgentTask (Status = Queued)

AgentTaskWorker  (BackgroundService, chạy nền mỗi ~2s)
   lấy AgentTask Queued cũ nhất
     └► AgentRunService.RunAsync(projectId, agentId, prompt)   (Services/Agents)
          vòng lặp agent: LLM ⇄ tool (đọc/ghi file, chạy lệnh…) cho tới khi xong
     └► cập nhật Task.Output + chuyển trạng thái WorkflowRun
```

`AgentRunService.RunAsync(projectId, agentId, message)` là "tim" của một lượt agent: nó để agent suy nghĩ và *gọi tool* lặp đi lặp lại. Khi thêm vai mới, bạn **không cần đụng vào vòng lặp này** — chỉ cần tạo task trỏ đúng agent.

---

## 6. Công thức thêm một tính năng (tổng quát)

Các bước chi tiết (Domain/Contracts → Application → Services → Controller → DI → View/Test) nằm ở
**[`ARCHITECTURE.md` §6](ARCHITECTURE.md#6-công-thức-thêm-một-tính-năng-mới)** — để một bản duy nhất,
tránh hai tài liệu trôi lệch nhau. Lưu ý riêng cho dev: nếu thêm/đổi entity ở `Domain/` thì nhớ tạo
migration (`dotnet ef migrations add <Tên>`) rồi để `DbInitializer` tự áp khi khởi động.

---

## 7. Pipeline cả team BA → Tech Lead → Dev → Tester (đã triển khai)

Tính năng tiêu biểu nhất của roadmap **đã được hiện thực** đúng theo blueprint mô tả ở các mục con dưới đây. Phần này giữ lại để giải thích *vì sao* thiết kế như vậy; nếu chỉ cần biết "code nằm đâu", xem mục 7.6.

### 7.1. Hiện trạng (trước khi triển khai — để đối chiếu)

Trước đây, sau khi requirement được duyệt, `WorkflowOrchestrator.StartDeliveryWorkflowAsync(...)` chỉ làm đúng *một* việc:

> tạo một `WorkflowRun` ở stage `Implementation`, kèm **một** `AgentTask` loại `Implementation` giao cho agent `Developer`.

Rồi `AgentTaskWorker` chạy task đó xong là đánh dấu cả workflow `Completed` luôn — pipeline chỉ có một mắt xích: Dev. Giờ đây pipeline đã là một *chuỗi* Tech Lead → Dev → Tester với hand-off giữa các bước (xem 7.6).

### 7.2. Cái thực sự còn thiếu: "hand-off" giữa các bước

Để cả team làm việc với nhau, ta cần một *chuỗi* bước, và sau mỗi bước có **hand-off**: output của vai trước trở thành input của vai sau, và workflow tiến sang stage kế tiếp thay vì kết thúc.

Pipeline mục tiêu:
```
RequirementApproved
   → ArchitectureDesign (Tech Lead)   : từ design spec → đề xuất kiến trúc
   → Implementation     (Developer)   : từ kiến trúc → sinh code
   → Testing            (Tester)      : từ code → test cases + báo lỗi
   → Completed
```

### 7.3. Nguyên tắc thiết kế: để pipeline là *dữ liệu khai báo*, đừng rải if/else

Cạm bẫy lớn nhất là nhét "ai làm sau ai" thành các `if (stage == X) ...` rải khắp `AgentTaskWorker`. Làm vậy thì mỗi lần thêm/đổi vai phải sửa logic ở nhiều chỗ. Thay vào đó, **mô hình hoá pipeline thành một danh sách stage khai báo** (đúng tinh thần "behavior-first" — quy trình là công dân hạng nhất của domain). Khi đó worker giữ nguyên, generic; thêm Tester chỉ là thêm một dòng vào danh sách.

### 7.4. Các bước cụ thể

**Bước 1 — Mở rộng giai đoạn.** Thêm stage vào `Domain/Enums/WorkflowStageKey.cs`:
```csharp
public enum WorkflowStageKey
{
    RequirementApproved = 1,
    ArchitectureDesign  = 5,   // mới — Tech Lead
    Implementation      = 2,
    Testing             = 6,   // mới — Tester
    Completed           = 3,
    Failed              = 4
}
```
(`AgentTaskType` đã có sẵn `ArchitectureDesign`, `CodeReview`, `Testing` — không cần thêm.)

**Bước 2 — Khai báo pipeline ở một chỗ duy nhất.** Tạo `Services/Workflows/DeliveryPipeline.cs` — một định nghĩa thuần, dễ đọc, dễ sửa:
```csharp
namespace ICOGenerator.Services.Workflows;

public record PipelineStep(
    WorkflowStageKey Stage,
    AgentRoleKey Role,
    AgentTaskType TaskType,
    string Title);

public static class DeliveryPipeline
{
    // Thứ tự = thứ tự hand-off. Thêm Tester = thêm 1 dòng.
    public static readonly IReadOnlyList<PipelineStep> Steps = new[]
    {
        new PipelineStep(WorkflowStageKey.ArchitectureDesign, AgentRoleKey.TechLead,   AgentTaskType.ArchitectureDesign, "Đề xuất kiến trúc từ design spec"),
        new PipelineStep(WorkflowStageKey.Implementation,     AgentRoleKey.Developer,  AgentTaskType.Implementation,     "Sinh code từ kiến trúc đã duyệt"),
        new PipelineStep(WorkflowStageKey.Testing,            AgentRoleKey.Tester,     AgentTaskType.Testing,            "Viết & chạy test, báo lỗi"),
    };

    public static PipelineStep? Next(WorkflowStageKey current)
    {
        var idx = -1;
        for (var i = 0; i < Steps.Count; i++)
            if (Steps[i].Stage == current) { idx = i; break; }
        return idx >= 0 && idx + 1 < Steps.Count ? Steps[idx + 1] : null;
    }
}
```

**Bước 3 — Orchestrator tạo task cho bước *đầu tiên*** thay vì cứng nhắc Dev. Sửa `WorkflowOrchestrator` để: tạo `WorkflowRun` ở stage của `Steps[0]`, rồi enqueue task đầu tiên (tra agent theo `Role` qua `RoleKey`, giống cách nó đang tra Developer). Phần tra agent theo `RoleKey` đã có mẫu sẵn trong file.

**Bước 4 — Worker làm hand-off thay vì kết thúc sớm.** Đây là thay đổi cốt lõi. Trong `AgentTaskWorker`, ở nhánh task chạy *thành công*, thay vì luôn set `Completed`, hãy hỏi pipeline "bước kế là gì":
```csharp
task.Status = AgentTaskStatus.Completed;
task.Output = output;
task.FinishedAt = DateTime.UtcNow;

var next = DeliveryPipeline.Next(task.WorkflowRun.CurrentStage);
if (next is null)
{
    task.WorkflowRun.Status = WorkflowRunStatus.Completed;
    task.WorkflowRun.CurrentStage = WorkflowStageKey.Completed;
    task.WorkflowRun.FinishedAt = DateTime.UtcNow;
}
else
{
    // HAND-OFF: output của vai này thành input của vai kế
    var nextAgent = await db.Agents.FirstOrDefaultAsync(a => a.RoleKey == next.Role, cancellationToken);
    task.WorkflowRun.CurrentStage = next.Stage;
    db.AgentTasks.Add(new AgentTask
    {
        WorkflowRunId = task.WorkflowRunId,
        ProjectId     = task.ProjectId,
        AgentId       = nextAgent?.Id,
        Type          = next.TaskType,
        Status        = AgentTaskStatus.Queued,
        Title         = next.Title,
        Input         = output            // ← chính là hand-off
    });
}
await db.SaveChangesAsync(cancellationToken);
```
Worker vẫn generic: nó không biết gì về Tech Lead hay Tester, chỉ biết "chạy task → hỏi bước kế → enqueue hoặc kết thúc". Vòng lặp `AgentRunService.RunAsync` không phải đụng tới.

**Bước 5 — Prompt theo vai.** Prompt hiện đang hard-code trong worker ("User đã approve requirement…"). Khi mỗi vai cần chỉ dẫn khác nhau, chuyển phần dựng prompt ra template theo `AgentTaskType` (đặt ở `Prompts/` + nạp qua `PromptTemplateService`, giống cách BA đang làm), để worker chỉ truyền dữ liệu.

**Bước 6 — Điểm dừng cho con người (tùy chọn nhưng nên có).** `WorkflowRunStatus` đã có `WaitingForHuman` và `AgentTaskStatus` có `NeedsReview`. Nếu muốn người dùng duyệt giữa các bước (vd duyệt kiến trúc trước khi cho Dev code), cho hand-off dừng ở `WaitingForHuman` và thêm một use case `ApproveStageUseCase` (tầng Application) để enqueue bước kế khi người dùng bấm duyệt.

### 7.5. Vì sao cách này "đi đường dài"

Thêm một vai mới (vd chèn UI/UX trước Dev) chỉ là **thêm một dòng** vào `DeliveryPipeline.Steps` — không sửa worker, không sửa orchestrator, không `if/else` mới. Đó là dấu hiệu của một seam mở rộng tốt: thay đổi tập trung một chỗ, phần còn lại bất biến.

### 7.6. Bản triển khai thực tế — pipeline có CỔNG DUYỆT

Pipeline đã hiện thực **có cổng duyệt giữa mọi bước** (không chạy thẳng một mạch) để tiết kiệm token: xem trước rẻ (POC) → chốt từng cổng → mới đầu tư bước đắt (full code). Quy trình thực tế:

```
Approve requirement
  → AI Design Spec   (BA, NỀN — run "Requirement Progress" riêng; xong tự khởi động delivery)
  → POC preview      (Dev,      từ AI Design Spec) ──┐ WaitingForHuman
  → TechnicalDocs    (BA, BRD/SRS/FSD/UserStories)    ┤ mỗi bước xong DỪNG chờ
  → ArchitectureDesign (Tech Lead, từ AI Design Spec) ┤ user bấm Duyệt mới sang bước kế;
  → Implementation   (Dev,  code đa file, từ kiến trúc)┤ bấm "Sửa requirement" → hủy run
  → CodeReview       (Tech Lead, soát code Dev)       ┤
  → Testing          (Tester, từ output review)      ──┘
  → Completed
```

Bản đồ file:

| Thành phần | File | Vai trò |
|---|---|---|
| Giai đoạn/loại việc mới | `Domain/Enums/WorkflowStageKey.cs`, `AgentTaskType.cs` | thêm `PocPreview` (và trước đó `ArchitectureDesign`, `Testing`). Enum lưu int → **không cần migration**. |
| Khai báo pipeline | `Services/Workflows/DeliveryPipeline.cs` | `Steps` (POC → TechnicalDocs → Architecture → Impl → CodeReview → Test → PR), mỗi bước khai báo `InputSource` (DesignSpec/PreviousOutput) + `MaxSteps`. Thêm vai = thêm một dòng. |
| Prompt theo bước | `Prompts/Workflow/{poc-preview,architecture-design,implementation,code-review,testing}.v1.md` | `{{input}}` = nội dung theo `InputSource`. `implementation` = sinh **code đa file** trong `04_Implementation/src/`; `code-review` = Tech Lead soát code đó, ghi `04_Implementation/code-review.md`. |
| Dựng prompt | `Services/Workflows/WorkflowTaskPromptBuilder.cs` | map `AgentTaskType` → template. |
| Khởi tạo | `Services/Workflows/WorkflowOrchestrator.cs` | `StartAiDesignSpecWorkflowAsync` (run NỀN sinh AI Design Spec sau Approve — một bước BA, tránh treo màn hình), rồi `StartDeliveryWorkflowAsync` tạo `WorkflowRun` + task ở `DeliveryPipeline.First` (POC). |
| Chạy + cổng | `Services/Workflows/AgentTaskWorker.cs` | chạy task xong: nếu còn bước kế → set `WaitingForHuman` (KHÔNG tự enqueue); hết bước → `Completed`. Dùng `MaxSteps` theo bước. |
| Cổng duyệt | `Application/Requirements/ApproveStageUseCase.cs`, `RejectStageUseCase.cs` | Approve → resolve input theo `InputSource` + enqueue bước kế; Reject → `Canceled`. **Ngoại lệ:** ở cổng **POC** (`WorkflowStageKey.PocPreview`) Reject bị chặn (`RejectStageResult.PocGateNotRejectable`) — POC sai = đổi requirement, là việc của user, không phải TeamDev. Nút "Từ chối" cũng bị ẩn ở client cho bước này. |
| Controller/UI | `AgentDashboardController` (`ApproveStage`/`RejectStage`/`RetryWorkflow`), `GetWorkflowStatusQuery`, `Views/AgentDashboard/Index.cshtml` + `wwwroot/js/agent-dashboard.js` | Cổng "Duyệt & tiếp tục"/"Từ chối"/"Thử lại" sống trên **Agent Dashboard** và yêu cầu quyền `DeliveryAdvance`. Màn hình `Requirements` chỉ hiển thị tiến độ + banner bàn giao. |

> **Phân tách vai trò (User vs TeamDev).** Flow của *user thường* dừng ở bước **POC** (`poc-demo.html`) — họ không có quyền `DeliveryAdvance` nên không thấy cổng duyệt; banner ở `Requirements` chỉ báo "POC đã sẵn sàng, đội Dev sẽ tiếp nhận". Các bước sau (Architecture → code → review → test → PR) do *TeamDev/Admin* đẩy từ Agent Dashboard. Quyền `DeliveryAdvance` thuộc nhóm màn hình **Agents** (xem `PermissionCatalog`), seed mặc định cho TeamDev và backfill idempotent trong `DbInitializer` cho install cũ.

Lưu ý thiết kế:
- **Hành vi sâu theo vai** đến từ *system-prompt* của agent (`Prompts/Agents/Instructions/{RoleKey}.md`); template `Prompts/Workflow/` chỉ mô tả *việc của bước*.
- **POC template** (`poc-template.html`) chỉ copy vào workspace ở bước `PocPreview`. File có HAI vùng marker do `Services/Artifacts/PocTemplate.cs` quản: `POC_CONTENT` (HTML tính năng — `SetPocContent`/`AppendPocContent`) và `POC_SCRIPT` (JS nghiệp vụ — `SetPocScript`/`AppendPocScript`; shell expose `window.pocToast`/`window.pocNavigate` cho script này). Bước POC yêu cầu hiện thực Business Rules của spec thành hành vi thật (tính toán, validate, chuyển trạng thái, mô phỏng vai) chứ không chỉ màn hình tĩnh; agent tự soát bằng tool `AuditPocContent` (`Services/Artifacts/PocAudit.cs` — bắt menu thiếu section, id trùng/đụng id shell, trigger modal trỏ id không tồn tại, CRUD thiếu form/lệch field, script rỗng) và sửa hết ISSUES trước khi trả final.
- **Worker generic**: chỉ "chạy task → còn bước kế thì chờ duyệt, hết thì xong". Việc enqueue bước kế nằm ở `ApproveStageUseCase`.
- **Vòng lặp về requirement**: Reject = hủy run; user bổ sung với BA → "Write Requirement" → "Approve" tạo run mới (phiên bản kế).
- **Luồng requirement-draft** (`RequirementAnalysis`) vẫn là workflow một-bước riêng, không qua pipeline này.
- **Tài liệu cho user vs cho dev (tách vai)**: "Write Requirement" ở trang `Requirements` **chỉ sinh Product Brief** (`ProductBrief.docx`, ngôn ngữ đời thường cho user) ở dạng draft — để user có thể chỉnh đi chỉnh lại mà không đốt token sinh lại bản kỹ thuật. **AI Design Spec** (`AIDesignSpec.docx`, bản kỹ thuật để dựng POC) chỉ được sinh **khi user bấm Approve**: `ApproveRequirementUseCase` promote Product Brief lên `V{n}` rồi gọi đồng bộ `BARequirementService.GenerateAiDesignSpecAsync` (lời gọi LLM từ Product Brief đã duyệt), ghi thẳng vào `02_Design/V{n}` (đã duyệt), rồi mới `StartDeliveryWorkflowAsync` dựng POC. Bộ tài liệu kỹ thuật nặng **BRD/SRS/FSD/UserStories** KHÔNG sinh ở đây — chúng là **bước 2 của Delivery Pipeline** (`AgentTaskType.TechnicalDocs`, sau POC): sau khi duyệt POC thì BA sinh chúng từ Product Brief + AI Design Spec đã duyệt rồi dừng ở cổng duyệt như mọi bước. Khác với các bước khác (agent + prompt chung), bước này chạy qua `BARequirementService.GenerateTechnicalDocsAsync` (worker xử lý nhánh riêng, không dùng `MaxSteps`). Prompt: `Prompts/BA/product-brief.v1.md`, `Prompts/BA/ai-design-spec.v1.md` và `Prompts/BA/technical-docs.v1.md`.

### 7.7. Vòng tự sửa lỗi (Testing ↔ BugFix)

Bước Testing không còn là ngõ cụt "báo lỗi rồi thôi". Tester bắt buộc chốt một dòng máy-đọc-được ở cuối báo cáo — `VERDICT: PASS` hoặc `VERDICT: FAIL` — và worker dựa vào đó để **tự sửa lỗi** mà KHÔNG cần cổng duyệt:

```
Testing ──FAIL──► BugFix (Developer sửa code) ──► Testing (kiểm thử lại) ──► …
   │                                                                          ▲
   └──PASS──► Completed        (lặp tối đa MaxBugFixAttempts lần rồi dừng) ────┘
```

Khác với chuỗi tuyến tính (POC → Architecture → Impl → CodeReview → Test, có cổng duyệt giữa mỗi bước), đây là một **chu trình**: `DeliveryPipeline.Next()` cố tình KHÔNG trả về `BugFix`, và worker xử lý nó riêng trong `TryAdvanceTestFixCycleAsync` (set run về `Queued` để tự chạy tiếp). Số lần sửa được đếm bằng số task `BugFix` trong run (không cần cột mới); hết `MaxBugFixAttempts` thì kết thúc run và báo còn lỗi để người xem lại.

| Thành phần | File | Vai trò |
|---|---|---|
| Stage mới | `Domain/Enums/WorkflowStageKey.cs` | thêm `BugFix` (enum lưu int → **không cần migration**). |
| Verdict | `Services/Workflows/TestVerdictParser.cs` | đọc dòng `VERDICT: PASS/FAIL` (khoan dung hoa/thường, `**bold**`, `:`/`=`); không rõ → coi như PASS (giữ hành vi cũ). |
| Khai báo chu trình | `Services/Workflows/DeliveryPipeline.cs` | `BugFixStep` (ngoài `Steps`), `TestingStep`, `MaxBugFixAttempts`. |
| Prompt | `Prompts/Workflow/{testing,bugfix}.v1.md` | testing yêu cầu chốt verdict; bugfix giao Developer sửa đúng chỗ theo báo cáo. |
| Điều phối chu trình | `Services/Workflows/AgentTaskWorker.cs` | `TryAdvanceTestFixCycleAsync` (FAIL→BugFix; BugFix xong→Testing lại); ngoài chu trình → `AdvanceLinearPipeline`. |

---

## 8. Cạm bẫy & quy ước cần biết

- **Chat BA chạy đồng bộ.** Người dùng gửi message → `RequirementsController.Chat` → `ChatWithBAUseCase` → `BARequirementService` (trong cùng request). Luồng job bất đồng bộ cũ (`AgentJob`/`AgentJobRunner`) đã được gỡ; đừng dựng lại trừ khi thực sự nối vào UI. Pipeline nền giao hàng vẫn dùng `WorkflowRun` + `AgentTask`.
- **Rào an toàn của tool.** Tool chạy lệnh/đụng file bị giới hạn bởi `AllowedCommands` và `AllowedFileExtensions` trong `appsettings.json`, và `ToolPolicyService` kiểm tra tham số. Thêm tool mạnh thì cân nhắc kỹ.
- **Thêm tool cho agent = viết một method** trong một class `*Tools` (`Services/Tools/...`); registry (`ToolDiscoveryService`) + `AIFunctionFactory` tự sinh schema và lo bind/invoke. Không phải sửa vòng lặp agent. Nhớ gán tool cho vai trong `DbInitializer.AssignDefaultToolsAsync`.
- **namespace = đường dẫn thư mục.** Giữ đúng để nhìn là biết file ở đâu.
- **`Tools/Abstractions` chỉ chứa interface/record**; class hiện thực nằm ở `Tools/Execution`. Đừng để lẫn.
- **Đăng ký DI một chỗ.** Mọi service mới phải vào đúng nhóm `AddXxx()` ở file Extensions, nếu không sẽ lỗi "Unable to resolve service" lúc chạy.
- **Migration.** Đổi `Domain` entity là phải tạo migration (`dotnet ef migrations add <Tên>`); `DbInitializer` tự `MigrateAsync` lúc khởi động.

---

## 8.1. Phân quyền (Role & Permission)

- **3 role người dùng** (`Domain/Enums/UserRole.cs`): `Admin`, `TeamDev`, `User`. Khác hẳn `AgentRoleKey` (vai của AI agent). Người dùng nằm ở bảng `AppUser`, seed sẵn trong `DbInitializer` (chưa có UI tạo user).
- **Quyền ở mức hành động** (`Domain/Enums/AppPermission.cs`), ví dụ `ProjectsView`, `ModelsDelete`, `SettingsManage`. `PermissionCatalog` (`Domain/Security`) gom quyền theo màn hình để render ma trận và lọc menu.
- **Cấp quyền** lưu ở bảng `RolePermission` (cấu hình được). **Admin luôn có toàn quyền** (implicit-all trong `PermissionService`) nên không có dòng nào trong bảng và không tự khóa được. Mặc định: TeamDev = mọi thứ trừ Settings/Roles; User = chỉ xem Projects/Requirements.
- **Kiểm tra quyền — một nguồn sự thật:** `IPermissionService` (`Services/Security`, có cache MemoryCache). Dùng bởi:
  - Filter `[RequirePermission(AppPermission.X)]` đặt trên controller (mức xem) hoặc action (mức thao tác). Thiếu quyền ⇒ về `/Account/AccessDenied`.
  - `_Layout.cshtml` (qua `@inject IPermissionService`) để ẩn/hiện menu sidebar.
- **Cấu hình runtime:** màn hình **Roles & Permissions** (`RolesController`, chỉ Admin) tick ma trận và lưu; `UpdateRolePermissionsUseCase` gọi `InvalidateCache()` nên đổi quyền có hiệu lực **ngay, không cần đăng nhập lại**.
- **Thêm màn hình/quyền mới:** thêm giá trị vào `AppPermission`, khai báo trong `PermissionCatalog.Screens`, gắn `[RequirePermission]` lên controller/action, và (nếu là menu) thêm nhánh `@if` trong `_Layout.cshtml`.

---

## 9. Bản đồ file nhanh

- `Program.cs` — điểm vào, pipeline middleware, gọi seed.
- `Extensions/ApplicationServiceCollectionExtensions.cs` — **nơi duy nhất** đăng ký DI (mỗi nhóm `AddXxx` = một thư mục).
- `Data/DbInitializer.cs` — migrate + seed agent/model/tool/project mẫu.
- `Application/` — use case theo khu vực (Projects, Requirements, Agents, Models).
- `Services/Agents/` — vòng lặp agent (`AgentRunService`) dùng bởi worker pipeline.
- `Services/Workflows/` — orchestrator pipeline + `AgentTaskWorker` (nơi làm hand-off ở mục 7).
- `Services/Requirements/` — biến hội thoại BA thành tài liệu.
- `Services/Tools/` — `Abstractions` (hợp đồng), `Execution` (hiện thực), `Registry` (khám phá/gọi), và các nhóm tool nghiệp vụ.
- `Services/Llm/`, `Services/Prompts/`, `Services/Artifacts/` — gọi LLM, nạp prompt template, lưu file sản phẩm.
- `Prompts/` — template prompt dạng `.md`.
- `ARCHITECTURE.md` — kiến trúc & luật phụ thuộc đầy đủ.

Bắt đầu từ đâu khi nhận task mới? Mở `ARCHITECTURE.md` để nắm tầng, rồi quay lại mục 6 (công thức) và mục 7 (ví dụ) của tài liệu này.
