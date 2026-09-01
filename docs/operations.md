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
giữ 14 ngày). Production có thể đổi Console sang JSON nén cho log aggregator (Seq/Loki/ELK) qua
`appsettings.Production.json` — **không sửa code**.

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

### Log nghiệp vụ riêng (ngoài Serilog)

Ba loại log sống trong DB chứ không trong file log, vì chúng là dữ liệu để tra cứu trên UI chứ không
phải dòng chảy vận hành:

| Bảng | Ghi gì | Xem ở đâu |
|---|---|---|
| `AgentModelCallLogs` | Mỗi lời gọi model: request/response, token, thời lượng, `Purpose` | Popup **AI Call Logs** ở Agent Dashboard; là nguồn của trang Usage & Delivery Quality |
| — (`ToolExecutionLogger`) | Mỗi lần agent gọi tool | Log ứng dụng |
| `AuditLogs` | Thay đổi cấu hình (Settings/Roles/Agent/Model/Prompt) kèm actor + before/after JSON | Màn hình **Audit Log** |

### Mang một lời gọi model đi hỏi chỗ khác

Modal **Model Invocation Detail** có hai nút tải file `.md`, và mỗi dòng trong popup AI Call Logs có nút
`⬇` tải nhanh lời gọi của dòng đó (`GET CallLogExport` / `CallLogTurnExport`, dựng bởi
`ModelCallLogMarkdown`). Đây là đường để mang trọn ngữ cảnh một lượt gọi ra ngoài — dán cho một AI khác
soi khi response lệch, hoặc đính kèm vào một issue.

Bản xuất là Markdown chứ không phải `RequestJson`, vì thứ cần đọc nằm trong `messages`: ở dạng JSON thì
mọi xuống dòng của prompt là `\n` và mọi dấu nháy bị escape, người lẫn model đều phải giải mã trước khi
đọc được câu đầu tiên. Ở đây mỗi message là một khối riêng ghi rõ VAI và ĐỘ DÀI, nên câu hỏi *"lệch là do
prompt hay do context"* nhìn thấy được từ mục lục. Nội dung **không bị cắt** — cắt một khối context ở giữa
là bỏ đi đúng thứ đang cần soi. Ảnh gửi kèm chỉ được nêu tên (bytes ở trên đĩa, xem
["Ảnh trong call log"](requirement-flow.md#tài-liệu-nguồn-ảnh-và-call-log)).

**Nút "Cả cụm lượt" tồn tại vì một thao tác người dùng tốn vài lượt gọi model nối nhau**, và output của
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

## Troubleshooting — lỗi thường gặp

| Triệu chứng | Nguyên nhân & cách xử lý |
|---|---|
| App chết ngay khi khởi động, log Fatal `Encryption...` | Thiếu `Encryption__ApiKeyKey` — cố ý fail-fast. Đặt biến môi trường rồi chạy lại |
| App cố kết nối SQL Server dù bạn muốn Sqlite | Thiếu env var `Database__Provider=Sqlite` (mặc định `appsettings.json` là SqlServer). Đặt biến này khi chạy DLL trực tiếp ([getting-started.md](getting-started.md#ba-kịch-bản-chạy), kịch bản B) |
| `Unable to resolve service for type ...` | Quên đăng ký DI trong `ApplicationServiceCollectionExtensions` — thêm vào đúng nhóm `AddXxx()` |
| `dotnet build` fail `MSB3552: **/*.resx cannot be found` (Linux) | Lần chạy trước tạo thư mục literal `C:\Study App\...` trong repo (root path Windows). Xóa thư mục rác đó + `Logs/`; lần sau set `AgentWorkspace__RootPath` |
| ApiKey model giải mã lỗi / gọi LLM báo key sai sau khi đổi máy/khóa | `Encryption__ApiKeyKey` khác với khóa lúc mã hóa. Dùng lại khóa cũ, hoặc nhập lại ApiKey ở màn AI Models |
| Lỗi `Value cannot be an empty string (Parameter 'key')` khi agent chạy | Model đang chọn có ApiKey rỗng (model seed DeepSeek để trống) — điền ApiKey hoặc trỏ agent sang model khác |
| Agent chạy "thành công" nhưng Output rỗng (khi dùng stub/proxy) | Endpoint không hỗ trợ **SSE streaming** — app đọc stream. Stub phải trả `text/event-stream` |
| Task đứng `Running` mãi sau khi app restart | Bình thường: `DbInitializer` sẽ re-queue ở lần khởi động kế (tối đa 3 lần thử rồi Failed). Không tự sửa tay Status trong DB khi app đang chạy |
| Đổi quyền ở màn Roles mà user kêu không thấy thay đổi | Không thể — cache được invalidate ngay khi lưu. Kiểm tra lại đúng role, và nhớ **SuperAdmin luôn full quyền** bất kể ma trận (Admin thì theo ma trận) |
| Sinh tài liệu ném `FileNotFoundException` trên bản publish | Thiếu thư mục `Templates/` — csproj đã cấu hình copy; nếu tự đóng gói tay phải mang theo `Templates/*.docx` + `Prompts/**` |
| Đổi schema khi dev Sqlite không thấy cột mới | Sqlite dùng `EnsureCreated` (không migration) — xóa `ICOGenerator.db*` để dựng lại |
| Muốn reset sạch lịch sử migration | Xóa `Migrations/` → `dotnet ef migrations add V1` với env ≠ Development (để sinh theo SqlServer) — xem [data-model.md](data-model.md#migration) |
| Bật Otel xong log đầy lỗi exporter | Chưa có OTLP collector — tắt `Otel:Enabled` hoặc dựng collector trước (nhanh nhất: `docker compose -f docker-compose.otel.yml up -d`, nghe sẵn `localhost:4317`) |
| Cổng duyệt POC không có nút Từ chối | Cố ý (`PocGateNotRejectable`) — POC sai = requirement sai, user sửa qua chat BA; TeamDev chỉ được "Yêu cầu chỉnh sửa" |
