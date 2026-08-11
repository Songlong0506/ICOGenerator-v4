# Tham chiếu cấu hình

Mọi key, ý nghĩa và mặc định. Override bằng biến môi trường theo cú pháp `Section__Key` (hai gạch dưới).

| Key | Mặc định | Ý nghĩa |
|---|---|---|
| `Database:Provider` | `SqlServer` | `SqlServer` hoặc `Sqlite`. Chạy Sqlite bằng env var `Database__Provider=Sqlite`. Sqlite mà connection string vẫn dạng SQL Server ⇒ tự fallback file `ICOGenerator.db` |
| `ConnectionStrings:DefaultConnection` | `Server=SONGLONG;...` | Chuỗi kết nối. SqlServer bật `EnableRetryOnFailure` |
| `AgentWorkspace:RootPath` | `C:\Study App\ICOGeneratorWorkspaces` | Thư mục gốc workspace agent. **Phải đổi theo máy** |
| `AllowedCommands` | dotnet, git status/diff/add/commit/push/checkout/remote get-url, dir, npm, node | Whitelist lệnh cho `RunCommand` |
| `AllowedFileExtensions` | .cs .cshtml .css .scss .ts .js .json .md .txt .sln .csproj .html .sql .yml .yaml | Whitelist đuôi file cho tool file |
| `BoschTemplate:BackendRepoUrl/FrontendRepoUrl/Branch` | trống | Repo khung Bosch để clone skeleton; trống = bỏ qua. Nạp URL private qua env |
| `Commands:TimeoutSeconds` | 120 | Timeout mỗi lệnh RunCommand |
| `Workers:MaxConcurrentAgentTasks` | 1 | Số agent task chạy song song tối đa của `AgentTaskWorker` (trần cứng 16). Mặc định 1 = tuần tự như trước (an toàn cho LLM tự host); tăng 2–4 khi endpoint model chịu tải song song. Mỗi project luôn chỉ một task một thời điểm |
| `Feedback:UploadRootPath` | trống ⇒ `{ContentRoot}/FeedbackUploads` | Nơi lưu file đính kèm feedback |
| `Feedback:MaxFileBytes` / `MaxFilesPerFeedback` | 50MB / 8 | Trần file đính kèm |
| `PullRequest:RemoteName/BaseBranch/GitHubToken` | origin / main / trống | Bước PR: token trống hoặc remote không phải GitHub ⇒ fallback link compare |
| `Notifications:BaseUrl` | trống | URL gốc app để dựng link tuyệt đối trong Teams/email |
| `Notifications:Teams:{Enabled,WebhookUrl}` | tắt | Incoming Webhook Teams. Fail-open |
| `Notifications:Email:{Enabled,Host,Port,UseStartTls,Username,Password,From,To}` | tắt / 587 STARTTLS | SMTP. Password qua env. Fail-open |
| `Notifications:BoschEmail:{Enabled,BaseUrl,SendMailApi,ApiKey,FromEmail,To,OnlySendToTesterEmail,TesterEmail}` | tắt / `api/Email` | Email Server API nội bộ Bosch (HTTP) thay SMTP. ApiKey qua env. `OnlySendToTesterEmail` = chốt an toàn non-prod. Fail-open |
| `Llm:Proxy:{Enabled,Address}` | false / `http://127.0.0.1:3128` | Proxy công ty cho lời gọi LLM ra ngoài (client "proxied"); code mặc định coi Enabled=true nếu **thiếu key** — appsettings hiện đặt tường minh false. Proxy chết thì "Test Connection" gọi tên proxy thay vì đổ cho endpoint ([llm-and-prompts.md](llm-and-prompts.md#thêm-một-model-mới)) |
| `Poc:RuntimeCheck:{Enabled,BrowserPath,AutoInstall,AutoInstallTimeoutSeconds}` | true / trống / true / 300 | Tầng chạy POC trong Chromium headless ([getting-started.md](getting-started.md#chromium-cho-tầng-kiểm-poc)). `BrowserPath` trống ⇒ dùng bộ Playwright của máy, chưa có thì tự tải một lần (`AutoInstall`). Fail-open toàn phần |
| `Budget:{Enabled,Period,SystemUsdLimit,PerProjectUsdLimit}` | true / Monthly / 0 / 0 | Trần chi phí USD. 0 = không giới hạn scope đó (opt-in thực tế) |
| `Encryption:ApiKeyKey` | ⚠️ có giá trị commit sẵn | **Bắt buộc nạp qua env**; khóa cũ trong git history coi như đã lộ — xoay khóa trên môi trường thật |
| `Serilog:*` | Console + File `Logs/ico-.log` xoay ngày, giữ 14 ngày, 50MB/ngày | Mức log/sink đổi không cần build |
| `Otel:{Enabled,ServiceName,OtlpEndpoint}` | false / ICOGenerator / trống ⇒ gRPC `localhost:4317` | OpenTelemetry opt-in. Đừng bật khi chưa có collector — dev/demo chạy `docker compose -f docker-compose.otel.yml up -d` là có sẵn |

> Màn hình **Settings** trong app sửa được một phần cấu hình này lúc runtime (qua `AppSettingsFileStore` ghi ngược vào file) — vì vậy trang Settings được bảo vệ chặt (`SettingsManage`, mặc định chỉ Admin).
