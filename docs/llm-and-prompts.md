# Tầng LLM & hệ thống Prompt

## Tầng LLM

### Đường đi của một lời gọi model

```
LlmClient / AgentRunService
  └► IChatClientFactory (OpenAIChatClientFactory) — dựng IChatClient theo AiModel
       ├► HttpClient "direct"  (UseProxy=false)        — cho endpoint localhost
       ├► HttpClient "proxied" (Llm:Proxy — mặc định tắt trong appsettings) — khi ngồi sau proxy công ty
       │     cả hai: Timeout = Infinite (deadline per-call do CancellationToken lo)
       │     + LlmRequestCompatibilityHandler (chèn field "thinking" cho endpoint tương thích; với OpenAI chính thức thì bỏ "thinking" và bỏ "temperature" cho reasoning model o-series/gpt-5)
       └► ChatClientBuilder compose ModelCallLoggingChatClient (middleware chung):
             deadline • trần completion-token (MaxOutputTokenResolver + TokenEstimator)
             • map lỗi API/timeout thành LlmCallResult • ghi AgentModelCallLogs • progress
```

- **`ILlmClient.ChatAsync`** — đường chat thuần (BA). **`ChatStructuredAsync<T>`** — xin API ép JSON, opt-in theo từng model (xem [Structured output](#structured-output-cho-các-lời-gọi-ba-opt-in-3-mức)).
- **`LlmCost`** tính chi phí = token × đơn giá model — cùng công thức cho trang Usage và Budget guard.
- **`IBudgetGuard`** kiểm tra **trước mỗi lời gọi** (cả agent lẫn BA chat): chạm trần (`Budget:*`) ⇒ từ chối gọi, ném `BudgetExceededException` với lý do.
- **`JsonExtractor`/`JsonDefaults`** — tiện ích bóc JSON từ trả lời văn xuôi.

### Thêm một model mới

Màn hình **AI Models** → Create: điền `Name`, `Provider`, `ModelId`, `Endpoint` (base URL OpenAI-compatible), `ApiKey`, `ContextWindow`, đơn giá (0 nếu tự host). Model gán cho agent nào là do màn **Agents** quyết định. Không cần đụng code.

Modal Add/Edit có nút **Test Connection**: gọi thử một request chat cực nhỏ (prompt `"ping"`, chặn ở 16 token đầu ra) tới endpoint đang gõ và hiện ngay kết quả (OK + thời gian phản hồi, hoặc lỗi kèm status/nguyên nhân) — không cần lưu model rồi đi chạy agent mới biết cấu hình sai. Lời gọi thử KHÔNG ghi call log, không tính vào budget; trên form Edit để trống `ApiKey` thì nó dùng key đã lưu. Deadline riêng: `Llm:TestConnectionTimeoutSeconds` (mặc định 30s).

Lời gọi thử đi đúng đường dây thật, **kể cả khâu chọn proxy** — nên khi bật `Llm:Proxy` mà proxy chết thì lỗi hiện ra không phải lỗi của endpoint. `ModelConnectionTester` tách riêng hai trường hợp đó, vì triệu chứng giống hệt nhau còn việc phải làm thì ngược nhau:

| Lỗi | Câu hiện trong modal |
|---|---|
| Proxy từ chối mở tunnel (503/407) — nhận theo `HttpRequestError.ProxyTunnelError`, dò cả cây exception vì nó nằm dưới lớp bọc retry | gọi tên proxy và nói endpoint có thể vẫn khỏe (kèm gợi ý `Llm:Proxy:Enabled=false`) |
| Lỗi kết nối trơ (proxy tắt hẳn thì không phân biệt được với endpoint chết) | câu "kiểm tra endpoint" như cũ, **cộng** một dòng nói request này đi qua proxy nào |
| Endpoint local, hoặc proxy đang tắt | câu "kiểm tra endpoint" trần — không nhắc proxy, vì nhắc là chỉ sai chỗ |

Luật "endpoint nào đi thẳng, endpoint nào qua proxy" chỉ được viết một lần ở `OpenAIChatClientFactory.IsLocalEndpoint` và cả hai bên cùng hỏi nó.

### Structured output cho các lời gọi BA (opt-in, 3 mức)
Các lời gọi của BA trả JSON (soạn 5 tài liệu, cổng kiểm tra đầy đủ, gợi ý chat) có thể xin API ép định dạng
JSON thay vì chỉ nhắc model trả JSON rồi parse văn xuôi. `ILlmClient.ChatStructuredAsync<T>` lo việc này.

Mức xin được chọn **theo từng model** qua `AiModel.StructuredOutputMode` (dropdown ở trang quản trị Models,
lưu DB dạng chuỗi). Đây **không phải cờ bật/tắt** vì `response_format` có hai tầng năng lực khác nhau và có
endpoint chỉ đỡ được tầng dưới — DeepSeek nhận `json_object` nhưng trả 400 *"This response_format type is
unavailable now"* với `json_schema`:

| Mức | Gửi đi | Đường thực thi | Dành cho |
|---|---|---|---|
| `None` (mặc định) | không gửi `response_format` | streaming | server local/model lạ |
| `JsonObject` | `{"type":"json_object"}` | **streaming** (giữ `onToken`) | DeepSeek, đa số server OpenAI-compatible |
| `JsonSchema` | schema sinh từ `T` (`GetResponseAsync<T>`) | non-streaming (**`onToken` bị bỏ qua**) | endpoint OpenAI thật |

Ba lưới đỡ, để một cấu hình sai không bao giờ làm chết tính năng:
- **JSON không khớp kiểu mong đợi** ⇒ trả `value = null`, caller fallback về parser tay
  (`RequirementResponseParser`/`BAChatReplyParser`). Reply có JSON hợp lệ nhưng **không trùng field nào** với
  `T` cũng bị coi là không khớp — nếu không, `System.Text.Json` sẽ dựng ra một object toàn giá trị mặc định,
  trông như parse thành công và cướp mất lượt của parser tay.
- **Endpoint từ chối chính `response_format`** ⇒ gọi lại **một lần** bằng đường text thuần + ghi cảnh báo chỉ
  đúng chỗ cần sửa (hạ mức ở trang Models). Không có nhánh này thì lỗi 400 bị middleware nuốt thành
  `LlmCallResult` thất bại, và các caller `if (!callResult.IsSuccess) return empty` sẽ tắt cổng trong im lặng.
- **Prompt không chứa chữ "json"** ⇒ bỏ `response_format` cho lượt đó (JSON mode bị cả DeepSeek lẫn OpenAI từ
  chối nếu thiếu), khỏi tốn một vòng gọi chắc chắn 400.

### Giải phẫu `Services/Llm` (một trách nhiệm một file)
Có **ba** đường gọi model — agent (`AgentRunService`), chat/structured của BA (`LlmClient`), eval
(`EvalRunnerService`) — và tất cả chạy qua **cùng một** chồng hạ tầng. Thư mục được cắt theo trách
nhiệm để thêm một thứ mới chỉ phải sửa đúng một file:

| File | Trách nhiệm | Sửa khi… |
|---|---|---|
| `ILlmClient` / `LlmClient` | Điều phối một lượt hỏi–đáp (không có vòng lặp tool): chọn mức structured output, thử lại khi endpoint từ chối | thêm một mức/chiến lược gọi mới |
| `ModelCallPipeline` | Lắp `IChatClient` theo model + middleware, **giữ lại `LlmCallResult`** middleware dựng ra | (hiếm) |
| `ModelCallLoggingChatClient` | Middleware cắt ngang: budget, deadline, trần token, dựng result, map lỗi, log DB, progress | thêm một mối quan tâm cắt ngang |
| `ModelCallOptions` | Núm vặn của middleware theo từng đường gọi (record) | thêm một núm — **không phải sửa chỗ dựng nào cả** |
| `ModelCallRequestPreview` | Dựng chuỗi JSON "request đã gửi" cho màn Call Log | đổi hiển thị call log |
| `OpenAiCompatibility` + `LlmRequestCompatibilityHandler` | Vá **request đi ra** theo từng API (thêm `thinking`, bỏ `temperature`) | thêm quirk phía request |
| `EndpointQuirks` | Nhận biết endpoint **từ chối** cái gì và sửa hội thoại để thử lại | thêm quirk phía response |
| `LlmJson` | Đọc JSON model trả về: bóc khỏi code-fence, deserialize khoan dung, không ném | (hiếm) |
| `LlmSettings` | Toàn bộ section `"Llm"` của appsettings, đọc **một lần** | thêm một khoá cấu hình |
| `IChatClientFactory` / `OpenAIChatClientFactory` | Dựng client theo `AiModel` (chọn proxy theo endpoint local/remote) | đổi nhà cung cấp/transport |
| `IModelCallLogger` / `ModelCallLogger` | Ghi một dòng call log | đổi schema log |
| `IModelConnectionTester` / `ModelConnectionTester` | Nút "Test Connection" — **không** log, **không** tính budget | đổi cách chẩn đoán lỗi cấu hình |
| `LlmCost`, `TokenEstimator`, `MaxOutputTokenResolver` | Ba phép tính thuần (USD, ước lượng token, trần output) | đổi công thức |

Hai quy ước giữ cho nó không rối lại:
- **`LlmJson` là chỗ ĐỌC JSON model trả về duy nhất.** Trước đây gần chục service tự chép "bóc JSON rồi
  `Deserialize` trong `try/catch`" nên hành vi biên (phản hồi bị cắt, JSON toàn field lạ) mỗi nơi một
  kiểu. Parser dự phòng giờ là một dòng `LlmJson.TryDeserialize<T>(raw)`.
- **`LlmSettings` là chỗ ĐỌC config `Llm:*` duy nhất.** Trước đây ba service tự đọc
  `Llm:RequestTimeoutSeconds` kèm ba hằng mặc định riêng — sửa một chỗ là lệch ngay với hai chỗ kia.

---

## Hệ thống Prompt

### Nguồn prompt & độ phân giải

Prompt gốc là file `.md` dưới `/Prompts` (copy ra output khi build). `PromptTemplateService.Get(key)` giải theo thứ tự:

1. Hỏi `IPromptOverrideProvider` (`DbPromptOverrideProvider`) — bản **active** trong bảng `PromptTemplateVersions` (sửa runtime qua **Prompt Studio**, cache IMemoryCache 30s, ghi là invalidate ngay). **Fail-open**: DB lỗi ⇒ rơi về file.
2. Không có override ⇒ nội dung file.

Nghĩa là: sửa prompt qua Prompt Studio **có hiệu lực ngay không cần deploy**, và app không bao giờ hỏng vì bảng version.

### Quy ước hình thức (chốt bằng `PromptConventionTests`)

Prompt là file `.md` không được compiler soi, nên mỗi lần thêm một bước pipeline lại có thêm một file
được chép từ file gần giống nhất rồi sửa — sau vài vòng thì mỗi file một kiểu. Bốn quy ước dưới đây được
**test chốt lại**, sai là fail build chứ không âm thầm trôi:

| Quy ước | Vì sao |
|---|---|
| Dòng đầu là `# Vai trò: <vai> — <việc>` | mở file ra biết ngay ai làm gì; 5 ngoại lệ khai báo trong `RoleHeadingExempt` kèm lý do (ba khối ngữ cảnh `organization-*`, `Shared/revision`, `Shared/tool-agent-native` — đều là khối ghép vào prompt khác chứ không phải prompt của một vai) |
| Mục đầu ra tên **duy nhất** `## ĐỊNH DẠNG TRẢ LỜI (BẮT BUỘC)` | trước đây cùng một việc mang 4 tên khác nhau (`## ĐỊNH DẠNG ĐẦU RA`, `## Yêu cầu đầu ra`, `## Đầu ra`, `## Định dạng`), so hai prompt cạnh nhau phải dịch tên mục |
| `{{input}}`/`{{persona}}` nằm dưới heading `# ĐẦU VÀO: <tên khối>` | đánh dấu rõ ranh giới CHỈ DẪN ↔ DỮ LIỆU |
| Placeholder phải có trong bản đăng ký `KnownPlaceholders` | thêm `{{x}}` mà quên `.Replace(...)` thì chuỗi `{{x}}` đi thẳng tới model: không có exception, chỉ có câu trả lời tệ hơn |

Hai điều **KHÔNG** làm khi dọn prompt:

- **Đừng đổi tên file** (kể cả bump `.vN`). `PromptKey` là khoá của `PromptTemplateVersions` và của
  `EvalScenario.PromptKey` — đổi tên là mồ côi toàn bộ lịch sử Prompt Studio và điểm eval của template đó.
  Phiên bản nội dung đã có Prompt Studio lo.
- **Đừng gộp phần trùng lặp mà chưa đọc test đi kèm.** Một số chỗ trùng là **cố ý**: khối ranh giới phạm vi
  sống ở cả `organization-scope.v1.md` lẫn `requirement-chat.v4.md` vì Prompt Studio có thể override khối
  ngữ cảnh, và vì eval chạy prompt chat một mình — `BAChatScopeConflictRuleTests` chốt cả hai bản.

Ngược lại, **hành vi sâu theo vai thuộc `instruction.md`, việc của từng bước thuộc template bước**. Trước
đây `Developer/instruction.md` chép lại nguyên giao thức dựng POC của `poc-preview.v1.md` rồi hai bản
trôi lệch nhau (bản trong instruction thiếu hẳn tầng tự kiểm runtime, `pocSelfTest`/`pocScenarios`/
`pocWorkedExamples`, và mục REGRESSIONS) — agent đọc được hai đặc tả khác nhau cho cùng một việc.

### Danh mục prompt

| File | Dùng cho |
|---|---|
| `BusinessAnalyst/requirement-chat.v4.md` | Lượt chat BA |
| `BusinessAnalyst/source-ack.v2.md` | Lượt đọc lại tài liệu nguồn (docx/xlsx/PDF/ảnh) để người dùng xác nhận; kiêm ghi `sourceNotes` cho các hình — lượt DUY NHẤT model nhìn thấy ảnh. Bốn luật chốt bằng `SourceAckReadbackRuleTests`: danh mục của cột lấy từ khối `#### Thống kê cột` chứ không suy từ dòng mẫu; **đối chiếu tài liệu với điều người dùng đã kể** (đúng file đã xin chưa / thiếu gì so với lời kể / quy mô có khớp không); "Chỗ chưa chắc" chỉ chứa thứ chỉ người dùng trả lời được; và **chọn** cột đáng hỏi (cột phân loại ít giá trị, header là mã, cột chở quy tắc người dùng đã nói, giá trị bất thường) rồi nêu dưới dạng **đề xuất cách hiểu** — không bao giờ trải cả bảng ra xin giải nghĩa từng cột. Với file bảng tính còn trả `columns`: **bảng cột** (mỗi cột một dòng, ý nghĩa ĐIỀN SẴN, tích sẵn cột nghiệp vụ) để người dùng chốt phạm vi cột trong một lượt — xem [requirement-flow.md](requirement-flow.md#bảng-cột-chốt-phạm-vi-cột-của-file-bảng-tính) |
| `BusinessAnalyst/project-domain.v1.md` | Xếp dự án vào một `domainKey` trong 13 miền nghiệp vụ cố định |
| `BusinessAnalyst/decision-log.v1.md` | Nhật ký "Điều đã chốt" — các quyết định người dùng đã nói/đã xác nhận, gộp lũy tiến. Câu tóm tắt của BA **không** phải lời người dùng: BA gộp nhiều điều vào một lượt mà người dùng chỉ đáp một vế ⇒ chỉ ghi vế đó (`SourceAckReadbackRuleTests`) — dòng ghi dư ở đây không còn cổng nào chặn, vì bước soạn tài liệu coi mọi dòng là điều đã duyệt |
| `BusinessAnalyst/interview-outlook.v1.md` | Ba danh sách "triển vọng phỏng vấn": `openQuestions` / `plannedScope` / `workedExamples` |
| `BusinessAnalyst/conflict-check.v1.md` | Cổng soát MÂU THUẪN ngay trước khi soạn tài liệu (trả `conflicts[]` kèm hai vế + câu hỏi chốt) |
| `BusinessAnalyst/product-brief.v3.md` | Sinh Product Brief (Write Requirement) |
| `BusinessAnalyst/product-brief-review.v2.md` | Vòng tự soát Product Brief |
| `BusinessAnalyst/ai-design-spec.v1.md` | Sinh AI Design Spec sau Approve — gồm mục `## 14. Acceptance Criteria` chép NGUYÊN VĂN các dòng "Hoàn thành khi: …" của Product Brief đã duyệt (`BriefAcceptanceCriteria`); `SpecBriefParityChecker` soát ba tầng màn hình/quy tắc/câu nghiệm thu và cho BA sửa một vòng nếu lệch |
| `BusinessAnalyst/uat-scenarios.v1.md` | Sinh kịch bản nghiệm thu (UAT) từ spec TRƯỚC khi dựng POC — mỗi `AC-n` phải có ≥1 kịch bản (`acRefs`), thiếu thì chạy một vòng bổ sung |
| `BusinessAnalyst/technical-docs.v1.md` | Sinh BRD/SRS/FSD/UserStories (bước 2 pipeline) |
| `BusinessAnalyst/conversation-summary.v1.md` | Gộp tóm tắt hội thoại (bộ nhớ dài hạn) |
| `BusinessAnalyst/user-memory.v1.md` | Chắt lọc hồ sơ user |
| `BusinessAnalyst/checklist-gap.v2.md` | Rút "khoảng trống checklist" sau khi sinh tài liệu — trả JSON `{items:[{text,rationale,evidence}]}`, chỉ ĐỀ XUẤT THÊM bài học mới |
| `BusinessAnalyst/poc-feedback-gap.v2.md` | Rút bài học từ ghi chú POC đã gửi Dev sửa (cùng dạng JSON như trên) |
| `BusinessAnalyst/poc-feedback-triage.v1.md` | Phân loại MỖI ghi chú POC theo đường xử lý: sửa TÀI LIỆU (`isRequirementIssue`) hay Dev vá thẳng POC |
| `BusinessAnalyst/poc-feedback-compose.v1.md` | Gom các ghi chú thuộc nhóm "tài liệu" thành MỘT tin nhắn ngôi thứ nhất gửi lại BA |
| `BusinessAnalyst/requirement-coverage.v3.md` | Cập nhật bản đồ bao phủ yêu cầu — kiêm "giám khảo" của cổng "Write Requirement" (ready suy tất định từ bản đồ, không có prompt readiness riêng) |
| `BusinessAnalyst/organization-context.v2.md` | Khung render bức tranh tổ chức |
| `BusinessAnalyst/organization-scope.v1.md` | Ranh giới phạm vi: sản phẩm chỉ phục vụ nhà máy Bosch Đồng Nai — cấm BA gợi ý phạm vi vượt nhà máy ("Toàn Bosch Việt Nam"…), cho sẵn thang phạm vi hợp lệ (orgUnit → department → toàn nhà máy). Đính vào MỌI lời gọi BA, kể cả khi `OrgUnits` còn trống. Người dùng nói "toàn công ty"/"tất cả nhân viên Bosch" ⇒ hiểu ngầm là toàn nhà máy, ghi nhận và đi tiếp; khối này là hằng số sản phẩm nên KHÔNG được chèn vào câu "mình ghi nhận…" như lời người dùng, cũng không được làm một vế của mâu thuẫn |
| `BusinessAnalyst/organization-platform.v1.md` | Nền tảng đã chốt của nhà máy: **chỉ có DUY NHẤT kênh thông báo email** — nhóm "Thông báo / nhắc nhở" chỉ hỏi *ai nhận* và *khi nào*, cấm hỏi "muốn báo qua kênh nào" và cấm gợi ý Teams/SMS/Zalo/push. Đính vào MỌI lời gọi BA như khối ranh giới phạm vi; cũng là hằng số sản phẩm nên KHÔNG được chèn "qua email" vào câu "mình ghi nhận…". Chốt bằng `BAChatNotificationChannelRuleTests` |
| `TechLead/architecture-design[-bosch].v1.md`, `TechLead/code-review.v1.md`, `Developer/poc-preview.v1.md`, `Developer/implementation[-bosch].v1.md`, `Developer/bugfix.v1.md`, `Developer/pull-request.v1.md`, `Tester/testing.v1.md` | Từng bước pipeline theo vai (`{{input}}` = nội dung theo `InputSource`); bản `-bosch` dùng khi `Project.IsUseBoschTemplate`. Bốn file thiết kế/hiện thực nhắc lại ràng buộc **thông báo chỉ có email** — pipeline KHÔNG nhận khối ngữ cảnh tổ chức, ràng buộc chỉ tới được qua tài liệu nên phải có bản của riêng chúng |
| `Shared/revision.v1.md` | Khối "Yêu cầu chỉnh sửa" nối sau prompt gốc của bước |
| `{TechLead,Developer,Tester}/instruction.md` | **System prompt theo vai** — hành vi sâu của agent (loại task nhận được, quy tắc lưu kết quả, ngân sách bước, thứ không được đụng) nằm ở đây; template task chỉ mô tả *việc của bước*. Chỉ ba vai chạy tool nên chỉ ba file: `AgentInstructionProvider` giải `{RoleKey}/instruction.md` và **fail-open về chuỗi rỗng** khi vai không có file (BA và UiUx không chạy qua agent+tool) |
| `Shared/tool-agent-native.v1.md` | Khung prompt chung cho agent chạy tool (bọc `{{instruction}}` của vai) |
| `UiUx/poc-visual-review.v1.md` | Chấm HÌNH ẢNH của POC từ ảnh chụp từng màn hình — lớp bắt lỗi mà soát mã không thấy (màn trống, layout vỡ, chữ đè, sai ngôn ngữ, tương phản kém) |
| `Eval/judge.v1.md` | LLM-judge chấm điểm eval 1–5 + đối chiếu ĐẠT/TRƯỢT từng dòng tiêu chí |
| `Eval/persona.v1.md` | Model đóng vai NGƯỜI DÙNG NGHIỆP VỤ trong scenario eval kiểu `Interview` (đo cả cuộc phỏng vấn, không chỉ một lượt) |
| `Eval/chat-review.v1.md` | Chỉ dẫn chấm đi kèm **bản xuất hội thoại** để người dùng nhờ một AI NGOÀI hệ thống rà soát buổi phỏng vấn — nhúng vào đầu `01-chat-ba.md` (xem [requirement-flow.md](requirement-flow.md#tải-trọn-gói-để-nhờ-một-ai-khác-rà-soát)) |
| `Eval/delivery-review.v1.md` | Chỉ dẫn chấm đi kèm **gói rà soát dây chuyền** (hội thoại → Product Brief → AI Design Spec → POC) — nhúng vào đầu `00-README.md` của file `.zip` tải về |
| `Design/poc-template.html` | Shell HTML của POC (sidebar/topbar/Bootstrap + engine `data-crud-*`, hai vùng marker `POC_CONTENT`/`POC_SCRIPT`) |

### Prompt Studio — sửa runtime, rollback, gắn với eval

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
