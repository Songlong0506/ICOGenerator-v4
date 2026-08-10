# Màn hình, endpoint & bảo mật

## Các màn hình & endpoint

Route mặc định: `{controller=Projects}/{action=Index}/{id?}`. Mọi endpoint yêu cầu đăng nhập (fallback
policy) trừ nơi ghi `[AllowAnonymous]`. Quyền ghi ở cột phải; action ghi thêm quyền riêng nghĩa là
*chồng lên* quyền controller.

| Màn hình | Controller | Actions chính | Quyền |
|---|---|---|---|
| **Login** | `Account` | `GET Login` (AllowAnonymous — SSO thì Challenge sang IdentityServer, Local thì tự đăng nhập), `GET ReAuth`, `POST Logout`, `GET AccessDenied` | — |
| **Projects** (trang chủ) | `Projects` | `Index` (lọc theo chủ nếu không có `ProjectsViewAll`), `POST Create`/`Update`, `Mockup` (xem POC sandbox; `review=True` tiêm annotator), `PocReview` (review POC + ghim ghi chú), `GET PocComments`, `POST AddPocComment`/`DeletePocComment`/`ReopenPocComment`, `GET PocShareLinks`/`SearchAssociates`, `POST CreatePocShareLink`/`RevokePocShareLink`, `POST TriagePocFeedback` (phân loại ghi chú cho hộp xác nhận), `POST DispatchPocFeedback` (gửi Dev chỉnh demo / gửi BA sửa tài liệu theo đúng tập con), `POST AcceptPoc` (nghiệm thu bản demo), `DownloadSource` (zip) | `ProjectsView`; Create: `ProjectsCreate`; thêm ghi chú POC: `ProjectsView` (như Feedback — quyền View đủ để gửi phản hồi của mình); xóa: chủ ghi chú hoặc `DeliveryAdvance` |
| **Requirements** (workspace chat BA) | `Requirements` | `Index`, `POST ChatStream` (SSE — đường chat chính, stream token), `POST Chat` (fallback postback), `GET ChatReplyStatus`, `POST UploadSource`/`DeleteSource`, `GET SourceContent`, `POST WriteRequirement`, `POST ReopenCoverage`, `POST CheckConflicts`/`ResolveConflicts`, `POST ReviseBrief`, `POST Approve`, `POST ConfirmAssumptions`/`ReviseAssumptions` (cổng giả định), `POST NewChat`, `POST RetryWorkflow`, `GET WorkflowStatus`/`WorkflowStream` (SSE), `GET DocumentRevisions`/`DocumentRevisionDiff`/`DocumentPreview`/`DownloadDocument` | `RequirementsView`; mọi thao tác ghi: `RequirementsManage` |
| **Chia sẻ POC ra ngoài** | `PocShare` | `GET poc-share/{token}`, `GET {token}/demo`, `GET/POST {token}/comments` — **`[AllowAnonymous]`**, tách hẳn controller riêng để bề mặt ẩn danh không lẫn vào controller có quyền | — (bảo vệ bằng token + hạn dùng của `PocShareLinks`) |
| **Agent Dashboard** (điều phối delivery) | `AgentDashboard` | `Index`, `GET WorkflowStatus`/`ActiveAgents`/`AgentStats`/`AgentActivity`/`AgentCallLogs`/`CallLogDetail`/`CallLogImage`/`DocumentPreview`, `POST ApproveStage`/`RejectStage`/`RequestRevision`/`RetryWorkflow`/`UpdateDeliveryConfig` | `AgentsView`; các POST cổng duyệt: `DeliveryAdvance` |
| **Agents** (cấu hình agent) | `Agents` | `Index`, `Checklist` (bật/tắt bài học BA), `POST Update` (model, temperature, tools...) | `AgentsView` / `AgentsManage` |
| **AI Models** | `Models` | `Index`, `POST Create`/`Update`/`Delete`/`TestConnection` | `ModelsView` / `ModelsCreate`/`Edit`/`Delete`; `TestConnection` cần `ModelsCreate` HOẶC `ModelsEdit` |
| **Usage** (chi phí LLM) | `Usage` | `Index(year?)` — theo model/project/tháng + roll-up phòng ban | `UsageView` |
| **Delivery Quality** | `Quality` | `Index(year?)` — thông lượng, rework, độ tin cậy model | `QualityView` |
| **Prompt Evals** | `Evals` | `Index(runPromptKey?, runStatus?, page, pageSize)`, `POST CreateScenario`/`UpdateScenario`/`DeleteScenario`/`StartRun`/`CancelRun`/`DeleteRun`, `GET RunStatus`/`RunDetail`/`Compare` | `EvalView` / `EvalManage` |
| **Prompt Studio** | `Prompts` | `Index`, `Detail`, `Diff`, `Download`, `POST Save`/`Activate`/`RevertToFile` | `PromptView` / `PromptManage` |
| **Feedback** | `Feedback` | `Index`, `POST Submit` (kèm files), `POST UpdateStatus` (triage), `GET Attachment`, `POST Delete` | `FeedbackView` / `FeedbackManage` |
| **Notifications** | `Notifications` | `Index`, `GET Feed` (chuông poll), `GET Open` (đánh dấu đọc + đi tới link), `POST MarkAllRead`, `GET/POST Preferences` | chỉ cần đăng nhập (dữ liệu tự lọc theo username) |
| **Settings** | `Settings` | `Index`, `POST Update` — sửa `AllowedCommands`, `AllowedFileExtensions`... ghi ngược vào appsettings qua `AppSettingsFileStore` | `SettingsView` / `SettingsManage` |
| **Roles & Permissions** | `Roles` | `Index` (ma trận), `POST Update` | `AdministrationManageRoles` (mặc định chỉ Admin) |
| **User Roles** (gán vai trò trên IdentityServer) | `UserRoles` | `Index`, `Roles`, `SearchUsers`, `UsersByRole`, `POST Assign`/`Withdraw` — gọi REST API của IS4, **chỉ chạy khi `Authentication:Provider = IdentityServer`** | `UserRolesView` / `UserRolesManage` |
| **Audit Log** | `Audit` | `Index` (lọc category/thời gian) | `AuditView` |
| — | `Home` | `Error` (AllowAnonymous) | — |

---

## Bảo mật: đăng nhập, phân quyền, rào chắn

### Xác thực — hai provider, không có mật khẩu trong app

App **không tự quản mật khẩu**: bảng `AppUser` không có cột `PasswordHash`, và không có form
username/password. Đăng nhập rẽ theo cờ `Authentication:Provider` (đổi **không cần build lại**):

| Provider | Hành vi |
|---|---|
| `Local` (mặc định) | `GET /Account/Login` **tự phát cookie** theo tài khoản `superadmin` seed sẵn — dành cho dev/nội bộ. SuperAdmin có toàn quyền và không tự khóa được nên máy dev luôn đủ quyền |
| `IdentityServer` | Challenge sang SSO OpenID Connect của Bosch. Sau khi IdP xác thực, `SsoUserProvisioner` tra user theo claim `username` (≈ NTID) rồi **đồng bộ nguyên tập vai trò** từ role claim mỗi lần đăng nhập (IdP là nguồn sự thật). Đơn vị tổ chức lấy từ claim `department` vào `AppUser.OrgUnitName`. Ánh xạ claim → `UserRole` khai báo bằng attribute `[SsoRoleClaim]` ngay trên enum `UserRole`, không còn bảng mapping trong appsettings |

Rào chắn chung của tầng web:

- Cookie auth: `LoginPath=/Account/Login`, hết hạn 8h **sliding**, `HttpOnly`, `SameSite=Lax`,
  `Secure=Always` (Development thì `SameAsRequest` để chạy HTTP local).
- **Fallback authorization policy**: *mọi* endpoint đòi đăng nhập trừ khi gắn `[AllowAnonymous]` —
  controller mới quên `[Authorize]` vẫn an toàn. Quan trọng vì trang Settings sửa được `AllowedCommands`.
- **Antiforgery tự động**: `AutoValidateAntiforgeryTokenAttribute` global — mọi POST đều được
  CSRF-protect kể cả khi quên attribute.
- Security headers trên mọi response: `X-Content-Type-Options: nosniff`, `X-Frame-Options: SAMEORIGIN`,
  `Referrer-Policy: no-referrer`. Không đặt CSP global (inline script hiện có); HTML do LLM sinh được
  sandbox ở endpoint `Projects/Mockup` riêng.

### Phân quyền chiều DỌC — role × quyền mức hành động

- `UserRole`: **SuperAdmin / Admin / TeamDev / User** — *khác hẳn* `AgentRoleKey` (vai của AI).
- **Một người giữ nhiều role** (bảng nối `AppUserRole`, mỗi vai trò phát một claim `ClaimTypes.Role`);
  quyền hiệu lực = **HỢP quyền** của tất cả vai trò, vì quyền giữa các vai trò *giao nhau* chứ không
  lồng nhau — **không được** rút gọn về vai trò "cao nhất". Claim rỗng khi đăng nhập SSO thì **giữ
  nguyên** vai trò cũ, để một mapping thiếu không vô tình hạ quyền.
- Quyền mức hành động: enum `AppPermission` (28 quyền — xem cột "Quyền" ở bảng trên). `PermissionCatalog`
  (`Domain/Security`) gom quyền theo màn hình để render ma trận + lọc menu sidebar.
- **Một nguồn sự thật**: `IPermissionService` (cache MemoryCache; **SuperAdmin implicit-all** nên không
  có dòng nào trong `RolePermissions` và không tự khóa được, **Admin cấu hình được** như TeamDev/User),
  dùng bởi filter `[RequirePermission(...)]` và `_Layout.cshtml`. Truyền nhiều quyền
  (`[RequirePermission(A, B)]`) = **cần một trong số đó** (OR); muốn buộc đủ cả thì xếp nhiều attribute (AND).
- Cấu hình runtime ở màn Roles; lưu xong `InvalidateCache()` ⇒ **hiệu lực ngay, không cần đăng nhập lại**.
  Thiếu quyền ⇒ `/Account/AccessDenied`.
- **Thêm quyền mới**: thêm giá trị `AppPermission` → khai báo vào `PermissionCatalog.Screens` → gắn
  `[RequirePermission]` → (nếu là menu) thêm nhánh `@if` trong `_Layout.cshtml` → cân nhắc seed mặc định
  trong `DbInitializer`.

### Phân quyền chiều NGANG — theo project

Phân quyền theo role trả lời "role này có được làm việc X không" và **không** trả lời được "user A có
được đụng project của user B không". Việc đó là của `IProjectAccessGuard` (`Services/Security`), khai
báo trên action bằng **`[RequireProjectAccess]`**:

```csharp
[RequireProjectAccess]                                          // đọc "projectId", từ chối bằng 404
[RequireProjectAccess(Denial = ProjectAccessDenial.JsonError)]  // endpoint fetch: { ok=false, error }
[RequireProjectAccess("id", ProjectResource.Document)]          // id là ProjectDocument
[RequireProjectAccess("vm.ProjectId")]                          // id nằm trong view model đã bind
```

Là **action filter** (không phải authorization filter) vì id cần lấy sau model binding. Người không có
`ProjectsViewAll` chỉ thao tác được project **mình tạo**; ai có `ProjectsViewAll` (TeamDev/Admin mặc
định) pass ngay, không tốn thêm query. Từ chối luôn trả về **giống hệt** trường hợp "không tồn tại"
(redirect về danh sách / 404) để không xác nhận sự tồn tại của tài nguyên với người ngoài — chặn truy
cập chéo bằng GUID đoán/lộ qua URL/log.

`ProjectAccessCoverageTests` **fail build** nếu một action nhận `projectId` mà quên khai báo — quên
guard không làm gãy gì cả nên không có chốt chặn này thì lỗ hổng không ai thấy. Action nào không diễn
tả được bằng attribute thì tự kiểm tra trong thân hàm và phải khai báo lý do ở danh sách `Exempt` của
test đó (hiện chỉ có `AgentDashboardController.DocumentPreview`).

> **Thêm endpoint theo project**: action mới nhận `projectId` (hoặc id tài nguyên con) phải gắn
> `[RequireProjectAccess]` — chọn `Denial` khớp thứ client đang chờ (404 mặc định / `RedirectToProjects`
> cho trang / `JsonError` cho endpoint fetch). Xem các action hiện có trong `RequirementsController` làm mẫu.

### Bảo vệ bí mật & dữ liệu

- `AiModel.ApiKey` mã hóa AES trong DB (`AesApiKeyProtector`); khóa từ `Encryption__ApiKeyKey`
  (fail-fast nếu thiếu). Giá trị không có prefix mã hóa được passthrough (tiện seed/test).
- Bí mật chỉ nạp qua env/user-secrets: GitHub PAT, SMTP password, IdentityServer client secret, URL repo Bosch private.
- Prompt BA **không chứa PII** của Associates — chỉ dữ liệu gộp, tên thật chỉ ở vai trò HoD/manager.
- `AuditLogger` ghi nhật ký thay đổi cấu hình (Settings/Roles/Agent/Model/Prompt) kèm actor + before/after JSON.
