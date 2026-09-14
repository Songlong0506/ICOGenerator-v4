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

System prompt của run do `AgentPromptBuilder.BuildNative` dựng từ `Shared/tool-agent-native.v1.md`:
`{{instruction}}` là `Prompts/{RoleKey}/instruction.md`, còn `{{learnedChecklist}}` là các bài học vai
này đã rút từ **nhận xét của người duyệt ở những dự án trước** (`ChecklistNoteStore`, bucket chung — xem
[delivery-pipeline.md](delivery-pipeline.md#học-từ-nhận-xét-ở-cổng-duyệt)). Checklist đọc **một lần** đầu
`RunAsync` và dùng cho cả lượt chính lẫn lượt salvage; vai chưa học được gì ⇒ khối biến mất hoàn toàn
chứ không để lại tiêu đề rỗng.

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
| | `SearchFiles(keyword)` | Tìm file theo **đường dẫn** chứa keyword |
| | `SearchInFiles(keyword, pathFilter, maxResults)` | Tìm theo **NỘI DUNG** file, trả `path:dòng: text`. Đây là tool điều hướng của các bước đụng code có sẵn — xem [ghi chú bên dưới](#searchinfiles-tìm-theo-nội-dung-không-phải-theo-tên-file) |
| | `ReplaceInFile(relativePath, oldText, newText, replaceAll)` | Thay text trong file có sẵn. `oldText` phải khớp **đúng một chỗ**, nếu không thì call bị TỪ CHỐI và không ghi gì — xem [ghi chú bên dưới](#replaceinfile-khớp-nhiều-chỗ-là-từ-chối-không-phải-thay-hết) |
| | `SetPocContent` / `AppendPocContent` | Ghi/nối vùng HTML tính năng (`POC_CONTENT`) của `04_Implementation/poc-demo.html` — nối nhiều call nhỏ để không bị cắt token |
| | `SetPocScript` / `AppendPocScript` | Ghi/nối vùng JS nghiệp vụ (`POC_SCRIPT`) — hiện thực business rules thật (tính toán, chuyển trạng thái, mô phỏng vai) |
| | `AuditPocContent` | Tự soát POC: menu thiếu section, id trùng, modal trỏ id không tồn tại, CRUD lệch field, script rỗng, **độ phủ so với AI Design Spec** — agent phải sửa hết ISSUE rồi audit lại (tối đa 3 vòng) |
| `CommandTools` | `RunCommand(command, workingDirectory)` | Chạy lệnh shell **trong whitelist `AllowedCommands`**, timeout `Commands:TimeoutSeconds` (120s). `workingDirectory` là đường dẫn **tương đối** so với workspace (rỗng = gốc workspace) — đường DUY NHẤT để `dotnet build`/`npm` chạy đúng thư mục dự án, vì toán tử shell và `cd` đều bị chặn |
| `GitTools`\* | `GitStatus(repoPath)`, `GitDiff(repoPath)` | trạng thái / diff --stat của MỘT repo |
| | `CreateBranch(repoPath, branchName, baseBranch)` | Tạo + checkout nhánh |
| | `GitCommit(repoPath, message)`, `PushBranch(repoPath, branchName)` | Commit / push |
| | `OpenPullRequest(repoPath, branchName, title, body)` | Push + tạo PR thật (có token) hoặc trả link compare — gọi **một lần cho mỗi repo** |

\* **`repoPath` là tham số BẮT BUỘC** (không có giá trị mặc định, nên thiếu nó thì `ToolArgumentValidator`
từ chối lời gọi): đường dẫn tương đối tới gốc một repo trong workspace, lấy từ khối "CÁC REPO PHẢI BÀN
GIAO" mà prompt bước Pull Request mang theo. Trước đây mọi lệnh git chạy ở **gốc workspace** — nơi không
bao giờ có repo — nên bước bàn giao chỉ nhận về `fatal: not a git repository`; tệ hơn, nếu
`AgentWorkspace:RootPath` vô tình nằm trong một repo khác thì agent commit vào NHẦM repo. Một mặc định
"gốc workspace" chỉ làm lỗi đó quay lại lặng lẽ. Danh sách repo và cách chúng được dựng:
[workspace-and-poc.md](workspace-and-poc.md#repo-đích-của-dự-án).

### Tool mặc định theo vai

Gán trong `DbInitializer.AssignDefaultToolsAsync`:

| Vai | Tools |
|---|---|
| BA | ListFiles, ReadFile, WriteFile, SearchFiles, SearchInFiles |
| Tech Lead | ListFiles, ReadFile, WriteFile, SearchFiles, SearchInFiles, GitDiff, GitStatus |
| Developer | Tất cả Workspace + POC tools (gồm SearchFiles/SearchInFiles), RunCommand, GitStatus, GitCommit, CreateBranch, PushBranch, OpenPullRequest |
| Tester | ListFiles, ReadFile, WriteFile, SearchFiles, SearchInFiles, RunCommand |
| UI/UX | WriteFile, ReadFile, ListFiles |

Bảng này khai ở **`DbInitializer.DefaultToolsByRole`** — nguồn DUY NHẤT cho cả hai đường cấp tool:

- **Lần seed đầu** (`AssignDefaultToolsAsync`) — DB rỗng.
- **Cấp bù khi có tool MỚI** (`GrantNewToolsAsync`) — `SyncToolDefinitionsAsync` trả về tên các tool
  *lần đầu xuất hiện* trong bảng định nghĩa, và chỉ chúng mới được cấp. Trước đây phần gán chỉ chạy ở
  lần seed agent đầu tiên, nên thêm một tool vào code chỉ có tác dụng trên **máy cài mới**: mọi môi
  trường đang chạy nhận được định nghĩa tool nhưng không vai nào được cấp, và triệu chứng là agent im
  lặng không bao giờ gọi nó. "Lần đầu xuất hiện" là mốc duy nhất an toàn để tự cấp — admin bỏ tick ở
  màn Agents thì lần khởi động sau tool không còn mới nữa nên không bao giờ bị cấp lại.

### `SearchInFiles`: tìm theo NỘI DUNG, không phải theo tên file

`SearchFiles` chỉ khớp **đường dẫn**, nên câu hỏi thường gặp nhất ở mọi bước đụng code có sẵn — *"chỗ
nào đang dùng cái này"* — trước đây không có tool nào trả lời được: agent phải `ListFiles` rồi `ReadFile`
mò từng file, đốt ngân sách bước trước khi viết được dòng code đầu tiên. Điều đó đau nhất ở đúng hai
chỗ đắt nhất: dự án khung Bosch (code THÊM vào một skeleton thật đã clone sẵn) và các vòng sửa lỗi
(BugFix/BuildFix sửa chính code vòng trước).

Bốn trần của một lượt tìm, mỗi trần một lý do: bỏ file > 1MB (một bundle `.min.js` gần như không bao giờ
là chỗ cần sửa), quét tối đa 5000 file, tối đa 10 dòng khớp **mỗi file** (một từ khoá phổ biến khớp 400
dòng trong một file sẽ đẩy mọi file khác ra khỏi kết quả — mà chính bề RỘNG mới là thứ agent cần), và
cắt mỗi dòng ở 240 ký tự. File nhị phân nhận ra bằng byte NUL trong khối đầu (heuristic của grep) thay vì
nuôi một danh sách đuôi file — danh sách đó luôn thiếu đúng đuôi của dự án kế tiếp. `pathFilter` đi qua
đúng `GetSafeFullPath` như mọi tool file, escape thì trả lỗi dạng chuỗi chứ không ném.

### `ReplaceInFile`: khớp nhiều chỗ là TỪ CHỐI, không phải thay hết

Bản cũ dùng thẳng `string.Replace`, nên một lời gọi nhắm vào MỘT chỗ lại sửa luôn mọi chỗ giống hệt —
và tool vẫn trả `File updated`. Trên code thật (một hàm trong file vài trăm dòng) đó là hỏng dữ liệu
trong im lặng: build vẫn có thể xanh, chỗ sai chỉ lộ ra ở một tính năng không ai chạy. Nay `oldText`
phải khớp **đúng một chỗ**; khớp nhiều thì **không ghi gì cả** và observation nói rõ số chỗ khớp để
model tự thêm ngữ cảnh rồi gọi lại — cùng luật với `ToolArgumentValidator`: thà một observation bắt gọi
lại còn hơn một lần ghi sai. `replaceAll=true` là đường thoát TƯỜNG MINH cho ca thật sự muốn đổi mọi
lần xuất hiện. `oldText` rỗng bị chặn riêng: chuỗi rỗng khớp ở mọi vị trí nên `string.Replace` sẽ chèn
text mới vào giữa từng ký tự của file.

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
