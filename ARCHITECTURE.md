# Kiến trúc ICOGenerator

Tài liệu này mô tả cấu trúc thư mục, các pattern được dùng và quy ước để mở rộng dự án.
Mục tiêu: bất kỳ ai (kể cả "tương lai của chính bạn") nhìn vào cũng biết **một file nên nằm ở đâu** và **tại sao**.

---

## 1. Tổng quan

- **Loại ứng dụng:** ASP.NET Core MVC (.NET 8), EF Core (SqlServer).
- **Bài toán:** một hệ thống AI agent — nhận yêu cầu (requirement) từ người dùng, để các "agent"
  dùng LLM + công cụ (tool) tạo ra tài liệu/đặc tả và chạy các workflow nền.
- **Kiểu kiến trúc:** **Layered Architecture (kiến trúc phân lớp) thực dụng**, kết hợp pattern
  **Use Case / Command–Query mỗi thao tác một class** ở tầng Application.

> Đây *không* phải Clean Architecture "sách giáo khoa" (tầng Application ở đây phụ thuộc trực tiếp
> vào `Data`/EF Core và các service cụ thể, thay vì chỉ phụ thuộc abstraction). Cách làm hiện tại
> đơn giản và đủ dùng cho quy mô này — tài liệu mô tả đúng cái đang có, không tô vẽ.

---

## 2. Sơ đồ thư mục

```
Domain/            # Trái tim: entity nghiệp vụ + enum. KHÔNG phụ thuộc layer nào khác.
  Enums/
Contracts/         # DTO "hợp đồng" dữ liệu (vd: BrdDto, FsdDto...). Thuần POCO, không logic.
  Requirements/
Data/              # EF Core: AppDbContext + DbInitializer (seed).
Application/       # Tầng điều phối use case. Mỗi file = 1 thao tác người dùng.
  Agents/          #   - Query (đọc)  : GetXxxQuery
  Models/          #   - UseCase (ghi): XxxUseCase
  Projects/        #   - ViewModel    : XxxVm
  Requirements/
Services/          # Hạ tầng & service nghiệp vụ tái dùng (gọi LLM, tool, file, prompt...).
  Agents/          #   Vòng lặp agent tự động dùng tool + background runner
  Artifacts/       #   Lưu/đọc file sản phẩm trong workspace
  Llm/             #   Client gọi LLM + model request/response
  Logging/         #   Ghi log lời gọi model
  Prompts/         #   Nạp & render template prompt (file .md trong /Prompts)
  Requirements/    #   Biến hội thoại BA -> tài liệu requirement
    Templates/     #     Sinh file .docx
  Tools/           #   Hệ thống công cụ cho agent (xem mục 5.3)
    Abstractions/  #     Interface hợp đồng (IToolExecutionLogger)
    Execution/     #     Class hiện thực: policy, logger, schema builder
    Registry/      #     Khám phá & gọi tool động (reflection)
  Workflows/       #   Orchestrator điều phối nhiều bước + background worker
Controllers/       # MVC controller. Mỏng: chỉ nhận request -> gọi Application -> trả View/Json.
Views/             # Razor view (.cshtml)
Extensions/        # ApplicationServiceCollectionExtensions: nơi DUY NHẤT đăng ký DI.
Prompts/           # Template prompt dạng .md (copy ra output khi build).
Migrations/        # EF Core migrations (tự sinh — không sửa tay).
wwwroot/           # Tài nguyên tĩnh (css/js).
tests/             # Unit test (xUnit).
```

**Quy ước vàng:** `namespace` luôn khớp với đường dẫn thư mục
(`Services/Tools/Execution/Foo.cs` → `namespace ICOGenerator.Services.Tools.Execution`).
Nhờ đó, nhìn namespace là biết file ở đâu và ngược lại.

---

## 3. Chiều phụ thuộc (dependency rule)

Mũi tên = "được phép phụ thuộc vào". Phụ thuộc chỉ đi **một chiều, từ trên xuống**:

```
Controllers ─► Application ─► Services ─► Data ─► Domain
                   │              │                  ▲
                   └──────────────┴──────────────────┘
                         (đều có thể dùng Domain & Contracts)
```

Luật bất di bất dịch:
- **Domain** không phụ thuộc gì (chỉ tự tham chiếu `Domain.Enums`). Đây là tầng ổn định nhất.
- **Contracts** thuần POCO, không phụ thuộc layer khác.
- **Services** *không bao giờ* `using` ngược lên `Application` hay `Controllers`.
- **Application** điều phối: được phép gọi `Data`, `Domain`, `Services`.
- **Controllers** chỉ gọi `Application` (không gọi thẳng `Services`/`Data`).

> Đã kiểm chứng: hiện không có vi phạm chiều nào ở trên.

---

## 4. Luồng xử lý một request (ví dụ: tạo bản nháp requirement)

```
Browser
  └► RequirementsController.Chat(projectId, message)        [Controllers] - mỏng
       └► GenerateRequirementDraftUseCase.ExecuteAsync(...)  [Application] - điều phối
            ├► BARequirementService                          [Services/Requirements]
            │     ├► RequirementPromptBuilder  (dựng prompt)
            │     ├► ILlmClient                 (gọi LLM)      [Services/Llm]
            │     ├► RequirementResponseParser  (parse JSON)
            │     └► RequirementDocumentGenerator -> Templates/DocxTemplateWriter
            └► AppDbContext.SaveChanges                        [Data]
```

Controller không chứa logic; nó chỉ map HTTP ⇄ use case. Toàn bộ "việc thật" nằm ở
Application (điều phối) và Services (chi tiết kỹ thuật).

---

## 5. Các pattern chính

### 5.1. Use Case / Command–Query mỗi thao tác một class (tầng Application)
Mỗi hành động người dùng = **một class, một file**, có đúng một method công khai `ExecuteAsync`.

- Class **đọc** đặt tên `...Query`   → `GetProjectListQuery`, `ListAiModelsQuery`.
- Class **ghi/đổi trạng thái** đặt tên `...UseCase` → `CreateProjectUseCase`, `UpdateAgentUseCase`.
- ViewModel của form đặt tên `...Vm` → `ProjectCreateVm`, `AgentEditVm`.

Lợi ích: dễ tìm, dễ test, dễ thêm mới mà không đụng class cũ (Open/Closed).

### 5.2. Thin Controller
Controller chỉ: nhận tham số → gọi 1 use case → trả `View`/`Json`/`Redirect`.
Không truy vấn DB, không gọi LLM trực tiếp.

### 5.3. Tool system cho agent (Registry + Reflection)
Một "tool" chỉ là **một method C# `public`** trong một class `*Tools`, được gắn `[Description]`.
Không có interface chiến lược (`IAgentTool`) hay lớp adapter bọc method — hệ thống gọi thẳng
method qua reflection.

- `Tools/Abstractions`  — **hợp đồng**: `IToolExecutionLogger` (ghi log mỗi lần gọi tool).
- `Tools/Execution`     — **hiện thực**: `ToolPolicyService` (kiểm tra tool có được phép gọi) và
  `ToolExecutionLogger`. (JSON schema của tham số do `AIFunctionFactory` sinh từ chữ ký method.)
- `Tools/Registry`      — `ToolDiscoveryService` quét các method có `[Description]` rồi đồng bộ vào
  bảng `ToolDefinition`; `ToolRegistry`/`IToolRegistry` lấy danh sách tool theo agent;
  `ToolRuntimeDescriptor` gói (definition + instance + `MethodInfo`) cho một tool runtime. Việc
  deserialize JSON args của model vào tham số method và invoke do `AIFunctionFactory` lo (xem mục 5.8).
- Các nhóm tool nghiệp vụ: `WorkspaceTools`, `CommandTools`, `GitTools`, `DiffTools`.

Muốn thêm tool mới cho agent: viết một method `public` có `[Description]` trong một class `*Tools`
(thêm class mới vào `ToolDiscoveryService.ToolTypes` nếu cần). Registry + `AIFunctionFactory` tự sinh
schema từ chữ ký method và cho agent gọi — không phải sửa vòng lặp agent.

### 5.4. Background processing (Hosted Service + Orchestrator)
- `AgentTaskWorker` là `BackgroundService` chạy nền (poll `AgentTask` ở trạng thái `Queued`).
- `WorkflowOrchestrator` (ẩn sau `IWorkflowOrchestrator`) điều phối các bước workflow.

### 5.5. Prompt as template
Prompt nằm ở file `.md` trong `/Prompts` (được copy ra output khi build) và nạp/render qua
`PromptTemplateService`. Đổi nội dung prompt không cần build lại logic.

### 5.6. Đăng ký DI tập trung
Mọi đăng ký dịch vụ nằm ở `Extensions/ApplicationServiceCollectionExtensions.cs`, chia thành các
method nhỏ `AddXxx()` — **mỗi nhóm tương ứng một thư mục/layer**. `Program.cs` chỉ gọi
`AddIcoGeneratorApplication(...)`.

### 5.7. Xác thực (cookie auth, secure-by-default)
Toàn app nằm sau một lớp đăng nhập cookie. Cấu hình tập trung ở `AddAuthServices()`:
một **fallback authorization policy** bắt **mọi endpoint** phải đăng nhập, trừ nơi gắn
`[AllowAnonymous]` (trang `Account/Login`, `Home/Error`). Nhờ vậy một controller mới quên
`[Authorize]` vẫn được bảo vệ mặc định — quan trọng vì trang Settings sửa được `AllowedCommands`.
Thông tin đăng nhập đọc từ cấu hình `Auth:Username`/`Auth:Password` (mật khẩu là secret, nạp qua
`Auth__Password`/user-secrets — **không commit**), so khớp **fixed-time** trong `LoginUseCase`.
`AccountController` mỏng: validate qua use case rồi `SignInAsync`/`SignOutAsync`.

### 5.8. Đường thực thi agent (native tool-calling, dùng Microsoft Agent Framework)
`AgentRunService.RunAsync` chạy agent trên **Microsoft Agent Framework (`Microsoft.Agents.AI`)**: một
`ChatClientAgent` + `AgentSession` **tự lo vòng lặp ReAct** (gọi model → gọi tool → lặp), nên
`AgentRunService` **không có vòng `for` tự viết**. Tool được quảng bá qua tham số `tools` của OpenAI,
schema sinh bằng `AIFunctionFactory` từ chữ ký method. Các mối quan tâm cắt ngang được tách thành
**middleware**:

- `ModelCallLoggingChatClient` (`DelegatingChatClient`): mỗi lần gọi model → đặt deadline, tính trần
  completion-token, **dựng `LlmCallResult` + map lỗi API/timeout**, log request/response vào DB
  (`IModelCallLogger`), đẩy progress "thinking" theo bước, và (khi `throwOnFailure`) biến một lời gọi
  lỗi thành lỗi kết thúc run. (Token live do orchestrator đẩy từ `RunStreamingAsync` nên không emit ở
  đây để khỏi lặp.) **Đây là middleware dùng chung** — `LlmClient` (đường chat thuần của BA) cũng
  compose nó qua `ChatClientBuilder`, nên deadline/token-cap/log/dựng-result không bị viết lặp hai nơi.
- `InvokerBackedAIFunction` (`DelegatingAIFunction`): bọc mỗi tool — schema/tên **và cả bind args +
  invoke** đều do `AIFunctionFactory` lo (wrapper gọi thẳng `base.InvokeCoreAsync`, không tự bind/reflect
  nữa); wrapper chỉ **chồng thêm** các mối quan tâm của app: report tiến độ, `ToolPolicyService` (policy
  theo agent), `IToolExecutionLogger` (log), và chốt chặn `ToolArgumentValidator`: call thiếu đối số bắt
  buộc (args bị cắt do `finish_reason=length` hay không gộp được) bị **từ chối** và trả observation yêu
  cầu model gọi lại — thay vì bind null rồi làm hỏng dữ liệu âm thầm (vd `SetPocContent` không có `content`).

Ngân sách bước được mô phỏng qua trần lặp `FunctionInvokingChatClient.MaximumIterationsPerRequest`
trong **ba pha** trên cùng một `AgentSession`: (1) chạy trong ngân sách kỳ vọng; (2) nếu chưa xong thì
nhắc "hoàn tất nốt" và cấp thêm tới trần cứng (`maxSteps * AutoContinueFactor`); (3) nếu vẫn chưa xong
thì một lượt **salvage** không-tool để chốt tóm tắt phần đã làm (file đã nằm trên đĩa) thay vì fail
trắng. Quy ước phát hiện "đã hội tụ": pha kết thúc khi dùng **ít hơn** ngân sách của nó (model trả lời
mà không xin thêm tool).

> **Lịch sử:** trước đây còn một đường **fallback prompt-based** (vòng ReAct tự viết, hợp đồng JSON
> action nằm trong prompt `tool-agent.v1.md`, `AgentActionParser` parse phản hồi) cho model không hỗ trợ
> tham số `tools`. Đường này đã được **gỡ bỏ** vì mọi model mục tiêu đều hỗ trợ native tool-calling —
> cùng với `NativeToolCallingPolicy`, `AgentActionParser`/`AgentActionDto`, `ToolSchemaBuilder` và cấu
> hình `Llm:NativeToolCalling`. Giờ chỉ còn một đường thực thi duy nhất, không phải chọn theo model.

### 5.9. Structured output cho các lời gọi BA (opt-in)
Các lời gọi của BA trả JSON (soạn 5 tài liệu, cổng kiểm tra đầy đủ, gợi ý chat) có thể dùng **structured
output** của MEAI (`response_format: json_schema` qua `IChatClient.GetResponseAsync<T>`) thay vì chỉ nhắc
model trả JSON rồi parse văn xuôi. `ILlmClient.ChatStructuredAsync<T>` lo việc này; khi structured **không**
bật/không khả dụng/JSON không khớp schema thì trả `value = null` để caller **fallback** về parser tay cũ
(`RequirementResponseParser`/`BAChatReplyParser`/`RequirementReadinessParser`) — không bao giờ fail trắng.
Quyết định bật theo `StructuredOutputPolicy` (cấu hình `Llm:StructuredOutput`): **opt-in**, mặc định TẮT vì
nhiều server local từ chối `response_format`; chỉ liệt kê ModelId chắc chắn hỗ trợ vào `ModelIds`. Mặc định
tắt ⇒ hành vi **giữ nguyên** đường text + parser cũ.

---

## 6. Công thức thêm một tính năng mới

Ví dụ: thêm màn hình "xuất báo cáo tổng hợp project".

1. **Domain/Contracts:** nếu cần kiểu dữ liệu mới → thêm entity vào `Domain/` hoặc DTO vào `Contracts/`.
2. **Application:** tạo `Application/Projects/ExportProjectReportUseCase.cs` (một class, một `ExecuteAsync`).
3. **Services (nếu cần):** nếu có logic kỹ thuật tái dùng (sinh file, gọi LLM...) → đặt ở `Services/...`.
4. **Controller:** thêm action mỏng trong `ProjectsController` gọi use case.
5. **View:** thêm `.cshtml` nếu trả UI.
6. **DI:** đăng ký use case trong nhóm `AddProjectUseCases()` ở file Extensions.
7. **Test:** thêm test ở `tests/`.

Nếu một class không rơi gọn vào bước nào ở trên thì nhiều khả năng nó đang gánh quá nhiều việc — tách ra.

---

## 7. Những gì đã được dọn trong lần refactor này

| Vấn đề trước đây | Đã xử lý |
|---|---|
| `ManageAgentsUseCases.cs` / `ManageAiModelsUseCases.cs` gộp nhiều class trong một file (lệch với phần còn lại) | Tách thành một-class-một-file (`GetAgentManagementPageQuery`, `UpdateAgentUseCase`, `ListAiModelsQuery`, `CreateAiModelUseCase`, ...) |
| Thư mục `Tools/Abstractions` chứa lẫn cả class hiện thực (`ToolPolicyService`, `ToolExecutionLogger`) | Tách contract (interface) ở `Abstractions`, chuyển hiện thực sang `Tools/Execution` |
| Đăng ký DI để lẫn layer (service của `Services/Requirements` đăng ký trong nhóm "Application use case"; `BARequirementService` nằm trong nhóm "Agent runtime") | Gom đúng nhóm: thêm `AddRequirementServices()`; mỗi nhóm `AddXxx` khớp một thư mục |

Không thay đổi hành vi runtime: số lượng và nội dung đăng ký DI giữ nguyên (48 đăng ký), chỉ
sắp xếp lại; namespace luôn khớp đường dẫn.

---

## 8. Quy ước nên giữ về sau

- **Một file = một kiểu công khai** (class/record/enum/interface). Trừ DTO nhóm nhỏ liên quan chặt.
- **Đặt tên theo vai trò:** `...Query` (đọc), `...UseCase` (ghi), `...Vm` (view model),
  `I...` (interface), `...Service` (service nghiệp vụ).
- **namespace = đường dẫn thư mục.**
- **Controller luôn mỏng**, không chứa logic nghiệp vụ.
- **Đăng ký DI** chỉ ở file Extensions, đúng nhóm theo layer.
- **Đừng để Services `using` ngược** lên Application/Controllers.

---

## 9. Quan sát còn lại (chưa xử lý, để bạn cân nhắc)

- **Luồng job bất đồng bộ đã được gỡ:** trước đây `StartRequirementChatUseCase` (action
  `StartChat`), `GetRequirementJobStatusQuery` (action `JobStatus`) và `BackgroundService`
  `AgentJobRunner` tạo nên một hàng đợi `AgentJob` mà UI không bao giờ gọi tới — chat BA thật đi
  qua `Chat` → `ChatWithBAUseCase` (đồng bộ). Cụm code chết này đã được xoá. **Bảng `AgentJobs`
  cũng đã được drop** (entity `AgentJob` + enum `AgentJobStatus` + `DbSet` đã xoá). Toàn bộ lịch
  sử migration sau đó đã được gộp lại thành một baseline duy nhất `20260617161007_V1` (tạo 10
  bảng, không còn `AgentJobs`); migration `RemoveAgentJob` riêng lẻ không còn tồn tại nữa.
- `Services/Logging` chỉ có logger cho lời gọi model, đặt cạnh `Services/Llm`. Nếu sau này log
  nhiều loại hơn thì giữ nguyên là hợp lý; nếu không, có thể gộp vào `Llm`.
- Package `Microsoft.EntityFrameworkCore.Sqlite` (không dùng — `AppDbContext` chỉ `UseSqlServer`)
  đã được gỡ khỏi `ICOGenerator.csproj`.
