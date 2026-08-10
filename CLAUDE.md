# CLAUDE.md

Hướng dẫn cho AI agent làm việc trong repo này.

## Trước khi sửa gì

Đọc [`docs/README.md`](docs/README.md) để biết chủ đề bạn đang đụng tới thuộc file tài liệu nào, rồi
đọc file đó. Đừng suy kiến trúc từ việc đọc rải rác vài file `.cs` — các cơ chế ở đây (cổng duyệt,
trí nhớ hội thoại, bản đồ bao phủ, tầng tự kiểm POC) đều có lý do thiết kế đã ghi lại, và sửa mà không
biết lý do là cách nhanh nhất để làm hỏng một chốt chặn cố ý.

## Luật kiến trúc không được vi phạm

Chi tiết ở [`docs/architecture.md`](docs/architecture.md) và [`docs/contributing.md`](docs/contributing.md).
Tóm tắt:

- **Chiều phụ thuộc một chiều**: `Controllers → Application → Services → Data → Domain`. `Services`
  **không bao giờ** `using` ngược lên `Application`/`Controllers`.
- **Controller mỏng** — chỉ gọi use case rồi trả View/JSON. Không truy vấn DB, không gọi LLM trực tiếp.
- **Một thao tác người dùng = một class, một file, một `ExecuteAsync`** ở `Application/`. Tên
  `...Query` (đọc) / `...UseCase` (ghi) / `...Vm` (view model).
- **`namespace` = đường dẫn thư mục.**
- **Đăng ký DI chỉ ở `Extensions/ApplicationServiceCollectionExtensions.cs`**, đúng nhóm `AddXxx()`.
  Quên là `Unable to resolve service` lúc chạy, không phải lỗi build.
- **Action nhận `projectId` phải gắn `[RequireProjectAccess]`** — `ProjectAccessCoverageTests` fail
  build nếu quên.
- **Enum đã lưu DB dạng chuỗi ⇒ không đổi tên giá trị enum** đã có dữ liệu.

## Điểm mở rộng — đừng rải `if/else`

| Muốn thêm | Chỗ duy nhất cần sửa |
|---|---|
| Bước pipeline / vai mới | một dòng trong `Services/Workflows/DeliveryPipeline.Steps` (+ giá trị `WorkflowStageKey` + prompt template) |
| Tool cho agent | một method `public` có `[Description]` trong class `*Tools` + gán vai trong `DbInitializer.AssignDefaultToolsAsync` |
| Kênh thông báo | hiện thực `INotificationChannel` + đăng ký DI |
| Model LLM | màn hình **AI Models** — không cần code |

`AgentTaskWorker` là **generic**: nó chỉ "chạy task → còn bước kế thì chờ duyệt, hết thì xong". Muốn
đổi hành vi hand-off thì sửa `ApproveStageUseCase`/`DeliveryPipeline`, **không** nhét `if (stage == X)`
vào worker. Hai ngoại lệ đã cô lập sẵn: chu trình BugFix và nhánh TechnicalDocs.

## Chạy & kiểm chứng

```bash
dotnet build -v q
dotnet test                      # xUnit trên EF Sqlite — không cần SQL Server hay LLM
```

Chạy app ở môi trường không có SQL Server / LLM thật: xem [`docs/getting-started.md`](docs/getting-started.md)
kịch bản B, hoặc skill [`.claude/skills/verify/SKILL.md`](.claude/skills/verify/SKILL.md) để lái UI thật
bằng Playwright với LLM stub (stub **bắt buộc** hỗ trợ SSE streaming, nếu không agent "chạy xong" mà
Output rỗng).

> Trên Linux/macOS **luôn** set `AgentWorkspace__RootPath` — mặc định là đường dẫn Windows, để nguyên
> sẽ tạo thư mục rác chứa backslash trong repo và làm `dotnet build` lần sau fail `MSB3552`.

## Quy ước tài liệu

- **Sửa code là sửa tài liệu trong cùng PR.** Bảng "sửa gì thì sửa ở đâu" nằm ở
  [`docs/README.md`](docs/README.md).
- **Một chủ đề một file.** Nếu định mô tả một cơ chế ở file thứ hai, hãy link sang file chủ quản.
- **Đừng viết changelog hay blueprint chưa làm vào `docs/`** — lịch sử thuộc về git, ý tưởng thuộc về
  issue. Tài liệu chỉ mô tả cái đang có.
- Số liệu kiểm được (số bảng, số quyền, số controller) phải cập nhật cùng code.

## Ngôn ngữ

Tài liệu, comment và commit message trong repo này viết bằng **tiếng Việt**. Giữ nguyên quy ước đó.
