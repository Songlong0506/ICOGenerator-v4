# Công thức làm việc & quy ước code

## Thêm một tính năng mới

Ví dụ: thêm màn hình "xuất báo cáo tổng hợp project".

1. **Domain/Contracts** — cần kiểu dữ liệu mới thì entity vào `Domain/` (nhớ tạo migration). DTO thì
   hỏi *ai đọc nó*: `Services` cũng đọc → `Contracts/<Module>/`; chỉ Application/Controllers/Views
   đọc → để **cạnh use case** trong `Application/<Module>/` (xem [quy ước đặt DTO](#chỗ-đặt-dto-quyết-định-bởi-ai-đọc-nó)).
2. **Application** — tạo `Application/Projects/ExportProjectReportUseCase.cs`: một class, một file,
   một `ExecuteAsync`. Tên `Get...Query` (đọc) / `...UseCase` (ghi).
3. **Services** (nếu có logic kỹ thuật tái dùng) — gọi LLM, sinh file... đặt ở `Services/...`.
4. **Controller** — action **mỏng** gọi use case; gắn `[RequirePermission]` phù hợp. Action nhận
   `projectId` thì gắn `[RequireProjectAccess]` — quên là **fail test**, không phải fail âm thầm.
5. **View/JS/CSS** — `.cshtml` + file js/css theo màn hình trong `wwwroot/`.
6. **DI** — đăng ký vào đúng nhóm `AddXxx()` ở `Extensions/ApplicationServiceCollectionExtensions.cs`.
   Quên là `Unable to resolve service` lúc chạy.
7. **Test** — thêm ở `tests/` đúng thư mục khu vực.

Nếu một class không rơi gọn vào bước nào ở trên thì nhiều khả năng nó đang gánh quá nhiều việc — tách ra.

### Các công thức chuyên biệt

| Muốn thêm | Làm gì | Chi tiết |
|---|---|---|
| **Tool cho agent** | Một method `public` có `[Description]` trong class `*Tools` + gán cho vai | [agents-and-tools.md](agents-and-tools.md#tool--một-method-c-public-có-description) |
| **Bước pipeline** | Thêm một dòng vào `DeliveryPipeline.Steps` + giá trị stage enum + prompt template. Worker/orchestrator **không đổi** | [delivery-pipeline.md](delivery-pipeline.md) |
| **Quyền / màn hình** | `AppPermission` → `PermissionCatalog.Screens` → `[RequirePermission]` → menu `_Layout` | [screens-and-permissions.md](screens-and-permissions.md#phân-quyền-chiều-dọc--role--quyền-mức-hành-động) |
| **Kênh thông báo** | Hiện thực `INotificationChannel` + đăng ký DI | [supporting-features.md](supporting-features.md#notifications) |
| **Model LLM** | Màn hình **AI Models** → Create. Không cần đụng code | [llm-and-prompts.md](llm-and-prompts.md#thêm-một-model-mới) |

---

## Quy ước phải giữ

- **Một file = một kiểu công khai** (class/record/enum/interface). Trừ DTO nhóm nhỏ liên quan chặt.
- **`namespace` = đường dẫn thư mục** (`Services/Tools/Execution/Foo.cs` → `ICOGenerator.Services.Tools.Execution`).
- **Đặt tên theo vai trò:** `...Query` (đọc), `...UseCase` (ghi), `...Vm` (view model), `I...`
  (interface), `...Service` (service nghiệp vụ).
- **Controller luôn mỏng**, không chứa logic nghiệp vụ.
- **Đừng để `Services` `using` ngược** lên `Application`/`Controllers`.
- **`Tools/Abstractions` chỉ chứa interface/record**; hiện thực ở `Tools/Execution`.
- **Đăng ký DI chỉ ở file Extensions**, đúng nhóm theo layer. Lifetime: policy/store config-bound
  stateless = Singleton; thứ gì đụng `DbContext` = Scoped; `IApiKeyProtector` **bắt buộc** Singleton.
- **Enum đã lưu DB dạng chuỗi ⇒ không đổi tên giá trị enum** đã có dữ liệu.
- **Action theo project luôn gắn `[RequireProjectAccess]`** — quyền theo role KHÔNG chặn được truy cập
  chéo giữa các project. Test sẽ fail nếu quên.
- **Dữ liệu seed lớn để dạng resource**, đừng viết thành mảng C# (xem
  [architecture.md](architecture.md#dữ-liệu-seed-lớn-là-resource-không-phải-code)).
- **Prompt đổi được runtime** — nhưng bản "chín" nên export đồng bộ ngược về repo.

### Icon: font bootstrap-icons, trừ nút chỉ-có-icon

Mặc định dùng `<i class="bi bi-*">` (font tải qua `<link>` CDN ở `_Layout`/`_GuestLayout`). Menu sidebar
thêm class `.nav-ico` để có hộp 20×20 cố định — glyph font rộng hẹp khác nhau, không ghim hộp thì nhãn
chữ so le và sidebar thu gọn canh giữa lệch từng dòng. Hộp phải ghi thẳng `width`/`height`, đừng để
`line-height` suy ra: hỏng CDN là `::before` rỗng và hộp tụt về 0.

Ngoại lệ — **giữ SVG nội tuyến** (`<svg class="ico">`) cho điều khiển **không có nhãn chữ đi kèm**: nút
thu gọn sidebar, nút đóng modal, bút chì sửa tại chỗ (`Views/Projects/Index.cshtml`,
`Views/AgentDashboard/Index.cshtml`), caret/kính lúp do JS dựng (`wwwroot/js/dropdown.js`,
`Views/Shared/_CommandBar.cshtml`). CDN không tới được thì icon có nhãn chữ vẫn dùng được, còn nút
chỉ-có-icon biến thành ô trống không ai bấm. Cùng lý do, chevron của `PocTemplate` giữ SVG vì animation
xoay bám `.nav-chevron`.

### Hộp thoại: chiều cao có trần, cuộn trong thân

`.modal-backdrop` là lưới canh giữa (`site.css`), nên hộp thoại cao hơn màn hình sẽ tràn ra cả hai
đầu và **phần tràn không cuộn tới được** — trên laptop màn thấp, một form dài (Add/Edit Model) mất
luôn hàng nút cuối. Vì vậy `.modal` mặc định đã có `max-height: 100%` + `overflow-y: auto`: hộp thoại
luôn nằm trọn trong màn hình và tự cuộn phần dư. Đừng gỡ cặp thuộc tính đó khi thêm modal mới.

Hộp thoại nào cần **tiêu đề/hàng tab đứng yên** khi cuộn thì tự khai `max-height` cho `.modal` và đặt
`overflow-y: auto` ở đúng khối thân (mẫu: `.eval-wide-modal` + `.eval-detail-body`, `.poc-tech-panel`).

Hệ quả cần biết khi đặt `<select>` gần đáy modal: panel của combo (`dropdown.js`) luôn thả xuống dưới
và bị `overflow` của modal cắt. `dropdown.js` xử lý sẵn — lúc mở, nó cuộn tổ tiên cuộn được gần nhất
xuống vừa đủ để panel lọt khung; đừng chép logic đó ra view.

### Chỗ đặt DTO quyết định bởi ai đọc nó

Không phải bởi "nó là DTO":

| Kiểu dữ liệu | Đặt ở | Dấu hiệu nhận biết |
|---|---|---|
| Đi qua ranh giới `Services ↔ Application` — schema LLM trả về, input dựng `.docx` | `Contracts/<Module>/` | thuần POCO, **0 `using` ngoài**, không logic |
| Chỉ `Application`/`Controllers`/`Views` đọc — `XxxVm`, `XxxPage`, `XxxResult` | `Application/<Module>/`, cạnh use case sinh ra nó | có thể dùng `Domain`, được phép có property tính toán cho view (`TotalPages`, `HasNext`) |
| POCO cấu hình bind từ `appsettings.json` | `Configuration/` | có `const string SectionName` |

Đừng gom hết vào `Contracts/` cho "thống nhất": `Contracts/` mất tính thuần POCO, còn model bị tách
khỏi use case sinh ra nó. Ngược lại, đừng để POCO mà `Services` cần đọc nằm trong `Application/` — đó
là cách vi phạm chiều phụ thuộc lọt vào.

---

## Cạm bẫy đã biết (đọc trước khi sửa sâu)

- **Chat BA chạy đồng bộ trong request** — luồng job `AgentJob`/`AgentJobRunner` cũ đã gỡ hẳn (bảng đã
  drop); đừng dựng lại trừ khi nối vào UI. Pipeline nền dùng `WorkflowRun` + `AgentTask`.
- **Đường fallback prompt-based cho agent đã gỡ** — chỉ còn native tool-calling; đừng tìm
  `AgentActionParser`/`ToolSchemaBuilder` (không còn tồn tại).
- **Worker generic** — muốn đổi hành vi hand-off thì sửa `ApproveStageUseCase`/`DeliveryPipeline`,
  không nhét `if/else` theo stage vào worker (ngoại lệ duy nhất được phép: chu trình BugFix và nhánh
  TechnicalDocs, đã cô lập sẵn).
- **`MaxSteps` = số lời gọi LLM của một task** — bước sinh nhiều file phải khuyến khích `WriteFiles`;
  hết budget có pha salvage nhưng đừng dựa vào nó.
- **`WorkflowProgressReporter` in-memory** — nhiều instance app (scale-out) sẽ không chia sẻ tiến độ
  live; kiến trúc hiện tại giả định single instance (worker nền cũng vậy).
- **Migration là SQL-Server-specific** — sinh migration phải để `Database:Provider` = `SqlServer`.
  Sqlite dùng `EnsureCreated`, đổi schema khi dev Sqlite = xóa file `ICOGenerator.db*`.
