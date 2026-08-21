# Tài liệu ICOGenerator

Mỗi file dưới đây phụ trách **đúng một chủ đề**. Không mô tả lại cùng một cơ chế ở hai nơi — link sang
file chủ quản. Đây là luật giữ cho bộ tài liệu không quay lại tình trạng chắp vá.

## Đọc theo thứ tự (người mới)

1. [overview.md](overview.md) — app làm gì, ai dùng, luồng end-to-end, các agent mặc định.
2. [getting-started.md](getting-started.md) — tech stack, bí mật bắt buộc, ba kịch bản chạy, khởi động, test.
3. [architecture.md](architecture.md) — phân tầng, chiều phụ thuộc, bản đồ thư mục, các pattern.
4. [requirement-flow.md](requirement-flow.md) + [delivery-pipeline.md](delivery-pipeline.md) — hai động cơ của hệ thống.
5. [contributing.md](contributing.md) — công thức thêm tính năng + quy ước phải giữ.

## Tra cứu theo chủ đề

| File | Chủ quản chủ đề |
|---|---|
| [overview.md](overview.md) | Bài toán, persona, agent mặc định, runtime lifecycle, định nghĩa "done" |
| [getting-started.md](getting-started.md) | Tech stack, môi trường, secrets, kịch bản chạy, `DbInitializer`, Chromium cho POC |
| [architecture.md](architecture.md) | Layer, dependency rule, bản đồ thư mục, use-case pattern, DI, seed-as-resource |
| [data-model.md](data-model.md) | Toàn bộ bảng, cột, index, quan hệ, cascade, ERD, seed data, migration |
| [requirement-flow.md](requirement-flow.md) | Chat BA (SSE), trí nhớ hội thoại/user/tổ chức, bản đồ bao phủ, tài liệu nguồn, các cổng phía yêu cầu |
| [delivery-pipeline.md](delivery-pipeline.md) | `AgentTaskWorker`, `DeliveryPipeline.Steps`, cổng duyệt, revision, chu trình BugFix, bước PR, UAT |
| [agents-and-tools.md](agents-and-tools.md) | `AgentRunService`, middleware, tool registry, danh mục tool, rào chắn an toàn |
| [llm-and-prompts.md](llm-and-prompts.md) | Đường gọi model, structured output, `Services/Llm`, danh mục prompt, Prompt Studio |
| [workspace-and-poc.md](workspace-and-poc.md) | Bố cục workspace, POC demo & tầng tự kiểm, POC Review, snapshot, khung Bosch |
| [screens-and-permissions.md](screens-and-permissions.md) | Bảng màn hình/endpoint/quyền, xác thực Local & SSO, RBAC, `[RequireProjectAccess]`, bảo vệ bí mật |
| [configuration.md](configuration.md) | Mọi key `appsettings.json` và mặc định |
| [operations.md](operations.md) | Serilog, OpenTelemetry, log nghiệp vụ, bảng troubleshooting |
| [supporting-features.md](supporting-features.md) | Notifications, budget guard, Usage/Quality, revision tài liệu, Prompt Evals, Feedback |
| [testing.md](testing.md) | `dotnet test`, skill `verify` để chạy end-to-end không cần hạ tầng thật |
| [contributing.md](contributing.md) | Công thức thêm tính năng/tool/bước pipeline/quyền, quy ước, cạm bẫy |
| [glossary.md](glossary.md) | Từ điển thuật ngữ trong dự án |

## Slide thuyết trình

Nằm ở [`presentation/`](presentation/) — HTML tự chứa, mở thẳng bằng trình duyệt, mũi tên để chuyển slide,
in ra PDF được. Slide chỉ **kể lại** cơ chế; nguồn chân lý vẫn là các file `.md` ở trên.

| Deck | Trả lời câu hỏi |
|---|---|
| [ico-generator-v4-slides.html](presentation/ico-generator-v4-slides.html) | Cả sản phẩm làm được gì: 5 agent, pipeline ý tưởng → Pull Request, cổng duyệt, chi phí/chất lượng |
| [ba-requirement-to-product-brief.html](presentation/ba-requirement-to-product-brief.html) | Riêng phía yêu cầu: BA khai thác thế nào và bằng cơ chế nào một buổi chat thành Product Brief đã duyệt — chi tiết ở [requirement-flow.md](requirement-flow.md) |

## Sửa gì thì sửa ở đâu

| Bạn vừa đổi | Cập nhật |
|---|---|
| Entity / migration / index | [data-model.md](data-model.md) |
| Controller / action / quyền mới | [screens-and-permissions.md](screens-and-permissions.md) |
| Bước pipeline, cổng duyệt, worker | [delivery-pipeline.md](delivery-pipeline.md) |
| Prompt file, Prompt Studio, model | [llm-and-prompts.md](llm-and-prompts.md) |
| Tool mới cho agent | [agents-and-tools.md](agents-and-tools.md) |
| Hành vi chat BA, trí nhớ, bản đồ bao phủ | [requirement-flow.md](requirement-flow.md) |
| POC template, tầng tự kiểm, POC Review | [workspace-and-poc.md](workspace-and-poc.md) |
| Key `appsettings.json` | [configuration.md](configuration.md) |
| Quy ước code / luật phân tầng | [architecture.md](architecture.md) + [contributing.md](contributing.md) |
| Thuật ngữ mới xuất hiện trong code | [glossary.md](glossary.md) |

## Nguyên tắc viết

- **Một chủ đề, một file.** Trùng lặp là nguồn gốc của tài liệu trôi lệch: bản sao thứ hai không bao
  giờ được cập nhật cùng bản gốc.
- **Không viết changelog vào tài liệu tham chiếu.** "Lần refactor này đã dọn X" mất nghĩa ngay sau lần
  refactor kế tiếp — lịch sử thuộc về git. Chỉ giữ ghi chú lịch sử khi nó ngăn người đọc đi tìm thứ
  không còn tồn tại (ví dụ: "đừng tìm `AgentActionParser`, đã gỡ").
- **Không viết blueprint chưa làm.** Tài liệu mô tả *cái đang có*. Ý tưởng chưa triển khai thuộc về
  issue, không phải file `.md` trong repo.
- **Số liệu phải kiểm được** (số bảng, số quyền, số controller). Đổi code là đổi luôn con số ở đây.
- Khi tài liệu và code lệch nhau, **tin code**.
