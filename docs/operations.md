# Vận hành: logging, observability, troubleshooting

## Logging tập trung & observability (Serilog + OpenTelemetry opt-in)
Toàn app ghi log qua **Serilog** thay cho logging Console mặc định của ASP.NET. Cấu hình (mức log, sink,
enrich) nằm ở section `Serilog` trong `appsettings.json` nên đổi **không cần build lại**; `Program.cs` chỉ
`builder.Host.UseSerilog(...)` đọc từ config + `Enrich.FromLogContext`, và `app.UseSerilogRequestLogging()`
ghi **một dòng tóm tắt có cấu trúc cho mỗi HTTP request** (method/path/status/thời lượng) thay cho log mặc
định dài dòng. Một **bootstrap logger** (`CreateBootstrapLogger`) bắt cả lỗi xảy ra TRƯỚC khi host dựng
xong — quan trọng vì `DbInitializer` migrate DB + seed **ngay lúc khởi động**; vì vậy toàn bộ thân
`Program.cs` nằm trong `try / catch(Log.Fatal) / finally(Log.CloseAndFlush)` để một lỗi khởi động (vd không
kết nối được SQL) thành **một log `Fatal`** rồi flush, thay vì stack trace trần ra stderr. Sink mặc định:
Console (stdout — để Docker/k8s/journald gom) + File xoay vòng theo ngày trong `Logs/` (đã `.gitignore`,
giữ 14 ngày), cộng một bản sao các dòng **Warning trở lên vào bảng `SystemLogs`** (mục dưới). Production có thể đổi Console sang JSON nén cho log aggregator (Seq/Loki/ELK) qua
`appsettings.Production.json` — **không sửa code**.

**Thông điệp log viết bằng tiếng Anh** (template của `ILogger`/`Log.*`), khác với tài liệu và comment. Console
Windows mặc định dùng code page OEM nên chữ có dấu ra thành `?`; `Program.cs` có ép
`Console.OutputEncoding = UTF8`, nhưng log còn đi qua terminal/ssh/log viewer khác mà ta không kiểm soát. Vì
cùng lý do, template log **không** đi qua `Loc.T` — nó là hằng số cố định (để Serilog gom theo template), không
phải câu chữ cho người dùng. Dữ liệu điền vào placeholder (tên dự án, nội dung người dùng) thì giữ nguyên.

Trên đó, **OpenTelemetry** (trace + metric) là **OPT-IN** qua `Otel:Enabled` (mặc định TẮT, cùng tinh thần
opt-in như `Llm:Proxy` / `Budget`): chưa bật thì `AddObservabilityServices` **không
đăng ký gì** — zero overhead, không sinh lỗi exporter. Khi bật, instrument **ASP.NET Core + HttpClient**
(nên các lời gọi LLM ra ngoài tự thành span — **dựng lại được chuỗi agent → model → tool**) và **metric
runtime/HTTP**, rồi xuất qua **OTLP** tới collector (`Otel:OtlpEndpoint`, trống ⇒ mặc định gRPC
`http://localhost:4317`). Đăng ký tập trung ở `AddObservabilityServices` trong file Extensions như mọi nhóm
DI khác.

Collector **không nhúng vào app** (giữ đúng ranh giới OTel: SDK sinh telemetry ↔ collector/backend nhận-lưu-
hiển thị, khác vòng đời, khác scale). Để dev/demo "bật là chạy", repo kèm `docker-compose.otel.yml` dựng
**.NET Aspire Dashboard** (OTLP endpoint + UI trong một image) map ra `localhost:4317` — khớp default nên chỉ
cần `docker compose -f docker-compose.otel.yml up -d` rồi `Otel:Enabled=true`. UI của dashboard ở
`http://localhost:18888`. File compose chạy dashboard **anonymous**, chỉ dùng local — production trỏ
`Otel:OtlpEndpoint` tới collector thật (Jaeger/Tempo/Grafana).

### Log Warning/Error trong DB (màn System Logs)

Để tra lỗi trên môi trường thật mà **không phải nhờ người quản lý máy chủ chép file log**, các sự kiện từ
`DatabaseLog:MinimumLevel` (mặc định `Warning`) trở lên được chép vào bảng `SystemLogs` và xem ở
**Administration → Logs**, tab **System Logs** (quyền `SystemLogView`). File log vẫn là nguồn đầy đủ — DB chỉ giữ bản sao
phần đáng chú ý, và mức của nó độc lập với `Serilog:MinimumLevel`.

| Mảnh | Việc |
|---|---|
| `Services/Logging/DatabaseLogSink` | `ILogEventSink` đăng ký DI ⇒ `Program.cs` nhặt qua `ReadFrom.Services`, **không** khai trong `Serilog:WriteTo`. Lọc mức, dựng `SystemLog` (message đã render, exception, `SourceContext`, `RequestPath`, các thuộc tính còn lại thành JSON, cắt độ dài) rồi đẩy vào hàng đợi trong RAM (trần 10.000, đầy thì bỏ dòng **cũ** nhất) |
| `Services/Logging/DatabaseLogWriter` | `BackgroundService` mỗi 2 giây rút hàng đợi, ghi theo lô 200 dòng qua `AppDbContext`; 6 giờ một lần xoá dòng cũ hơn `RetentionDays`. Lúc tắt app ghi nốt phần còn lại (tối đa 5 giây) |
| `Application/SystemLogs/GetSystemLogPageQuery` | Lọc "từ mức X trở lên" + tìm chuỗi con **phía server** trong message/logger/exception/URL, mới nhất trước |

Ba quyết định có chủ đích — đừng gỡ:

- **Không INSERT trong `Emit`.** `Emit` chạy đồng bộ trên luồng đang ghi log (request, worker); ghi DB ở đó
  thì mọi lời gọi log chậm theo DB, và DB treo thì app treo theo.
- **Chặn vòng lặp bằng `DatabaseLogSink.Suppress()`** (cờ `AsyncLocal`). Lúc ghi lô, chính EF Core cũng ghi
  log (cảnh báo truy vấn, lỗi kết nối); sự kiện sinh trong vùng `Suppress` vẫn ra file/console nhưng không
  quay lại hàng đợi DB. Thiếu chốt này, một lần ghi hỏng sinh một dòng Error, dòng đó lại ghi hỏng… vô hạn.
- **Ghi hỏng thì bỏ lô, không thử lại.** DB mất kết nối thì log vẫn đủ trong file; giữ lại trong RAM chờ
  DB sống lại chỉ biến một sự cố thành hai. Lỗi ghi được báo **một lần** cho mỗi đợt hỏng liên tiếp.

Giới hạn cần biết: log trước khi host dựng xong (bootstrap logger — lỗi đọc config, lỗi migrate trong
`DbInitializer`) **không** vào DB, vì lúc đó DB có thể chính là thứ đang hỏng — những lỗi này chỉ có ở
console/file. `DatabaseLog:Enabled=false` thì không đăng ký sink/worker nào; tab System Logs vẫn mở được
và nói rõ đang tắt.

### Log nghiệp vụ riêng (ngoài Serilog)

Ba loại log sống trong DB chứ không trong file log, vì chúng là dữ liệu để tra cứu trên UI chứ không
phải dòng chảy vận hành:

| Bảng | Ghi gì | Xem ở đâu |
|---|---|---|
| `AgentModelCallLogs` | Mỗi lời gọi model: request/response, token, thời lượng, `Purpose`. Lượt agent gọi tool thì phần chữ thường rỗng, nên Response ghi thêm từng lời gọi tool kèm tham số (`ToolCallLog`, mỗi tham số tối đa 20.000 ký tự), cùng trọn phần suy luận ẩn (không cắt) khi endpoint trả ra — thiếu nó thì các bước chỉ gọi tool hiện Response trống như thể không làm gì | Popup **AI Call Logs** ở Agent Dashboard (cột Tokens hiện tổng, một thanh xếp chồng bốn phần rời nhau — cache read / cache write / uncached input / output, cộng lại đúng bằng tổng — và dòng `…% cached` khi có phần prompt đọc từ cache, hoặc `Cache written` khi chỉ có phần vừa ghi; hover hay focus ô mở popover liệt kê từng phần kèm số và %, phần input tính % trên prompt nên `Cache read` chính là tỉ lệ trúng cache; tab **Usage** của khối chi tiết liệt kê prompt/cached/cache write/uncached/completion/total, vì tab Response chỉ có nội dung model trả về); là nguồn của trang Usage & Delivery Quality |
| — (`ToolExecutionLogger`) | Mỗi lần agent gọi tool | Log ứng dụng |
| `AuditLogs` | Thay đổi cấu hình (Settings/Roles/Agent/Model/Prompt) kèm actor + before/after JSON | Màn **Logs**, tab **Audit Log** |

### Mang một lời gọi model đi hỏi chỗ khác

Mỗi dòng trong bảng popup AI Call Logs có nút `⬇` tải lời gọi của dòng đó ra file `.md`
(`GET CallLogExport`); bản xuất cả cụm lời gọi cùng lượt là `GET CallLogTurnExport` — endpoint không có
nút bấm, gọi thẳng bằng URL kèm `?id=`. Cả hai đều dựng bởi `ModelCallLogMarkdown`. Đây là đường để mang
trọn ngữ cảnh một lượt gọi ra ngoài — dán cho một AI khác soi khi response lệch, hoặc đính kèm vào một
issue. Khối **Model Invocation Detail** (bung ra ngay dưới dòng khi bấm mũi tên ở cột đầu) chỉ để đọc
tại chỗ: request/response/error theo tab, không có nút tải. Khung JSON trong khối cố tình thấp để một
dòng mở ra không nuốt trọn popup, nên mép phải dải tab có hai nút biểu tượng: **Xem dạng dễ đọc** (ở tab
Request và Response, đổi qua lại với JSON gốc) và **Phóng to toàn màn hình** (mọi tab — khối tách thành
lớp phủ chiếm trọn viewport để đọc request dài mà không phải lăn chuột; bấm lại hoặc nhấn `Esc` để thu).

Dạng dễ đọc giải đúng một vấn đề ở cả hai tab: ở JSON gốc mọi xuống dòng là `\n` và mọi dấu nháy bị
escape. Tab Request tách `messages` thành từng khối theo VAI; tab Response thì phản hồi **không có cấu
trúc cố định** — tuỳ prompt mà model trả JSON (structured output, có khi bọc trong hàng rào ```` ```json ````),
trả Markdown, hoặc trả văn xuôi — nên nó bóc hàng rào rồi dựng theo field nếu parse được, không thì hiện
nguyên văn với xuống dòng thật. Cờ "đang xem dạng dễ đọc" giữ RIÊNG từng tab. Tab Error không có nút này:
nội dung đã là chuỗi lỗi thuần.

Hai luật làm nên "dễ đọc" ở đây, cả hai đều rút ra từ ca hỏng thật:

- **Không bao giờ `JSON.stringify` một nhánh con rồi đổ ra màn hình.** Field của response hay chở nguyên
  một tài liệu (`aiDesignSpec.content`, `productBrief.content`), và stringify lại thì mọi xuống dòng quay
  về `\n` literal — đúng bức tường chữ mà nút này sinh ra để dỡ. Cây JSON vì thế được đi tiếp xuống từng
  nhánh (`renderReadableValue`), chỉ in JSON khi đã quá sâu (`RD_MAX_DEPTH`).
- **Chuỗi nhiều dòng được dựng lại theo một TẬP CON của Markdown** (`renderMarkdownLite`): tiêu đề, gạch
  đầu dòng, danh sách đánh số, trích dẫn, đường kẻ ngang, khối ```` ``` ````, `` `code` ``, `**đậm**`.
  Prompt hệ thống và tài liệu model sinh ra đều viết bằng Markdown, nên đây là thứ biến khối chữ thành
  tài liệu có mục lục nhìn thấy được. Cố ý **không** kéo thư viện markdown về cho một khối đọc-tại-chỗ:
  phần cú pháp không nhận ra giữ nguyên văn, không bao giờ bị nuốt mất. Số của danh sách đánh số in đúng
  số model viết (không để `<ol>` đánh lại) vì log là chỗ truy vết. Mọi nội dung escape HTML **trước** rồi
  mới chèn thẻ — response có thể chở nguyên trang POC demo.

Bản xuất là Markdown chứ không phải `RequestJson`, vì thứ cần đọc nằm trong `messages`: ở dạng JSON thì
mọi xuống dòng của prompt là `\n` và mọi dấu nháy bị escape, người lẫn model đều phải giải mã trước khi
đọc được câu đầu tiên. Ở đây mỗi message là một khối riêng ghi rõ VAI và ĐỘ DÀI, nên câu hỏi *"lệch là do
prompt hay do context"* nhìn thấy được từ mục lục. Nội dung **không bị cắt** — cắt một khối context ở giữa
là bỏ đi đúng thứ đang cần soi. Ảnh gửi kèm chỉ được nêu tên (bytes ở trên đĩa, xem
["Ảnh trong call log"](requirement-flow.md#tài-liệu-nguồn-ảnh-và-call-log)).

**Bản xuất cả cụm tồn tại vì một thao tác người dùng tốn vài lượt gọi model nối nhau**, và output của
lượt này là input của lượt kia — một lượt chat BA gồm bản đồ bao phủ + hồ sơ user + tóm tắt hội thoại →
lượt trả lời → chắt lọc hậu kỳ (xem [requirement-flow.md](requirement-flow.md#các-cơ-chế-trí-nhớ)). Khi
lượt trả lời sai, nguyên nhân thường nằm ở một lời gọi KHÁC trong cùng cụm.

**Cụm được SUY RA, không được lưu sẵn**: không có cột định danh lượt trên `AgentModelCallLogs`, nên ranh
giới dựng từ *cùng dự án + cùng agent + cùng `WorkflowRunId`* và khoảng nghỉ giữa hai lời gọi liền nhau
≤ 30 giây (`ExportCallLogTurnQuery`). Ba điều cần biết về cách suy đó:

- Khoảng nghỉ đo từ lúc lời gọi trước **kết thúc** tới lúc lời gọi sau **bắt đầu** (`CreatedAt - DurationMs`),
  không phải giữa hai mốc `CreatedAt`: các bước chuẩn bị chạy song song, và một lời gọi dài 12 giây sẽ tự
  tạo ra một "khoảng trống" 12 giây không có thật, đủ để cắt cụm ngay giữa lượt.
- Ràng buộc agent + `WorkflowRunId` là thứ giữ cho cụm không nuốt việc của người khác: pipeline nền chạy
  song song với khung chat.
- Cụm **có thể gộp dư** đuôi của lượt trước (người dùng bấm chip trả lời ngay khi lượt chắt lọc hậu kỳ còn
  đang chạy). Ngưỡng cố ý nới về phía gộp dư — dư thì người đọc bỏ qua được, thiếu thì họ không biết mà đi
  tìm. Trần 20 lời gọi chặn cỡ file khi một phiên bấm chip liên tục làm các cụm dính vào nhau.

Về dữ liệu: `RequestJson`/`ResponseText` được **mã hóa at-rest** vì chúng chở lại toàn bộ transcript dự án
(xem [data-model.md](data-model.md)). Bản tải về **giải mã trọn gói** vào một file rời, nên hai endpoint
này đi qua đúng cổng quyền của màn xem chi tiết (`ProjectResource.CallLog`) — không có đường tắt nào rộng
hơn — và file tải về phải được đối xử như chính nội dung nghiệp vụ của dự án.

---

## Kiểm tra môi trường triển khai (nút Test Environment)

Trang **Settings** có nút **Test Environment** (quyền `SettingsManage`): chạy một lượt kiểm tra mọi thứ app cần
trên máy đang chạy nó và hiện kết quả trong một **popup** dạng bảng Pass / Warning / Fail / Info (có nút *Run Again*), mỗi dòng không đạt kèm **cách sửa** và nguyên văn
lỗi. Dùng nó ngay sau khi deploy và mỗi khi một tính năng "tự nhiên" không chạy trên server mà máy dev vẫn chạy.

| Nhóm | Kiểm gì | Lỗi hay gặp trên Windows/IIS |
|---|---|---|
| Host | Tài khoản chạy app, môi trường, runtime, content root (Info) | Mọi dòng phía dưới tính theo **danh tính application pool**, không phải tài khoản admin đang RDP |
| Database | Provider, mở connection thật, migration còn treo | `Trusted_Connection` ⇒ phải tạo login SQL cho `IIS APPPOOL\<pool>` |
| Secrets | Các bí mật trong `SecretsCheck.SecretKeys`: có không, **lấy từ provider nào**; ApiKey của model đang bật có giải mã được không | Giá trị còn lấy từ `appsettings.json` (đã commit) ⇒ Warning; nạp qua `environmentVariables` của `web.config` |
| Files & folders | Ghi thử file tạm ở `AgentWorkspace:RootPath` và `Logs/`, dung lượng đĩa, `Templates/` + `Prompts/` có trong bản publish | Pool thiếu quyền Modify trên thư mục workspace |
| Sign-in & HTTPS | Request có qua HTTPS (cookie Secure-only ngoài Development), chế độ `LocalRole`/SSO, discovery của IdentityServer | Site chỉ có binding HTTP ⇒ đăng nhập xong bị đá về Login |
| AI models | Proxy LLM (Info) + gọi thử **từng** model đang bật qua `IModelConnectionTester` (cùng đường với nút Test Connection — không ghi call log, không tính budget) | Proxy công ty chặn; ApiKey không giải mã được sau khi đổi khóa |
| POC browser | Khởi động Chromium headless bằng **đúng instance** của `PlaywrightPocRuntimeChecker` (`IPocBrowserProbe`), đường cache | Pool tắt *Load User Profile* ⇒ cache rơi vào `systemprofile`; lỗi launch được cache theo process ⇒ sửa xong phải Recycle pool |
| Command-line tools | `git` (≥ 2.31, `user.name/email`), .NET SDK 8+, `node`, `npm`, Claude Code CLI (chỉ khi có model `claude-code://`) | Cài Node/Git "cho riêng tôi" ⇒ PATH của pool không thấy; server chỉ có Hosting Bundle, không có SDK |
| Organization sync | Xin token employee-api (khi bật), bảng `OrgUnits` có dữ liệu chưa | Máy chủ không ra được mạng Bosch |

Thiết kế:

- Mỗi nhóm là một class `IEnvironmentCheck` ở `Services/Diagnostics/Checks/`; `EnvironmentDiagnostics` chạy
  tất cả **song song, mỗi nhóm một DI scope** (vài nhóm đọc `AppDbContext`), mỗi nhóm có trần thời gian riêng —
  nhóm treo hay ném lỗi thành một dòng Fail, không kéo cả bảng theo. Thứ tự đăng ký DI là thứ tự hiển thị.
- Lượt kiểm **không đổi trạng thái app**, trừ hai tác dụng phụ cố ý: gọi thử mỗi model một lời "ping" (vài token)
  và — trên máy chưa có Chromium — tự tải browser như vòng audit POC đầu tiên (lần bấm đầu có thể mất ~1 phút).
- Câu chữ đi qua `Loc` (khoá `EnvCheck.*`); thêm nhóm mới là thêm class + một dòng `AddScoped<IEnvironmentCheck, …>`
  trong `AddSettingsUseCases`.

---

## Troubleshooting — lỗi thường gặp

| Triệu chứng | Nguyên nhân & cách xử lý |
|---|---|
| App chết ngay khi khởi động, log Fatal `Encryption...` | Thiếu `Encryption__ApiKeyKey` — cố ý fail-fast. Đặt biến môi trường rồi chạy lại |
| App cố kết nối SQL Server dù bạn muốn Sqlite | Connection string đang có giá trị (mặc định `appsettings.json` là chuỗi SQL Server). Đặt env var rỗng `ConnectionStrings__DefaultConnection=` khi chạy DLL trực tiếp ([getting-started.md](getting-started.md#ba-kịch-bản-chạy), kịch bản B) |
| `OrgUnits`/`Associates` trống hoặc không cập nhật | Mở `/hangfire` (Admin) xem lần chạy `organization-sync`. `Thiếu cấu hình EmployeeApi:...` ⇒ điền key đó; `Xin token ... thất bại` ⇒ sai TokenUrl/ClientId/secret, hoặc client chưa được cấp quyền gọi employee-api ở IdentityServer; `employee-api trả về 0 ...` ⇒ API trả rỗng, job cố ý huỷ để không xoá mềm cả bảng. `EmployeeApi:Enabled=false` thì không có lịch nào ([data-model.md](data-model.md#orgunits--associates--đồng-bộ-từ-employee-api)) |
| `Unable to resolve service for type ...` | Quên đăng ký DI trong `ApplicationServiceCollectionExtensions` — thêm vào đúng nhóm `AddXxx()` |
| `dotnet build` fail `MSB3552: **/*.resx cannot be found` (Linux) | Lần chạy trước tạo thư mục literal `C:\Study App\...` trong repo (root path Windows). Xóa thư mục rác đó + `Logs/`; lần sau set `AgentWorkspace__RootPath` |
| ApiKey model giải mã lỗi / gọi LLM báo key sai sau khi đổi máy/khóa | `Encryption__ApiKeyKey` khác với khóa lúc mã hóa. Dùng lại khóa cũ, hoặc nhập lại ApiKey ở màn AI Models |
| Lỗi `Value cannot be an empty string (Parameter 'key')` khi agent chạy | Model đang chọn có ApiKey rỗng (model seed DeepSeek để trống) — điền ApiKey hoặc trỏ agent sang model khác |
| Agent chạy "thành công" nhưng Output rỗng (khi dùng stub/proxy) | Endpoint không hỗ trợ **SSE streaming** — app đọc stream. Stub phải trả `text/event-stream` |
| Call log báo `The response ended prematurely` (endpoint "đóng kết nối"), nhưng **Test Connection** của model vẫn OK | Đường dây thông; gateway/proxy trước endpoint cắt lời gọi chạy lâu (request thử `"ping"` xong trước ngưỡng cắt nên vẫn xanh). Thông báo lỗi nói rõ ca nào: **"CHƯA trả về cả header"** ⇒ gateway giữ cả phản hồi tới khi model trả lời xong (app đã stream mà vẫn vậy), nên đổi Structured Output hay thử lại đều vô ích; **"giữa lúc đang trả câu trả lời"** ⇒ gateway chặn theo tổng thời lượng. Cả hai đều phải xử lý ngoài app: nhờ quản trị gateway nâng timeout/cho chuyển tiếp stream, hoặc gắn agent sang endpoint không qua gateway đó. Nếu `Llm:Proxy` đang bật, thử cho host đó vào `Llm:Proxy:BypassList` để loại trừ proxy công ty. App không thử lại lần cắt chậm (`LlmRetryPolicy`) nên lỗi hiện sau một lần chờ, không phải bốn |
| Một lời gọi model thành công nhưng `Duration` lớn bất thường, token/giây thấp hơn hẳn các bước khác (vd. 748s cho 22k token) | Mở **System Logs**, lọc `LlmRetryPolicy` quanh thời điểm đó: có dòng `attempt N failed after ~300s (HTTP 504…)` ⇒ gateway/proxy cắt rồi SDK thử lại, mọi lần thử cộng vào một dòng call log (từ bản này lần cắt chậm bằng mã 408/5xx cũng không thử lại nữa — lỗi hiện ra sau một lần chờ, bấm **Continue from step N** ở Agent Dashboard để chạy tiếp từ checkpoint). Không có dòng nào mà file log có `streaming response headers arrived only after …s` ⇒ một lần thử duy nhất phải xếp hàng ở upstream — chuyện của Model Farm, không phải của app. Lượt hay dính nhất là lượt Developer **đầu tiên** (ghi trọn khung POC vào một tham số tool): nếu call log của nó ghi `_eager_input_streaming: false`, gateway đã từ chối trường đó và luồng lại im lặng tới khi model viết xong — xem [llm-and-prompts.md](llm-and-prompts.md#claude-trên-vertex-ai-bosch-model-farm-endpoint-2) |
| Task đứng `Running` mãi sau khi app restart | Bình thường: `DbInitializer` sẽ re-queue ở lần khởi động kế (tối đa 3 lần thử rồi Failed). Không tự sửa tay Status trong DB khi app đang chạy |
| Bấm **Continue from step N** (trang Requirements: *Chạy tiếp từ bước N*) mà task cứ hỏng lại ở đúng bước đó | Chạy tiếp gửi lại nguyên hội thoại đã ghi (`AgentRunCheckpoints`). Lỗi tạm thời (hạn mức, rớt kết nối) thì đợi rồi bấm lại; còn nếu call log cho thấy chính request bị từ chối (vd vượt context window) thì bấm **Run from scratch** (*Chạy lại từ đầu*) để bỏ checkpoint — xem [delivery-pipeline.md](delivery-pipeline.md#thử-lại-là-chạy-tiếp-checkpoint-của-lượt-agent) |
| Đổi quyền ở popup Permissions (trang User Roles) mà user kêu không thấy thay đổi | Không thể — cache được invalidate ngay khi lưu. Kiểm tra lại đúng role — mọi role đều theo ma trận, riêng Admin luôn giữ `AdministrationManageRoles` |
| Sinh tài liệu ném `FileNotFoundException` trên bản publish | Thiếu thư mục `Templates/` — csproj đã cấu hình copy; nếu tự đóng gói tay phải mang theo `Templates/*.docx` + `Prompts/**` |
| Đổi schema khi dev Sqlite không thấy cột mới | Sqlite dùng `EnsureCreated` (không migration) — xóa `ICOGenerator.db*` để dựng lại |
| Muốn reset sạch lịch sử migration | Xóa `Migrations/` → `dotnet ef migrations add V1` với env ≠ Development (để sinh theo SqlServer) — xem [data-model.md](data-model.md#migration) |
| Bật Otel xong log đầy lỗi exporter | Chưa có OTLP collector — tắt `Otel:Enabled` hoặc dựng collector trước (nhanh nhất: `docker compose -f docker-compose.otel.yml up -d`, nghe sẵn `localhost:4317`) |
| Cổng duyệt POC không có nút Từ chối | Cố ý (`PocGateNotRejectable`) — POC sai = requirement sai, user sửa qua chat BA; TeamDev chỉ được "Request revision" |
| Tool git trả `not a git repository root` | `repoPath` trỏ vào thư mục không phải gốc repo. Đúng đường dẫn nằm ở khối "CÁC REPO PHẢI BÀN GIAO" trong prompt bước PR ([workspace-and-poc.md](workspace-and-poc.md#repo-đích-của-dự-án)). Nếu chính thư mục repo cũng chưa có `.git`: `ProjectRepositorySeeder` chưa chạy — chạy lại bước Implementation hoặc Retry bước PR (seeder chạy ở cả hai) |
| Bước PR (hoặc clone ở bước Implementation) báo `could not read Username` / `Authentication failed` | Dự án chưa có tài khoản Git: nhập **Git user name + Git token** ở popup *Config delivery* (Bitbucket Server/Bosch bắt buộc đúng user name; GitHub chỉ cần token), hoặc cấu hình khóa SSH / credential helper cho tài khoản chạy app. Đã nhập mà vẫn lỗi ⇒ token hết hạn/thiếu quyền ghi repo, hoặc git máy chủ < 2.31 (không đọc được credential của dự án). Git được đặt `GIT_TERMINAL_PROMPT=0` nên nó fail ngay thay vì treo tới hết timeout |
| Bước PR báo `Please tell me who you are` | Repo thiếu `user.email`/`user.name` — cực hiếm vì seeder tự đặt mặc định `ICOGenerator` / `icogenerator@localhost`; kiểm tra `git config` trong `04_Implementation/src/...` |
| Task Implementation fail ngay với `git clone repo dự án ... thất bại` | Cố ý fail sớm: Backend/Frontend Git URL sai hoặc máy chủ không truy cập được repo. Sửa URL ở Agent Dashboard rồi **Thử lại** — thà dừng ở đây còn hơn sinh xong 40 file mới phát hiện không có chỗ bàn giao |
| Lệnh `dotnet build`/`npm install` của agent chạy sai thư mục | Agent quên truyền `workingDirectory` cho `RunCommand` (mặc định là GỐC workspace; `cd` và toán tử shell bị chặn). Prompt Implementation/Tester đã dặn — nếu tái diễn thì sửa ở prompt, không nới allowlist |
