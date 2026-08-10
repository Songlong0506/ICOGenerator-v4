# Agent & hệ thống Tool

## Vòng lặp agent — `AgentRunService.RunAsync`

`AgentRunService.RunAsync` chạy agent trên **Microsoft Agent Framework (`Microsoft.Agents.AI`)**: một
`ChatClientAgent` + `AgentSession` **tự lo vòng lặp ReAct** (gọi model → gọi tool → lặp), nên
`AgentRunService` **không có vòng `for` tự viết**. Tool được quảng bá qua tham số `tools` của OpenAI,
schema sinh bằng `AIFunctionFactory` từ chữ ký method.

Ngân sách bước được mô phỏng qua trần lặp `FunctionInvokingChatClient.MaximumIterationsPerRequest`
trong **ba pha** trên cùng một `AgentSession`:

1. Chạy trong ngân sách kỳ vọng (`MaxSteps` của bước pipeline).
2. Chưa xong ⇒ nhắc "hoàn tất nốt", cấp thêm tới trần cứng (`maxSteps × AutoContinueFactor`).
3. Vẫn chưa xong ⇒ một lượt **salvage** không-tool để chốt tóm tắt phần đã làm (file đã nằm trên đĩa)
   thay vì fail trắng.

Quy ước phát hiện "đã hội tụ": pha kết thúc khi dùng **ít hơn** ngân sách của nó (model trả lời mà
không xin thêm tool).

### Cross-cutting concerns là middleware, không nằm trong vòng lặp

- `ModelCallLoggingChatClient` (`DelegatingChatClient`): mỗi lần gọi model → hỏi cầu dao ngân sách, đặt
  deadline, tính trần completion-token, **dựng `LlmCallResult` + map lỗi API/timeout**, log
  request/response vào DB (`IModelCallLogger` → `AgentModelCallLogs`), đẩy progress "thinking" theo bước,
  và (khi `ModelCallOptions.ThrowOnFailure`) biến một lời gọi lỗi thành lỗi kết thúc run. (Token live do
  orchestrator đẩy từ `RunStreamingAsync` nên không emit ở đây để khỏi lặp.) **Đây là middleware dùng
  chung** cho cả ba đường gọi model — agent, chat thuần của BA (`LlmClient`) và eval
  (`EvalRunnerService`) — nên deadline/token-cap/log/dựng-result không bị viết lặp ba nơi. Các núm vặn
  khác nhau giữa ba đường nằm trong record `ModelCallOptions` (xem [llm-and-prompts.md](llm-and-prompts.md#giải-phẫu-servicesllm-một-trách-nhiệm-một-file)).
- `InvokerBackedAIFunction` (`DelegatingAIFunction`): bọc mỗi tool — schema/tên **và cả bind args +
  invoke** đều do `AIFunctionFactory` lo (wrapper gọi thẳng `base.InvokeCoreAsync`, không tự bind/reflect
  nữa); wrapper chỉ **chồng thêm** các mối quan tâm của app: report tiến độ, `ToolPolicyService` (policy
  theo agent), `IToolExecutionLogger` (log), và chốt chặn `ToolArgumentValidator`: call thiếu đối số bắt
  buộc (args bị cắt do `finish_reason=length` hay không gộp được) bị **từ chối** và trả observation yêu
  cầu model gọi lại — thay vì bind null rồi làm hỏng dữ liệu âm thầm (vd `SetPocContent` không có `content`).

> **Lịch sử:** trước đây còn một đường **fallback prompt-based** (vòng ReAct tự viết, hợp đồng JSON
> action nằm trong prompt `tool-agent.v1.md`, `AgentActionParser` parse phản hồi) cho model không hỗ trợ
> tham số `tools`. Đường này đã được **gỡ bỏ** vì mọi model mục tiêu đều hỗ trợ native tool-calling —
> cùng với `NativeToolCallingPolicy`, `AgentActionParser`/`AgentActionDto`, `ToolSchemaBuilder` và cấu
> hình `Llm:NativeToolCalling`. Giờ chỉ còn một đường thực thi duy nhất, không phải chọn theo model.

---

## Tool = một method C# public có `[Description]`

Một "tool" chỉ là **một method C# `public`** trong một class `*Tools`, được gắn `[Description]`.
Không có interface chiến lược (`IAgentTool`) hay lớp adapter bọc method.

- `Tools/Abstractions` — **hợp đồng**: `IToolExecutionLogger` (ghi log mỗi lần gọi tool).
- `Tools/Execution` — **hiện thực**: `ToolPolicyService` (kiểm tra tool có được phép gọi) và
  `ToolExecutionLogger`. (JSON schema của tham số do `AIFunctionFactory` sinh từ chữ ký method.)
- `Tools/Registry` — `ToolDiscoveryService` quét các method có `[Description]` trong các class thuộc
  `ToolDiscoveryService.ToolTypes` rồi đồng bộ vào bảng `ToolDefinitions`; `ToolRegistry`/`IToolRegistry`
  lấy danh sách tool theo agent; `ToolRuntimeDescriptor` gói (definition + instance + `MethodInfo`) cho
  một tool runtime. Việc deserialize JSON args của model vào tham số method và invoke do
  `AIFunctionFactory` lo (xem phần vòng lặp agent ở trên).
- Các nhóm tool nghiệp vụ: `WorkspaceTools`, `CommandTools`, `GitTools`.
- `Tools/PullRequests` — hạ tầng tạo PR mà `GitTools.OpenPullRequest` dùng (**không** phải tool
  gọi-được của agent): `GitHubPullRequestPublisher`/`IPullRequestPublisher` (gọi GitHub REST API),
  `PullRequestUrlBuilder` (dựng link compare khi không tạo được PR thật), `GitRemoteUrl` (parse remote
  URL dùng chung).

### Danh mục tool hiện có

| Nhóm | Tool | Chức năng |
|---|---|---|
| `WorkspaceTools` | `ListFiles` | Liệt kê file trong workspace |
| | `ReadFile(relativePath, offset)` | Đọc file (<200KB trả full; lớn hơn phân trang theo `offset`) |
| | `WriteFile(relativePath, content)` | Ghi một file |
| | `WriteFiles(files[])` | Ghi **nhiều file một lần** — quan trọng cho bước Implementation để không đốt hết budget từng file lẻ |
| | `SearchFiles(keyword)` | Tìm file theo keyword trong đường dẫn |
| | `ReplaceInFile(relativePath, oldText, newText)` | Thay text trong file có sẵn |
| | `SetPocContent` / `AppendPocContent` | Ghi/nối vùng HTML tính năng (`POC_CONTENT`) của `04_Implementation/poc-demo.html` — nối nhiều call nhỏ để không bị cắt token |
| | `SetPocScript` / `AppendPocScript` | Ghi/nối vùng JS nghiệp vụ (`POC_SCRIPT`) — hiện thực business rules thật (tính toán, chuyển trạng thái, mô phỏng vai) |
| | `AuditPocContent` | Tự soát POC: menu thiếu section, id trùng, modal trỏ id không tồn tại, CRUD lệch field, script rỗng, **độ phủ so với AI Design Spec** — agent phải sửa hết ISSUE rồi audit lại (tối đa 3 vòng) |
| `CommandTools` | `RunCommand(command)` | Chạy lệnh shell **trong whitelist `AllowedCommands`**, timeout `Commands:TimeoutSeconds` (120s) |
| `GitTools` | `GitStatus`, `GitDiff` | trạng thái / diff --stat |
| | `CreateBranch(branchName, baseBranch)` | Tạo + checkout nhánh |
| | `GitCommit(message)`, `PushBranch(branchName)` | Commit / push |
| | `OpenPullRequest(branchName, title, body)` | Push + tạo PR thật (có token) hoặc trả link compare |

### Tool mặc định theo vai

Gán trong `DbInitializer.AssignDefaultToolsAsync`:

| Vai | Tools |
|---|---|
| BA | ListFiles, ReadFile, WriteFile, SearchFiles |
| Tech Lead | ListFiles, ReadFile, WriteFile, GitDiff, GitStatus |
| Developer | Tất cả Workspace + POC tools, RunCommand, GitStatus, GitCommit, CreateBranch, PushBranch, OpenPullRequest |
| Tester | ListFiles, ReadFile, WriteFile, RunCommand |
| UI/UX | WriteFile, ReadFile, ListFiles |

**Thêm tool mới** = viết một method public có `[Description]` trong một class `*Tools` (class mới thì
thêm vào `ToolDiscoveryService.ToolTypes`), rồi gán cho vai trong `AssignDefaultToolsAsync` (hoặc tick
trong UI Agents). Registry + `AIFunctionFactory` tự sinh schema — **không phải sửa vòng lặp agent**.

---

## Rào chắn an toàn của tool

- `AllowedCommands` (appsettings): `RunCommand` chỉ chạy lệnh bắt đầu bằng các entry này (`dotnet`, `git status`, `npm`...).
- `AllowedFileExtensions`: tool file chỉ đụng các đuôi cho phép.
- `WorkspacePathResolver.GetSafeFullPath`: chống path-traversal *và* chống symlink escape (resolve tổ tiên sâu nhất tồn tại rồi kiểm tra lại nằm trong workspace).
- `ToolPolicyService`: kiểm tra tool có nằm trong tập được cấp cho agent đó.
- `ToolExecutionLogger`: ghi log mỗi lần gọi tool.
