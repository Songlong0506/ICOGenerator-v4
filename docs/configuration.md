# Tham chiếu cấu hình

Mọi key, ý nghĩa và mặc định. Override bằng biến môi trường theo cú pháp `Section__Key` (hai gạch dưới).

**`appsettings.json` chỉ giữ key phải đổi theo máy/môi trường hoặc mang giá trị khác mặc định của code**
(DB, đăng nhập, SSO, workspace, whitelist lệnh/đuôi file, timeout + proxy LLM, khóa mã hóa, Serilog). Mọi key
khác trong bảng dưới **vắng mặt = dùng mặc định ở cột "Mặc định"** — cột đó là mặc định *trong code*, không phải
giá trị đang ghi trong file. Muốn bật một tính năng opt-in (Notifications, Otel, Budget...) thì thêm section
tương ứng vào file hoặc nạp bằng biến môi trường; bí mật (token, mật khẩu, ApiKey) luôn nạp bằng biến môi trường.

| Key | Mặc định | Ý nghĩa |
|---|---|---|
| `Authentication:Provider` | `Local` | `Local` (tự đăng nhập, dev/nội bộ) hoặc `IdentityServer` (bắt buộc SSO). Đổi **không cần build lại** — xem [screens-and-permissions.md](screens-and-permissions.md#xác-thực--hai-provider-không-có-mật-khẩu-trong-app) |
| `Authentication:LocalUsername` | `superadmin` | Tài khoản `AppUser` mà chế độ `Local` tự đăng nhập vào (khớp không phân biệt hoa/thường). Không tìm thấy ⇒ trả 500 kèm tên đang tìm, thay vì phát cookie trống. Bị bỏ qua ở chế độ `IdentityServer` |
| `Authentication:LocalRole` | `SuperAdmin` | Vai trò phát cho phiên `Local`. **Nguồn duy nhất của vai trò ở chế độ này** — DB không lưu vai trò. Đặt thấp hơn để thử màn hình dưới góc nhìn quyền hạn chế. Bị bỏ qua ở chế độ `IdentityServer` (vai trò lấy từ role claim) |
| `Database:Provider` | `SqlServer` | `SqlServer` hoặc `Sqlite`. Chạy Sqlite bằng env var `Database__Provider=Sqlite`. Sqlite mà connection string vẫn dạng SQL Server ⇒ tự fallback file `ICOGenerator.db` |
| `ConnectionStrings:DefaultConnection` | — (bắt buộc với SqlServer) | Chuỗi kết nối. SqlServer bật `EnableRetryOnFailure` |
| `IdentityServer:{BaseURL,APIName,Client_Id}` | trống | Authority, API scope xin thêm (ngoài openid/profile/email), client id — chỉ đọc khi `Authentication:Provider=IdentityServer` ([screens-and-permissions.md](screens-and-permissions.md#xác-thực--hai-provider-không-có-mật-khẩu-trong-app)). Ánh xạ role claim → `UserRole` nằm ở attribute `[SsoRoleClaim]`, không ở đây. Redirect URI đăng ký ở IdP: `{UrlApp}/signin-oidc`, post-logout `{UrlApp}/signout-callback-oidc` |
| `IdentityServer:ClientSecret` | trống ⇒ client public | Chỉ cho confidential client; nạp qua env `IdentityServer__ClientSecret` |
| `IdentityServer:ResponseType` / `RequireHttpsMetadata` | `token id_token` / false | Theo mẫu Bosch (implicit; IdP nội bộ chứng chỉ tự ký) |
| `IdentityServer:{RolesByAPIName,IdentityServerUser,RoleForUser,WithdrawalRole,UserByName}` | — (không có mặc định) | Đường dẫn REST quản trị IS4 cho trang User Roles, tương đối với `BaseURL`; `{0}` trong `UserByName` là từ khóa tìm. Token gọi API là access_token của phiên SSO |
| `AgentWorkspace:RootPath` | `C:\Study App\ICOGeneratorWorkspaces` | Thư mục gốc workspace agent. **Phải đổi theo máy** |
| `AllowedCommands` | dotnet, git status/diff/add/commit/push/checkout/remote get-url, dir, npm, node | Whitelist lệnh cho `RunCommand` |
| `AllowedFileExtensions` | .cs .cshtml .css .scss .ts .js .json .md .txt .sln .csproj .html .sql .yml .yaml | Whitelist đuôi file cho tool file |
| `BoschTemplate:BackendRepoUrl/FrontendRepoUrl/Branch` | trống | Repo khung Bosch để lấy skeleton — clone ra thư mục tạm rồi **chép nội dung** (bỏ `.git`) vào repo đích của dự án; URL trống = bỏ qua, `Branch` trống = nhánh mặc định của repo. Nạp URL private qua env |
| `Commands:TimeoutSeconds` | 300 | Timeout mỗi lệnh `RunCommand` do agent gọi. 120s cũ cắt ngang đúng lệnh đắt nhất (`npm install` một skeleton Angular) |
| `Commands:BuildTimeoutSeconds` | 600 | Timeout mỗi lệnh của **cổng biên dịch** (app tự chạy sau bước Implementation — xem [delivery-pipeline.md](delivery-pipeline.md#cổng-biên-dịch-implementation--buildfix)). Rộng hơn vì lúc đó agent đã xong, chỉ tốn thời gian tường |
| `Workers:MaxConcurrentAgentTasks` | 1 | Số agent task chạy song song tối đa của `AgentTaskWorker` (trần cứng 16). Mặc định 1 = tuần tự như trước (an toàn cho LLM tự host); tăng 2–4 khi endpoint model chịu tải song song. Mỗi project luôn chỉ một task một thời điểm |
| `Feedback:UploadRootPath` | trống ⇒ `{ContentRoot}/FeedbackUploads` | Nơi lưu file đính kèm feedback |
| `Feedback:MaxFileBytes` / `MaxFilesPerFeedback` | 50MB / 8 | Trần MỖI file / số file mỗi phản hồi. Danh sách định dạng được phép cố định trong `FeedbackAttachmentStore` |
| `PullRequest:RemoteName/BaseBranch/GitHubToken` | origin / main / trống | Bước PR: token trống hoặc remote không phải GitHub ⇒ fallback link compare. `BaseBranch` cũng là tên nhánh đầu tiên khi một repo đích phải được `git init`. Token CHỈ dùng để gọi API tạo PR — quyền `git push` lấy từ credential của máy chủ (khóa SSH / credential helper), xem [delivery-pipeline.md](delivery-pipeline.md#bước-pull-request) |
| `PullRequest:CommitterName/CommitterEmail` | `ICOGenerator` / `icogenerator@localhost` | Danh tính commit đặt cho repo đích khi máy chủ **chưa** có `user.name`/`user.email` — thiếu nó thì `git commit` fail ở tận bước cuối pipeline. Máy chủ đã cấu hình sẵn thì hai key này không được đụng tới |
| `Notifications:BaseUrl` | trống | URL gốc app để dựng link tuyệt đối trong Teams/email. Cả section `Notifications` là kênh **ngoài**, opt-in; chuông in-app luôn chạy, không phụ thuộc nó |
| `Notifications:Teams:{Enabled,WebhookUrl}` | tắt | Incoming Webhook Teams. Fail-open |
| `Notifications:Email:{Enabled,Host,Port,UseStartTls,Username,Password,From,To}` | tắt / 587 STARTTLS | SMTP qua MailKit. Password qua env. Fail-open. `UseStartTls=true` + `Port=465` ⇒ implicit TLS (`SslOnConnect`); `true` + cổng khác ⇒ STARTTLS; `false` ⇒ không mã hóa |
| `Notifications:BoschEmail:{Enabled,BaseUrl,SendMailApi,ApiKey,FromEmail,To,OnlySendToTesterEmail,TesterEmail}` | tắt / `api/Email` | Email Server API nội bộ Bosch (HTTP) thay SMTP. ApiKey qua env. `To` gộp với email cá nhân đã opt-in. `OnlySendToTesterEmail` = chốt an toàn non-prod (lọc mọi người nhận về `TesterEmail`). Fail-open |
| `Llm:RequestTimeoutSeconds` | 600 (appsettings đặt 1800) | Deadline TỔNG cho một lời gọi model. 1800 vì bước POC sinh ~60k token trên model tự host — xem [workspace-and-poc.md](workspace-and-poc.md) |
| `Llm:TestConnectionTimeoutSeconds` | 30 | Deadline nút **Test Connection** ở trang Models |
| `Llm:ClaudeCode:BinaryPath` | trống | Đường dẫn lệnh `claude` cho model có Endpoint `claude-code://…` ([llm-and-prompts.md](llm-and-prompts.md#claude-qua-claude-code-cli-gói-claude-promax-chỉ-cho-máy-cá-nhân)). Trống ⇒ tự tìm: PATH, các chỗ trình cài hay đặt, rồi bản đi kèm extension Claude Code của VS Code |
| `Llm:Proxy:{Enabled,Address}` | true / `http://127.0.0.1:3128` (appsettings đặt proxy Bosch) | Proxy công ty cho lời gọi LLM tới endpoint không phải localhost (client "proxied"). Lưu ý code coi Enabled=true khi **thiếu key** — máy không ngồi sau proxy phải đặt tường minh `Llm__Proxy__Enabled=false`. Proxy chết thì "Test Connection" gọi tên proxy thay vì đổ cho endpoint ([llm-and-prompts.md](llm-and-prompts.md#thêm-một-model-mới)) |
| `Llm:Proxy:UseDefaultCredentials` | false (appsettings đặt true) | Trả lời `407` bằng tài khoản Windows đang chạy app (Negotiate/NTLM) ⇒ trỏ **thẳng** `Address` vào proxy công ty, không cần relay px/CNTLM. Chỉ có tác dụng trên Windows; chạy như service thì tài khoản service phải có quyền qua proxy |
| `Llm:Proxy:BypassList` | `[]` (appsettings đặt `.bosch.com`) | Host đi thẳng, không qua proxy: host đầy đủ (`llm.corp.local`) hoặc hậu tố tên miền (`.bosch.com`) — **không phải regex** (`LlmProxy` tự escape). Tương đương `no_proxy`; thiếu nó thì bật proxy là giết mọi model tự host nội bộ |
| `Llm:SourceUpload:{MaxFileBytes,MaxHtmlFileBytes}` | 10MB / 30MB | Trần cỡ MỖI file tài liệu nguồn lúc upload. HTML có trần riêng vì mockup xuất "tự chứa" nhồi font/thư viện/ảnh base64 — xem [requirement-flow.md](requirement-flow.md#tài-liệu-nguồn-ảnh-và-call-log). Cả request upload vẫn chặn ở 60MB (`RequestSizeLimit` của `UploadSource`) |
| `Llm:SourceUpload:{MaxImagesPerCall,MaxTotalImageBytes,MaxTextCharsPerFile}` | 12 / 20MB / 20.000 | Trần phần ảnh (số ảnh, dung lượng gói tin) và phần chữ (ký tự mỗi nguồn, cắt từ cuối) của MỘT lời gọi model, cộng dồn trên mọi nguồn của project — xem chú thích dưới bảng |
| `Llm:CallLog:{StoreRequestImages,MaxImageBytesPerCall}` | true / 20MB | Lưu lại ảnh đã gửi kèm mỗi lời gọi để xem lại ở Model Invocation Detail. Ảnh ghi ra **đĩa** trong workspace (`99_CallLogs/{logId}/`), DB chỉ giữ mô tả trong `RequestJson`; chết cùng workspace khi xóa project. Để `MaxImageBytesPerCall` bằng `Llm:SourceUpload:MaxTotalImageBytes` thì mọi ảnh đã gửi đều lưu được |
| `Poc:RuntimeCheck:{Enabled,BrowserPath,AutoInstall,AutoInstallTimeoutSeconds}` | true / trống / true / 300 | Tầng chạy POC trong Chromium headless ([getting-started.md](getting-started.md#chromium-cho-tầng-kiểm-poc)). `BrowserPath` trống ⇒ dùng bộ Playwright của máy, chưa có thì tự tải một lần (`AutoInstall`). Fail-open toàn phần |
| `Poc:VisualCheck:Enabled` | true | Bật/tắt bước model vision soi ảnh chụp POC (`PocVisualReviewer`) |
| `Budget:{Enabled,Period,SystemUsdLimit,PerProjectUsdLimit}` | true / Monthly / 0 / 0 | Trần chi phí USD ([supporting-features.md](supporting-features.md#budget-guard)). `Period`: `Monthly` \| `Daily` \| `Total` (không reset). 0 = không giới hạn scope đó (opt-in thực tế). Sửa được ở trang Settings (lưu lần đầu sẽ tạo section trong file), có hiệu lực ngay |
| `Encryption:ApiKeyKey` | ⚠️ có giá trị commit sẵn | **Bắt buộc nạp qua env**; khóa cũ trong git history coi như đã lộ — xoay khóa trên môi trường thật |
| `Serilog:*` | Console + File `Logs/ico-.log` xoay ngày, giữ 14 ngày, 50MB/ngày | Mức log/sink đổi không cần build |
| `Otel:{Enabled,ServiceName,OtlpEndpoint}` | false / ICOGenerator / trống ⇒ gRPC `localhost:4317` | OpenTelemetry opt-in. Đừng bật khi chưa có collector — dev/demo chạy `docker compose -f docker-compose.otel.yml up -d` là có sẵn |

**Trần ảnh của `Llm:SourceUpload`.** Ảnh chỉ đi kèm cho tới khi BA mô tả xong hình thành chữ
(`ProjectSourceFile.VisionSummary`), nên đây là trần *một lần* cho mỗi tài liệu chứ không nhân theo số lượt chat.
`MaxImagesPerCall = 12` để khớp trần hình bóc từ một file Word (`WordDocumentTextExtractor.MaxImages`). Hai trần
khác bản chất:

- `MaxImagesPerCall` là trần **token**: hạ xuống 4–6 khi chạy model vision context nhỏ (8–16k) — prompt phình vì
  ảnh không làm request lỗi, nó bóp ngân sách output xuống đáy 256 token và BA trả lời cụt (`MaxOutputTokenResolver`).
- `MaxTotalImageBytes` là trần **kích thước gói tin**: ảnh đi dạng base64 (+33%) trong một request JSON, đặt cao
  là gateway/proxy trước endpoint đóng kết nối giữa lúc đẩy body và lời gọi chết không có mã HTTP
  (`Retry failed after 4 tries…`) — triệu chứng "gửi tin thì được, đính kèm .docx nhiều ảnh thì lỗi". Chỉ hạ
  (4MB, hoặc 1MB khi endpoint đứng sau nginx mặc định `client_max_body_size`) khi thật sự gặp triệu chứng đó.

Vượt trần thì các ảnh sau bị bỏ và câu ghi chú tự nói đúng "kèm 5/12 hình" — BA vẫn trả lời được, chỉ thiếu hình.

> Màn hình **Settings** trong app sửa được một phần cấu hình này lúc runtime (qua `AppSettingsFileStore` ghi ngược vào file) — vì vậy trang Settings được bảo vệ chặt (`SettingsManage`, mặc định chỉ Admin).
