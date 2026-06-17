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

Chạy:
```
dotnet run
```
Khi khởi động, `DbInitializer.InitializeAsync` sẽ tự: chạy migration, đồng bộ danh mục tool (`ToolDiscoveryService`), và **seed sẵn 6 agent** (BA, Tech Lead, Developer, Tester, UI/UX, System) cùng AI model + vài project mẫu. App mặc định mở vào `ProjectsController`.

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
| `AgentRoleKey` | Vai trò của agent: `BusinessAnalyst`, `TechLead`, `Developer`, `Tester`, `UiUx`, `System`. **Đã định nghĩa đủ cho cả team.** | `Domain/Enums/AgentRoleKey.cs` |
| `ProjectDocument` | Tài liệu sinh ra trong dự án (BRD/SRS/FSD, design spec…), có `Folder`, `VersionName`, `IsApproved`. | `Domain/ProjectDocument.cs` |
| `AgentConversation` | Một dòng hội thoại user ↔ agent trong một project. | `Domain/AgentConversation.cs` |
| `AgentJob` | ⚠️ **Hiện không dùng.** Từng dùng cho luồng chat BA bất đồng bộ (đã gỡ — chat giờ chạy đồng bộ qua `ChatWithBAUseCase`). Entity/bảng còn giữ để tránh migration. | `Domain/AgentJob.cs` |
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

1. Cần kiểu dữ liệu mới? → thêm entity ở `Domain/` (rồi `dotnet ef migrations add ...`) hoặc DTO ở `Contracts/`.
2. Viết thao tác như **một class** ở `Application/<Khu vực>/` (`...Query` hoặc `...UseCase`, một `ExecuteAsync`).
3. Logic kỹ thuật tái dùng (gọi LLM, tool, file) → đặt ở `Services/...`.
4. Thêm action **mỏng** ở controller, gọi use case.
5. Đăng ký use case/service trong nhóm `AddXxx()` tương ứng ở `Extensions/ApplicationServiceCollectionExtensions.cs`.
6. View (nếu cần) + test ở `tests/`.

---

## 7. Ví dụ chạy thật: mở rộng thành team BA → Tech Lead → Dev → Tester

Đây là tính năng tiêu biểu nhất của roadmap. Mục này chỉ ra **đang ở đâu, thiếu gì, và sửa ở đâu**.

### 7.1. Hiện trạng

Sau khi requirement được duyệt, `WorkflowOrchestrator.StartDeliveryWorkflowAsync(...)` hiện làm đúng *một* việc:

> tạo một `WorkflowRun` ở stage `Implementation`, kèm **một** `AgentTask` loại `Implementation` giao cho agent `Developer`.

Rồi `AgentTaskWorker` chạy task đó xong là **đánh dấu cả workflow `Completed`** luôn. Tức pipeline hiện tại chỉ có một mắt xích: Dev.

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

---

## 8. Cạm bẫy & quy ước cần biết

- **Chat BA chạy đồng bộ.** Người dùng gửi message → `RequirementsController.Chat` → `ChatWithBAUseCase` → `BARequirementService` (trong cùng request). Luồng job bất đồng bộ cũ (`AgentJob`/`AgentJobRunner`) đã được gỡ; đừng dựng lại trừ khi thực sự nối vào UI. Pipeline nền giao hàng vẫn dùng `WorkflowRun` + `AgentTask`.
- **Rào an toàn của tool.** Tool chạy lệnh/đụng file bị giới hạn bởi `AllowedCommands` và `AllowedFileExtensions` trong `appsettings.json`, và `ToolPolicyService` kiểm tra tham số. Thêm tool mạnh thì cân nhắc kỹ.
- **Thêm tool cho agent = viết một method** trong một class `*Tools` (`Services/Tools/...`); registry + reflection (`ToolDiscoveryService`, `DynamicToolInvoker`) tự sinh schema. Không phải sửa vòng lặp agent. Nhớ gán tool cho vai trong `DbInitializer.AssignDefaultToolsAsync`.
- **namespace = đường dẫn thư mục.** Giữ đúng để nhìn là biết file ở đâu.
- **`Tools/Abstractions` chỉ chứa interface/record**; class hiện thực nằm ở `Tools/Execution`. Đừng để lẫn.
- **Đăng ký DI một chỗ.** Mọi service mới phải vào đúng nhóm `AddXxx()` ở file Extensions, nếu không sẽ lỗi "Unable to resolve service" lúc chạy.
- **Migration.** Đổi `Domain` entity là phải tạo migration (`dotnet ef migrations add <Tên>`); `DbInitializer` tự `MigrateAsync` lúc khởi động.

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
