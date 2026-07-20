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
  Evals/           #   Prompt eval harness: golden set + runner + LLM-judge + worker nền (xem 5.15)
  Llm/             #   Client gọi LLM + model request/response + ghi log lời gọi model (IModelCallLogger)
  Prompts/         #   Nạp & render template prompt (file .md trong /Prompts)
  Requirements/    #   Biến hội thoại BA -> tài liệu requirement
    Templates/     #     Sinh file .docx
  Tools/           #   Hệ thống công cụ cho agent (xem mục 5.3)
    Abstractions/  #     Interface hợp đồng (IToolExecutionLogger)
    Execution/     #     Class hiện thực: policy, logger, schema builder
    Registry/      #     Khám phá & gọi tool động (reflection)
    PullRequests/  #     Hạ tầng publish PR (GitHub API, dựng link compare, parse remote URL)
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
- Các nhóm tool nghiệp vụ: `WorkspaceTools`, `CommandTools`, `GitTools` (xem `ToolDiscoveryService.ToolTypes`).
- `Tools/PullRequests` — hạ tầng tạo PR mà `GitTools.OpenPullRequest` dùng (không phải tool gọi-được của agent):
  `GitHubPullRequestPublisher`/`IPullRequestPublisher` (gọi GitHub REST API), `PullRequestUrlBuilder` (dựng link
  compare khi không tạo được PR thật), `GitRemoteUrl` (parse remote URL dùng chung).

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

### 5.7. Xác thực & phân quyền (cookie auth, secure-by-default)
Toàn app nằm sau một lớp đăng nhập cookie. Cấu hình tập trung ở `AddAuthServices()`:
một **fallback authorization policy** bắt **mọi endpoint** phải đăng nhập, trừ nơi gắn
`[AllowAnonymous]` (trang `Account/Login`, `Home/Error`). Nhờ vậy một controller mới quên
`[Authorize]` vẫn được bảo vệ mặc định — quan trọng vì trang Settings sửa được `AllowedCommands`.
Người dùng nằm ở bảng **`AppUser`** (seed sẵn `admin`/`teamdev`/`user`); `LoginUseCase` đối chiếu
mật khẩu băm bằng `PasswordHasher` rồi `AccountController` phát hành claim **Role**.

Trên nền đó là **phân quyền theo role** (`UserRole`: SuperAdmin/Admin/TeamDev/User): quyền ở mức hành động
(`AppPermission`) được cấp cho role qua bảng **`RolePermission`** (cấu hình runtime tại màn hình
Roles & Permissions). `IPermissionService` (có cache, SuperAdmin implicit-all) là nguồn sự thật duy nhất,
dùng bởi cả filter `[RequirePermission(...)]` trên controller/action lẫn `_Layout` để lọc menu.
Thiếu quyền ⇒ `/Account/AccessDenied`. Chi tiết xem DEVELOPER_GUIDE §8.1.

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

### 5.10. Logging tập trung & observability (Serilog + OpenTelemetry opt-in)
Toàn app ghi log qua **Serilog** thay cho logging Console mặc định của ASP.NET. Cấu hình (mức log, sink,
enrich) nằm ở section `Serilog` trong `appsettings.json` nên đổi **không cần build lại**; `Program.cs` chỉ
`builder.Host.UseSerilog(...)` đọc từ config + `Enrich.FromLogContext`, và `app.UseSerilogRequestLogging()`
ghi **một dòng tóm tắt có cấu trúc cho mỗi HTTP request** (method/path/status/thời lượng) thay cho log mặc
định dài dòng. Một **bootstrap logger** (`CreateBootstrapLogger`) bắt cả lỗi xảy ra TRƯỚC khi host dựng
xong — quan trọng vì `DbInitializer` migrate DB + seed **ngay lúc khởi động**; vì vậy toàn bộ thân
`Program.cs` nằm trong `try / catch(Log.Fatal) / finally(Log.CloseAndFlush)` để một lỗi khởi động (vd không
kết nối được SQL) thành **một log `Fatal`** rồi flush, thay vì stack trace trần ra stderr. Sink mặc định:
Console (stdout — để Docker/k8s/journald gom) + File xoay vòng theo ngày trong `Logs/` (đã `.gitignore`,
giữ 14 ngày). Production có thể đổi Console sang JSON nén cho log aggregator (Seq/Loki/ELK) qua
`appsettings.Production.json` — **không sửa code**.

Trên đó, **OpenTelemetry** (trace + metric) là **OPT-IN** qua `Otel:Enabled` (mặc định TẮT, cùng tinh thần
opt-in như `Llm:Proxy` / `StructuredOutput` / `Budget`): chưa bật thì `AddObservabilityServices` **không
đăng ký gì** — zero overhead, không sinh lỗi exporter. Khi bật, instrument **ASP.NET Core + HttpClient**
(nên các lời gọi LLM ra ngoài tự thành span — **dựng lại được chuỗi agent → model → tool**) và **metric
runtime/HTTP**, rồi xuất qua **OTLP** tới collector (`Otel:OtlpEndpoint`, trống ⇒ mặc định gRPC
`http://localhost:4317`). Đăng ký tập trung ở `AddObservabilityServices` trong file Extensions như mọi nhóm
DI khác.

Collector **không nhúng vào app** (giữ đúng ranh giới OTel: SDK sinh telemetry ↔ collector/backend nhận-lưu-
hiển thị, khác vòng đời, khác scale). Để dev/demo "bật là chạy", repo kèm `docker-compose.otel.yml` dựng
**.NET Aspire Dashboard** (OTLP endpoint + UI trong một image) map ra `localhost:4317` — khớp default nên chỉ
cần `docker compose -f docker-compose.otel.yml up -d` rồi `Otel:Enabled=true`. File compose chạy dashboard
**anonymous**, chỉ dùng local — production trỏ `Otel:OtlpEndpoint` tới collector thật (Jaeger/Tempo/Grafana).

### 5.11. Bộ nhớ hội thoại BA (summarization memory — hai tầng nhớ)
Hội thoại BA (`ChatWithBAUseCase` → `BAChatService.ChatAsync`) dùng **hai tầng nhớ** để giữ ngữ
cảnh khi chat dài mà prompt không phình token, do `ConversationMemoryService` lo:

- **Ngắn hạn (working memory):** `RecentWindowSize` (=20) lượt gần nhất luôn gửi **nguyên văn** cho model.
- **Dài hạn:** các lượt **cũ** rơi ra ngoài cửa sổ được **gộp dần** thành một đoạn tóm tắt (text) lưu bền
  trên `Project.ConversationSummary`; `Project.SummarizedTurnCount` là con trỏ "đã gộp tới lượt nào". Đoạn
  tóm tắt được đính vào prompt như một `System` message nền (prompt `BusinessAnalyst/conversation-summary.v1.md`).

Việc tóm tắt **gom theo lô**: chỉ gọi LLM khi đã có ít nhất `SummarizeBatchThreshold` (=10) lượt cũ chưa
gộp — nên KHÔNG tóm tắt trên mỗi lượt chat (đây mới là chỗ tiết kiệm token). Trong lúc chờ đủ lô, các lượt
đó vẫn gửi nguyên văn nên **không mất ngữ cảnh**; cửa sổ verbatim chỉ phình tạm tới `20 + (10-1)` rồi co lại
sau mỗi lần gộp. **Fail-open:** lời gọi tóm tắt lỗi ⇒ giữ nguyên summary cũ, KHÔNG dời con trỏ (các lượt
chưa gộp vẫn được gửi nguyên văn) — không bao giờ fail trắng, không mất lượt nào.

### 5.12. Bộ nhớ cấp người dùng (personalization — "càng nói càng hiểu user")
Song song với bộ nhớ theo dự án ở 5.11, `UserMemoryService` lo một tầng nhớ **gắn theo NGƯỜI DÙNG** chứ
không theo dự án — đây là thứ tạo cảm giác giống Claude/ChatGPT: trò chuyện càng nhiều, BA càng hiểu user.

- **Lưu ở đâu:** một hồ sơ ngắn gọn các sự thật **bền** về user (vai trò, lĩnh vực, tổ chức, văn phong/định
  dạng ưa dùng, thuật ngữ hay dùng…) lưu trên `AppUser.UserMemory`, dùng lại **xuyên suốt mọi dự án** của họ.
  Hồ sơ được quy về **người tạo dự án** (`Project.CreatedByUsername`); dự án không có chủ thì bỏ qua.
- **Chắt lọc khi nào:** `BAChatService.ChatAsync` gọi `UserMemoryService.UpdateAndLoadAsync` mỗi lượt;
  việc gọi LLM chắt lọc **gom theo lô** — chỉ chạy khi đã có ≥ `HarvestBatchThreshold` (=10) lượt mới chưa
  chắt lọc (con trỏ riêng `Project.UserMemoryHarvestedTurnCount`, tách khỏi `SummarizedTurnCount` vì hai bộ
  nhớ tiến theo nhịp khác nhau). Prompt: `BusinessAnalyst/user-memory.v1.md`.
- **Nạp lại:** hồ sơ user (nếu có) được đính vào prompt BA như một `System` message nền — nên BA "đã biết
  user là ai" ngay từ lượt đầu, kể cả ở dự án mới.
- **Fail-open:** lời gọi chắt lọc lỗi ⇒ giữ hồ sơ cũ, KHÔNG dời con trỏ; lần sau gặp ngưỡng sẽ thử lại.

### 5.13. Bối cảnh tổ chức Bosch (OrgUnits/Associates → prompt BA + tài liệu + Usage)
Hai bảng **`OrgUnits`/`Associates`** (đồng bộ từ HR_Portal, seed một lần khi trống — xem `DbInitializer`)
được khai thác qua **`OrganizationContextService`** (Services/Requirements):

- **`BuildBaContextAsync`** render một "bức tranh tổ chức" gọn (~3–4KB): danh sách department + HoD
  (tra `TrgtManagerLId` → `Associates.PersonalNumber`), số orgUnit trực thuộc + headcount **roll-up cả cây
  con** (đi theo `TargetResponsible`, chống chu trình), chức danh phổ biến và quy mô. Phần chữ tĩnh nằm ở
  template `Prompts/BusinessAnalyst/organization-context.v2.md` (thay thế bản điền tay v1; comment HTML đầu file bị cắt
  trước khi render); dữ liệu chỉ ở dạng GỘP — **không đưa PII của Associates** (ngày sinh/điện thoại/email)
  vào prompt, tên người thật chỉ xuất hiện ở vai trò HoD/manager. Bản render **cache trong IMemoryCache 1h**.
- **`BuildProjectUnitNoteAsync`** dựng ghi chú "đơn vị yêu cầu" từ **`Project.OrgUnitCode`** (chọn tùy chọn
  ở modal New Project; `CreateProjectUseCase` chỉ lưu mã có thật trong OrgUnits): orgUnit + manager +
  department cha + HoD.
- Nơi tiêu thụ: `BAChatService.ChatAsync` (system message nền — BA hiểu tên phòng/vai trò, gợi ý
  bằng tên phòng thật, hỏi luồng duyệt đúng ngôn ngữ manager/HoD, biết external KHÔNG nằm trong dữ liệu HR),
  và các lời gọi soạn/soát/sửa Product Brief + Technical Docs (`RequirementPromptBuilder` — tài liệu dùng
  đúng tên phòng ban/HoD thật thay vì "TBD"; khối context đưa cả vào vòng tự soát để reviewer không coi tên
  thật là "tự thêm"). Trang **Usage** thêm bảng "Usage by department" (roll-up orgUnit của project về
  department gần nhất). **Fail-open toàn tuyến**: bảng trống/lỗi ⇒ mọi luồng chạy như trước.

### 5.14. Lịch sử revision tài liệu sinh ra (version history + diff)
Tài liệu sinh ra bị **ghi đè** ở nhiều luồng (bấm lại "Write Requirement" trên draft; vòng "Yêu cầu
chỉnh sửa" sinh lại BRD/SRS/FSD/UserStories cùng phiên bản) — trước đây lịch sử mất sạch. Nay
`RequirementDocumentGenerator.UpsertDocument` là **chốt chặn duy nhất**: mỗi lần Content được ghi
(lần đầu hoặc ghi đè CÓ thay đổi) nó chụp một **`ProjectDocumentRevision`** (nội dung đầy đủ — không
lưu delta — + `ChangeNote` nguồn gốc: "Write Requirement", "Chỉnh sửa theo nhận xét: ..." v.v.; ghi
lại cùng nội dung thì KHÔNG snapshot). Revision chỉ Add vào change tracker — SaveChanges của caller
lưu **atomic** cùng document, không bao giờ có revision mồ côi. Diff giữa revision liền kề tính **lúc
xem** bằng `DocumentDiffService` (LCS theo dòng, trim đầu/cuối chung, quá trần DP thì fallback "thay
cả khối"). UI: nút **Lịch sử** ở modal tài liệu trang Requirements + khung preview Agent Dashboard
(chỉ doc DB-tracked), dùng chung `wwwroot/js/doc-history.js` + endpoint
`Requirements/DocumentRevisions|DocumentRevisionDiff`.

### 5.15. Prompt evaluation harness (golden set + LLM-judge, màn hình Prompt Evals)
Trả lời câu "sửa prompt/đổi model xong, chất lượng LÊN hay XUỐNG?" bằng số thay vì cảm tính:
- **`EvalScenario`** (golden set): một tình huống = (template prompt dưới `/Prompts` + đầu vào mô
  phỏng + tiêu chí chấm). System prompt lấy **nội dung hiện hành** của file template lúc chạy, nên
  cùng bộ scenario đo được các phiên bản prompt khác nhau.
- **`EvalRun`/`EvalResult`**: một run chạy mọi scenario đang bật (lọc được theo template) với model
  MỤC TIÊU rồi để model **JUDGE** chấm 1–5 theo tiêu chí (prompt `Eval/judge.v1.md`, parse bằng
  `EvalJudgeParser`). Run chạy **nền** bởi `EvalRunWorker` (poll Queued như `AgentTaskWorker`; run
  Running mồ côi sau restart → Failed); UI poll tiến độ, xem chi tiết từng scenario và **so sánh 2
  run** theo từng scenario (khớp bằng `EvalScenarioId`).
- Lời gọi eval **tái dùng** middleware `ModelCallLoggingChatClient` (deadline/trần token/map lỗi)
  nhưng với `NullModelCallLogger`: KHÔNG ghi `AgentModelCallLogs` (bảng đó FK cứng Project/Agent) và
  không qua budget guard theo-project — token/lỗi đã nằm trên `EvalResult`.
- Model & scenario tham chiếu bằng **Guid + snapshot tên, không FK** (như `AgentModelCallLog`): xoá
  model/scenario không bị chặn và không mất lịch sử điểm.
- Phân quyền: `EvalView`/`EvalManage` (màn hình "Prompt Evals" trong `PermissionCatalog`; TeamDev
  được seed mặc định). Trang Delivery Quality có card "Prompt evals gần nhất" trỏ sang.

### 5.16. Quản lý phiên bản prompt (Prompt Studio — sửa runtime, rollback, gắn với eval)
Prompt gốc vẫn là file `.md` trong repo, nhưng trước đây sửa prompt là mất bản cũ, muốn đổi phải
deploy, và eval run không biết mình đã đo phiên bản nào. Nay có một lớp PHIÊN BẢN trên DB:

- **`PromptTemplateVersion`**: mỗi lần lưu ở màn hình **Prompt Studio** là một snapshot ĐẦY ĐỦ nội
  dung (không delta — như `ProjectDocumentRevision`), đánh số tăng dần theo `PromptKey`. Lần sửa
  ĐẦU TIÊN chụp thêm nội dung file làm v1 (baseline) nên lịch sử luôn diff được về bản gốc; nội
  dung trùng bản đang dùng thì KHÔNG snapshot. Nhiều nhất MỘT bản `IsActive` mỗi key.
- **Độ phân giải nội dung**: `PromptTemplateService.Get` hỏi `IPromptOverrideProvider`
  (`DbPromptOverrideProvider` — nạp MỌI bản active bằng một query, cache IMemoryCache 30s, các thao
  tác ghi `Invalidate()` nên đổi prompt **có hiệu lực ngay**, không cần deploy/restart). **Fail-open**:
  DB lỗi ⇒ provider trả null ⇒ mọi prompt rơi về nội dung file — app không bao giờ hỏng vì bảng này.
  `GetFileContent` luôn đọc file (baseline cho Studio). Danh mục file quét bởi `PromptFileCatalog`
  (Services/Prompts — đổi tên từ `EvalPromptCatalog` vì giờ Studio cũng dùng).
- **UI (Controllers/PromptsController + Views/Prompts)**: danh sách template (nguồn đang dùng:
  File / DB v{n}), trang chi tiết (editor + "Lưu & kích hoạt", lịch sử, "Kích hoạt" rollback,
  "Quay về file"), trang **Diff** giữa hai mốc (mốc `0` = file; tái dùng `DocumentDiffService` +
  style diff của doc-history). Mọi thao tác ghi vào **Audit Log** (category `Prompt`).
- **Gắn với eval**: `EvalRunnerService` hỏi provider trước khi chạy từng scenario và snapshot
  `EvalResult.PromptVersionId/PromptVersionNumber` (Guid + số, **không FK** — như mọi tham chiếu
  eval khác; null = nội dung file). Chi tiết run hiển thị "prompt v{n}/file" từng kết quả; màn so
  sánh 2 run gắn nhãn phiên bản mỗi bên (cùng nhãn = so MODEL, khác nhãn = so PROMPT); trang chi
  tiết template có bảng **"Điểm eval theo phiên bản"** (gộp điểm judge theo `PromptVersionNumber`)
  — nhìn một bảng là biết phiên bản nào tốt hơn.
- **Export/Import**: mỗi phiên bản tải được về file `.md` (tên mang số phiên bản, vd
  `requirement-chat.v3.db-v2.md`) để đồng bộ ngược bản đã "chín" về repo; chiều ngược lại nút "Nạp
  từ file" đổ nội dung một file `.md` vào editor (client-side) rồi Lưu như một lần sửa bình thường.
- Phân quyền: `PromptView`/`PromptManage` (màn hình "Prompt Studio" trong `PermissionCatalog`;
  TeamDev được seed mặc định — sửa prompt đổi hành vi AI ngay nên chỉ giao cho role tin cậy).

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
  sử migration đã được gộp lại thành một baseline `V1` duy nhất (tạo đủ 24 bảng khớp
  `AppDbContextModelSnapshot`); các migration tiến lẻ tẻ trước đây (drop cột/backfill/alter cho DB
  cũ) không còn tồn tại. Mỗi khi cần reset DB tạo lại từ đầu, cứ xoá `Migrations/` rồi
  `dotnet ef migrations add V1` để có lại một baseline sạch — nhớ để `Database:Provider` là
  `SqlServer` (mặc định; đừng đặt `Database__Provider=Sqlite`) để migration sinh ra theo provider
  SqlServer (production), không phải Sqlite.
- `IModelCallLogger`/`ModelCallLogger` (log lời gọi model) **đã được gộp vào `Services/Llm`** (trước
  đây nằm riêng ở `Services/Logging`): nó chỉ phục vụ một loại log và phụ thuộc chặt `LlmCallResult`,
  nên để cạnh client gọi LLM là hợp lý. Nếu sau này log nhiều loại khác thì tách lại thư mục riêng.
- Package `Microsoft.EntityFrameworkCore.Sqlite` được giữ trong `ICOGenerator.csproj` làm **provider
  thay thế**: chọn qua `Database:Provider` (`SqlServer` mặc định cho môi trường thật; `Sqlite` để chạy
  end-to-end ở nơi KHÔNG có SQL Server — Claude Code web / CI / máy dev). Bật Sqlite bằng env var
  `Database__Provider=Sqlite`; xem `AddDbContext` trong file Extensions. (Model đã provider-agnostic,
  test cũng chạy trên Sqlite.)
