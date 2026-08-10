# ICOGenerator

**Hệ thống multi-agent dùng LLM để biến *một cuộc trò chuyện về yêu cầu phần mềm* thành *tài liệu đặc
tả + demo chạy được + source code + Pull Request*, với con người duyệt ở từng cổng.**

```
User tạo Project
  └► Chat với agent BA (hỏi đáp làm rõ yêu cầu, upload tài liệu nguồn: ảnh, PDF, Word, Excel)
       └► "Write Requirement" → Product Brief (ngôn ngữ đời thường, sửa được nhiều lần)
            └► "Approve" → AI Design Spec (bản kỹ thuật) → cổng xác nhận giả định
                 └► Delivery Pipeline chạy nền, CỔNG DUYỆT giữa mỗi bước:
                      POC HTML → Tài liệu kỹ thuật → Kiến trúc → Code đầy đủ
                      → Code Review → Testing (tự sửa lỗi khi FAIL) → Pull Request
```

"Nhân sự" là 5 **AI agent** seed sẵn — BA, Tech Lead, Developer, Tester, UI/UX — mỗi agent có system
prompt riêng, model riêng và một tập **tool** được phép dùng (đọc/ghi file, chạy lệnh, git). Xung quanh
là hạ tầng vận hành đầy đủ: phân quyền theo role, audit log, trần chi phí LLM, thông báo, đo chất lượng
prompt (Evals), quản lý phiên bản prompt (Prompt Studio), báo cáo Usage/Delivery Quality.

Ứng dụng được xây trong bối cảnh nội bộ Bosch: có dữ liệu tổ chức (OrgUnits/Associates đồng bộ từ
HR_Portal) để BA "hiểu" phòng ban thật, và tùy chọn dựng code trên khung chuẩn Bosch (.NET + Angular).

---

## Chạy nhanh

Cần **.NET 8 SDK** và một endpoint LLM tương thích OpenAI. Không cần SQL Server nếu chạy Sqlite:

```bash
dotnet build -v q
ASPNETCORE_ENVIRONMENT=Development \
Database__Provider=Sqlite \
Encryption__ApiKeyKey=dev-key \
AgentWorkspace__RootPath=/tmp/ico-workspaces \
ASPNETCORE_URLS=http://127.0.0.1:5099 \
dotnet bin/Debug/net8.0/ICOGenerator.dll
```

```bash
dotnet test          # xUnit, chạy trên EF Sqlite — không cần SQL Server hay LLM
```

Mặc định `Authentication:Provider = Local` nên **không có form đăng nhập** — app tự phát cookie
SuperAdmin và vào thẳng màn hình Projects. Chi tiết đầy đủ (bí mật bắt buộc, ba kịch bản chạy, những gì
xảy ra lúc khởi động): **[docs/getting-started.md](docs/getting-started.md)**.

---

## Tài liệu

Toàn bộ tài liệu nằm trong [`docs/`](docs/). Bắt đầu ở
**[docs/overview.md](docs/overview.md)**, hoặc nhảy thẳng tới thứ bạn cần:

| Bạn muốn | Đọc |
|---|---|
| Hiểu app làm gì, ai dùng, các mảnh ghép lớn | [overview.md](docs/overview.md) |
| Chạy được app trên máy mình | [getting-started.md](docs/getting-started.md) |
| Biết một file nên nằm ở đâu và vì sao | [architecture.md](docs/architecture.md) |
| Tra bảng/cột/quan hệ trong DB | [data-model.md](docs/data-model.md) |
| Hiểu luồng chat BA: trí nhớ, bản đồ bao phủ, các cổng | [requirement-flow.md](docs/requirement-flow.md) |
| Hiểu pipeline nền: các bước, cổng duyệt, chu trình sửa lỗi | [delivery-pipeline.md](docs/delivery-pipeline.md) |
| Thêm tool cho agent / hiểu vòng lặp agent | [agents-and-tools.md](docs/agents-and-tools.md) |
| Thêm model, hiểu đường gọi LLM & hệ thống prompt | [llm-and-prompts.md](docs/llm-and-prompts.md) |
| Hiểu workspace, POC demo, review & nghiệm thu | [workspace-and-poc.md](docs/workspace-and-poc.md) |
| Tra endpoint, quyền, đăng nhập SSO | [screens-and-permissions.md](docs/screens-and-permissions.md) |
| Tra key `appsettings.json` | [configuration.md](docs/configuration.md) |
| Log, OpenTelemetry, sửa lỗi thường gặp | [operations.md](docs/operations.md) |
| Notifications, budget, Usage, Evals, Feedback | [supporting-features.md](docs/supporting-features.md) |
| Chạy test & xác minh end-to-end không cần hạ tầng thật | [testing.md](docs/testing.md) |
| **Thêm một tính năng đúng kiến trúc** | [contributing.md](docs/contributing.md) |
| Tra thuật ngữ trong dự án | [glossary.md](docs/glossary.md) |

---

## Tech stack

| Thành phần | Công nghệ |
|---|---|
| Runtime | .NET 8, ASP.NET Core **MVC** (Razor Views) — không có SPA framework |
| ORM | EF Core 8 — provider chọn runtime: `SqlServer` (mặc định) hoặc `Sqlite` (dev/CI) |
| Agent runtime | Microsoft Agent Framework (`Microsoft.Agents.AI`) |
| LLM | `Microsoft.Extensions.AI` + `...AI.OpenAI` — mọi endpoint OpenAI-compatible |
| Auth | Cookie + SSO OpenID Connect (IdentityServer) hoặc Local; phân quyền tự xây |
| Logging | Serilog; OpenTelemetry opt-in |
| Test | xUnit (`tests/ICOGenerator.Tests`) |

Chi tiết phiên bản và lý do chọn: [docs/getting-started.md](docs/getting-started.md#tech-stack).

---

## Bố cục repo

```
Program.cs        Điểm vào: Serilog bootstrap, middleware, gọi DbInitializer
Extensions/       NƠI DUY NHẤT đăng ký DI
Domain/           Entity + enum. Không phụ thuộc tầng nào
Contracts/        DTO hợp đồng Services ↔ Application (POCO thuần)
Configuration/    POCO cấu hình bind từ appsettings
Data/             AppDbContext, DbInitializer (migrate + seed)
Application/      Use case theo khu vực màn hình — một thao tác = một class
Services/         Việc kỹ thuật tái dùng: LLM, agent, tool, workflow, prompt, artifacts
Controllers/      MVC controller mỏng
Views/ wwwroot/   Razor view + css/js thuần theo màn hình
Prompts/          Template prompt .md (copy ra output khi build)
Templates/        BRD/SRS/FSD .docx
tests/            xUnit
docs/             Tài liệu (bảng ở trên)
```

---

## Quy ước tài liệu

- **Sửa code là sửa tài liệu trong cùng PR.** Mỗi chủ đề có **đúng một** file trong `docs/` — đừng mô
  tả lại cùng một cơ chế ở file thứ hai, hãy link sang.
- Khi thấy lệch giữa tài liệu và code, **tin code** — rồi sửa tài liệu.
- Luật "sửa gì thì sửa ở đâu": xem [docs/README.md](docs/README.md).
