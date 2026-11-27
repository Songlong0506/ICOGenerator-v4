# ICOGenerator

**Hệ thống multi-agent dùng LLM để biến *một cuộc trò chuyện về yêu cầu phần mềm* thành *tài liệu đặc
tả + demo chạy được + source code + Pull Request*, với con người duyệt ở từng cổng.**

```
User tạo Project
  └► Chat với agent BA (hỏi đáp làm rõ yêu cầu, upload tài liệu nguồn: ảnh, PDF, Word, Excel)
       └► "Write Requirement" → Product Brief
            └► "Approve" → AI Design Spec
                 └► Delivery Pipeline chạy nền, CỔNG DUYỆT giữa mỗi bước:
                      POC HTML → Tài liệu kỹ thuật → Kiến trúc → Code đầy đủ
                      → Code Review → Testing → Pull Request
```

> **Repo này được đồng bộ tự động, một chiều, từ repo gốc.** Mỗi lần đồng bộ ghi đè toàn bộ file đang
> có ở đây, nên **đừng commit sửa code trực tiếp vào repo này**: thay đổi sẽ mất ở lần đồng bộ sau. Cần
> sửa gì thì báo người duy trì repo gốc.

---

## Chạy

Cần **.NET 8 SDK**, SQL Server và một endpoint LLM tương thích OpenAI.

`appsettings.json` để sẵn `IdentityServer:LocalRole = "Admin"`: app **tự đăng nhập cục bộ** bằng user `Admin`,
không qua SSO — chỉ dùng cho dev/nội bộ. Khi triển khai thật đặt `IdentityServer:LocalRole` thành `""` trên máy
triển khai (vd trong `appsettings.Production.json`) để bắt buộc đăng nhập **SSO IdentityServer**. Khi đó ứng dụng phải
được đăng ký ở IdentityServer với redirect URI `{UrlApp}/signin-oidc` và post-logout
`{UrlApp}/signout-callback-oidc`; client/scope cấu hình ở section `IdentityServer` của `appsettings.json`.

Bí mật khác (ngoài khóa mã hóa ở trên) **không** nằm trong `appsettings.json`, nạp qua biến môi trường hoặc user-secrets. (Tài khoản Git để
clone/push repo dự án — user name + token — điền theo từng dự án ở popup **Config delivery** của Agent
Dashboard, không phải biến môi trường.)

| Biến | Ý nghĩa |
|---|---|
| `Encryption__ApiKeyKey` | Khóa mã hóa ApiKey của model và Git token của dự án lưu trong DB. `appsettings.json` có sẵn một khóa; nên ghi đè bằng biến này trên môi trường thật. Đổi khóa thì các giá trị đã lưu không giải mã được nữa. |
| `ConnectionStrings__DefaultConnection` | Chuỗi kết nối SQL Server (nếu khác giá trị trong `appsettings.json`). |
| `IdentityServer__ClientSecret` | Chỉ khi client ở IdentityServer là confidential client. |
| `Notifications__BoschEmail__ApiKey` | Chỉ khi bật gửi email qua Bosch Email Server API. |

```bash
dotnet build -v q
dotnet run
```

Lần chạy đầu app tự tạo database (EF migration) và seed dữ liệu mặc định: agent, prompt, quyền.

Trên Linux/macOS đặt `AgentWorkspace__RootPath` trỏ tới một thư mục có quyền ghi (mặc định là đường
dẫn Windows).

---

## Tech stack

| Thành phần | Công nghệ |
|---|---|
| Runtime | .NET 8, ASP.NET Core **MVC** (Razor Views) — không có SPA framework |
| ORM | EF Core 8 — SQL Server |
| Agent runtime | Microsoft Agent Framework (`Microsoft.Agents.AI`) |
| LLM | `Microsoft.Extensions.AI` + `...AI.OpenAI` — mọi endpoint OpenAI-compatible |
| Auth | Cookie + SSO OpenID Connect (IdentityServer); phân quyền tự xây |
| Logging | Serilog; OpenTelemetry opt-in |

## Bố cục repo

```
Program.cs        Điểm vào: Serilog bootstrap, middleware, gọi DbInitializer
Extensions/       NƠI DUY NHẤT đăng ký DI
Domain/           Entity + enum. Không phụ thuộc tầng nào
Contracts/        DTO hợp đồng Services ↔ Application
Configuration/    POCO cấu hình bind từ appsettings
Data/             AppDbContext, DbInitializer (migrate + seed)
Application/      Use case theo khu vực màn hình — một thao tác = một class
Services/         Việc kỹ thuật tái dùng: LLM, agent, tool, workflow, prompt, artifacts
Controllers/      MVC controller mỏng
Views/ wwwroot/   Razor view + css/js thuần theo màn hình
Prompts/          Template prompt .md (copy ra output khi build)
Templates/        BRD/SRS/FSD .docx
```
