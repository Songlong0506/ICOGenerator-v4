# SPIKE — Thay `AgentRunService` (đường native) bằng Microsoft Agent Framework

Mục tiêu: ĐO thực tế việc dùng **Microsoft Agent Framework (MAF 1.0, `ChatClientAgent`)** thay vòng lặp
agent tự xây thì **giảm được bao nhiêu dòng** và **giữ/mất hành vi gì** — sau một feature-flag, không
đụng đường mặc định.

> ⚠️ **Chưa compile được trong môi trường này** (không có .NET SDK, không restore được NuGet). Phần
> scaffolding (interface, flag, DI, delegate fallback, middleware logging) là C# chuẩn. Riêng file
> `ChatClientAgentRunService.cs` dùng API MAF nên **một số tên type/method cần đối chiếu lại với package
> khi build** (đã đánh dấu trong code). Đây là bản spike để đo & quyết định, không phải code production.

---

## 1. TL;DR

| Câu hỏi | Kết quả đo |
|---|---|
| MAF xoá được bao nhiêu code vòng lặp? | Đường native trong `AgentRunService` (**~176 dòng**) co lại còn **~40 dòng** orchestration thật. |
| Vậy có "ít code hơn" không? | **Gần như HOÀ** nếu muốn giữ đủ hành vi: phải thêm lại ~55 dòng middleware logging + còn **nợ 3 hành vi** (xem §5). |
| Lợi ích thật nằm ở đâu? | Không phải số dòng — mà ở chỗ **không còn tự sở hữu** tính đúng đắn của vòng lặp/dispatch/ghép message. |
| Có nên migrate toàn bộ ngay? | **Chưa.** Pilot đường native sau flag, giữ đường fallback + pipeline DB như cũ. |

**Kết luận một câu:** MAF làm phần *điều phối* sạch hơn rõ rệt, nhưng để **giữ parity** thì tổng dòng
gần như không giảm — giá trị là *offload tính đúng đắn cho framework*, đổi lại phải re-home vài hành vi
bespoke vào middleware và chấp nhận vài khoảng trống cần lấp trước khi production.

---

## 2. Spike này thay đổi gì

**Thêm mới**
- `Services/Agents/IAgentRunService.cs` — seam để hoán đổi engine (15 dòng).
- `Services/Agents/ChatClientAgentRunService.cs` — bản MAF cho đường native (168 dòng, *nhiều comment ⚠️GAP*).
- `Services/Agents/CallLoggingChatClient.cs` — middleware ghi call-log mỗi lời gọi model (77 dòng).

**Sửa (đều an toàn, không đổi hành vi mặc định)**
- `Services/Agents/AgentRunService.cs` — `: IAgentRunService` (giữ nguyên toàn bộ logic + hằng `MaxStepsReachedResult`).
- `Services/Workflows/AgentTaskWorker.cs` — resolve `IAgentRunService` thay vì class cụ thể.
- `Extensions/ApplicationServiceCollectionExtensions.cs` — `AddAgentRuntime` chọn impl theo flag (sau `#if USE_MAF_SPIKE`).
- `ICOGenerator.csproj` — `PackageReference Microsoft.Agents.AI` **có điều kiện** (`-p:EnableMafSpike=true`).
- `appsettings.json` — thêm `Llm:AgentRuntime:UseAgentFramework` (mặc định `false`).

**Quan trọng:** không có cờ build → project **không** phụ thuộc MAF, build & chạy y hệt hôm nay.

---

## 3. Bật/tắt

```bash
# Build thường (mặc định): KHÔNG cần MAF, hành vi không đổi
dotnet build

# Bật spike: kéo Microsoft.Agents.AI + định nghĩa USE_MAF_SPIKE
dotnet build -p:EnableMafSpike=true
```
Rồi đặt cấu hình (appsettings hoặc env `Llm__AgentRuntime__UseAgentFramework=true`):
```json
"Llm": { "AgentRuntime": { "UseAgentFramework": true } }
```
Chạy một Delivery Workflow và so sánh call-log + hành vi với đường cũ.

---

## 4. Đo số dòng

### Phần MAF xoá được (đường native của `AgentRunService`)
| Khối | Dòng |
|---|---|
| `RunWithNativeToolsAsync` (vòng for, nối message, dispatch tool, ghép `FunctionResultContent`) | 149 |
| `ToJsonArgs` (helper chỉ native dùng) | 14 |
| `DescribeMissingArguments` (helper chỉ native dùng) | 13 |
| **Tổng có thể xoá** | **~176** |

> `DescribeToolArgs` và `SaveConversation` **không** tính vào đây vì còn dùng chung với đường fallback.
> Đường fallback prompt-based (`RunWithPromptProtocolAsync`, ~150 dòng) **ở lại nguyên** — MAF không thay nó.

### Phần phải thêm để làm bằng MAF
| File | Dòng thô | Code thật (ước tính, bỏ comment) |
|---|---|---|
| `ChatClientAgentRunService.cs` | 168 | ~65 (trong đó orchestration MAF chỉ **~40**) |
| `CallLoggingChatClient.cs` (re-home logging) | 77 | ~50 |
| `IAgentRunService.cs` | 15 | ~7 |
| **Tổng** | **260** | **~120** |

File spike cố tình **nhiều comment ⚠️GAP** để ghi lại hành vi còn nợ; production sẽ gọn hơn ~40 dòng.

### Đọc kết quả
- Riêng *điều phối*: **176 → ~40 dòng** (giảm ~77%). Đây là phần MAF thắng đẹp.
- Nhưng để **giữ parity**: phải thêm ~55 dòng middleware logging + DI/fallback, và còn nợ 3 hành vi (§5)
  mà nếu cài lại sẽ thêm ~60–80 dòng nữa.
- **Net cho parity đầy đủ ≈ hoà** (xoá ~176, thêm ~120–200 tuỳ mức parity). Giảm dòng **không phải** lý do
  chính để đổi; *bớt phải tự bảo trì vòng lặp* mới là.

---

## 5. Bảng giữ/mất hành vi

| Hành vi (đường native hiện tại) | MAF | Ghi chú |
|---|---|---|
| Vòng ReAct think→tool→observe | ✅ native | `ChatClientAgent` tự chạy; xoá ~150 dòng |
| Tự gọi tool qua `AIFunctionFactory.Create(method, instance)` | ✅ native | **Cùng** reflection target như cũ |
| Stream token live (`onToken`) | ✅ native | Từ `RunStreamingAsync` updates |
| Trần số vòng (`maxSteps × 3`) | ✅ native | `FunctionInvokingChatClient.MaximumIterationsPerRequest` |
| Lưu hội thoại cuối (`AgentConversation`) | ✅ giữ | Vài dòng, bê nguyên |
| Fallback model không native (JSON-action) | ✅ giữ | Delegate sang `AgentRunService` |
| **Per-step call-log vào DB** (`IModelCallLogger`) | ⚠️ **re-home** | `CallLoggingChatClient` middleware (~55 dòng) |
| Sự kiện `onProgress` tool/observation | ⚠️ **kém hơn** | Suy ra từ `update.Contents`; mất preview đối số & thông điệp "thiếu đối số" |
| **Khôi phục tool-call thiếu/đứt đối số** (`ToolArgumentValidator`, cờ `Truncated`) | ❌ **nợ** | MAF auto-invoke → đúng cái bug *xoá sạch dữ liệu âm thầm* mà code cũ chống. Re-home: function-invocation middleware/bọc AIFunction (~30–40 dòng) |
| **Salvage** (cạn budget vẫn chốt kết quả một phần) | ❌ **nợ** | MAF dừng ở max-iterations. Re-home: chạy thêm 1 lượt no-tool tóm tắt (~15–20 dòng). **Nếu không có, `AgentTaskWorker` có thể đánh Completed nhầm** |
| **Per-call timeout** (deadline mỗi call) | ❌ **nợ** | `OpenAIChatClientFactory` đặt `NetworkTimeout=Infinite` vì `LlmClient` vốn giữ deadline; đường MAF bỏ qua `LlmClient` → **không còn timeout** → stream treo = treo cả run. Re-home: timeout middleware (~10 dòng) |
| Clamp `MaxOutputTokens` theo `ContextWindow` (`ResolveMaxTokens`) | ❌ **nợ** | Model context nhỏ có thể tràn. Set `MaxOutputTokens` (~5 dòng) |
| "Build succeeded → dừng sớm" / `stopWhen` | ❌ **nợ** | `stopWhen` hiện **đã null** ở caller duy nhất → ít tốn. "Build succeeded" cần function-result middleware |

### Một gap đáng chú ý: bỏ qua `DynamicToolInvoker`
MAF gọi thẳng `AIFunction` → **không đi qua** `DynamicToolInvoker`, nên mất:
- per-tool **execution log** (`IToolExecutionLogger.LogInvocation/LogResult`), và
- check `ToolPolicyService` (`Definition.IsActive`).

**An toàn lệnh KHÔNG mất** (check `IsAllowed`/shell-operator nằm trong `CommandTools`, MAF vẫn gọi vào đó).
Muốn giữ 2 thứ trên: bọc mỗi tool thành `AIFunctionFactory.Create(args => _invoker.InvokeAsync(descriptor, args), name, description)` thay vì `Create(method, instance)`.

---

## 6. Cảnh báo

1. **Không build được ở đây** → cần một lượt `dotnet build -p:EnableMafSpike=true` để chốt tên API MAF
   (`ChatClientAgentOptions`, `RunStreamingAsync`, `ToAgentRunResponse`, `UseFunctionInvocation(configure:…)`,
   thứ tự builder logging vs function-invocation).
2. **Version package** `Microsoft.Agents.AI 1.0.0` là phỏng đoán theo mốc GA 02/04/2026 — chỉnh cho khớp.
3. Spike **chỉ** thay đường native. Fallback + pipeline DB + cổng duyệt người + SSE **giữ nguyên**.

---

## 7. Khuyến nghị

- **Không big-bang.** Lợi ích dòng-code ≈ hoà; rủi ro nằm ở 3 hành vi đang "nợ" (nhất là khôi phục
  truncation và salvage — đều là fix tính-đúng-đắn đã đổ mồ hôi).
- Nếu theo đuổi MAF: làm đúng thứ tự — (1) compile spike, (2) re-home logging (xong), (3) re-home
  truncation-recovery + salvage + per-call timeout vào middleware, (4) bọc tool qua `DynamicToolInvoker`
  để giữ policy/log, (5) chạy A/B một workflow run, (6) mới cân nhắc xoá `RunWithNativeToolsAsync`.
- **Giữ nguyên** đường fallback và toàn bộ `Services/Workflows` (pipeline durable đã rất sạch).

**Phán quyết:** `ChatClientAgent` đáng dùng cho *đường native* vì trả phần điều phối về cho framework
có người bảo trì; nhưng đo cho thấy đây là đổi *"bớt phải tự lo"* lấy *"thêm vài middleware + nợ hành vi"*,
**không** phải một cú giảm dòng lớn. Quyết theo khẩu vị: muốn bớt tự bảo trì vòng lặp thì xúc tiến theo §7;
muốn tối giản phụ thuộc thì giữ nguyên.
