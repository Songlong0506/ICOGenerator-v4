# Màn hình, endpoint & bảo mật

## Các màn hình & endpoint

Route mặc định: `{controller=Projects}/{action=Index}/{id?}`. Mọi endpoint yêu cầu đăng nhập (fallback
policy) trừ nơi ghi `[AllowAnonymous]`. Quyền ghi ở cột phải; action ghi thêm quyền riêng nghĩa là
*chồng lên* quyền controller.

| Màn hình | Controller | Actions chính | Quyền |
|---|---|---|---|
| **Login** | `Account` | `GET Login` (AllowAnonymous — SSO thì Challenge sang IdentityServer, `LocalRole` có giá trị thì tự đăng nhập), `GET ReAuth`, `POST Logout`, `GET AccessDenied` | — |
| **Projects** (trang chủ) | `Projects` | `Index` (lọc theo chủ nếu không có `ProjectsViewAll`; cột `Status` hiện chặng của dự án — badge dùng chung `Shared/_ProjectStatusBadge`, xem [overview.md](overview.md#chặng-của-dự-án-projectstatus)), `POST Create`/`Update`/`Clone` (nhân bản dự án), `Mockup` (xem POC sandbox; `review=True` tiêm annotator), `PocReview` (review POC + ghim ghi chú), `GET PocComments`, `POST AddPocComment`/`WithdrawPocComment`/`ReopenPocComment`, `GET PocShareLinks`/`SearchAssociates`, `POST CreatePocShareLink`/`RevokePocShareLink`, `POST TriagePocFeedback` (phân loại ghi chú cho hộp xác nhận), `POST DispatchPocFeedback` (gửi Dev chỉnh demo / gửi BA sửa tài liệu theo đúng tập con), `POST AcceptPoc`/`WithdrawPocAcceptance` (nghiệm thu bản demo và rút nghiệm thu — nghiệm thu KHOÁ chat BA + ghi chú POC, xem [workspace-and-poc.md](workspace-and-poc.md#nghiệm-thu-là-công-tắc-hai-chiều-và-nó-khoá-nội-dung)), `DownloadSource` (zip) | `ProjectsView`; Create + Clone: `ProjectsCreate`; thêm ghi chú POC: `ProjectsView` (như Feedback — quyền View đủ để gửi phản hồi của mình); thu hồi (chỉ ghi chú còn `Open`; chưa gửi đi ⇒ xoá thật, đã đi một vòng ⇒ đóng dấu `WithdrawnAtUtc`): chủ ghi chú hoặc `DeliveryAdvance`; nghiệm thu/rút nghiệm thu: `RequirementsManage` |
| **Requirements** (workspace chat BA) | `Requirements` | `Index`, `POST ChatStream` (SSE — đường chat chính, stream token), `POST Chat` (fallback postback), `GET ChatReplyStatus`, `POST UploadSource`/`DeleteSource`, `GET SourceContent`, `POST WriteRequirement`, `POST ConfirmColumnMap` (sửa bảng cột của file bảng tính — BA đã chốt và lưu ở lượt đọc file; endpoint chỉ lưu, không gửi tin nhắn), `GET PermissionMatrix` + `POST ConfirmPermissionMatrix` (popup xem/sửa bảng phân quyền mở từ nút «Xem bảng phân quyền» dưới khung Tiến độ khai thác — BA soạn ngầm bảng này ở bước "Write Requirement"; POST chỉ lưu, không gửi tin nhắn), `GET UxBlueprint` (popup chỉ đọc «Thiết kế màn hình» — bản thiết kế UX BA/UiUx dựng ở bước "Write Requirement": menu theo vai, màn + hành động, hành trình, phát hiện), `POST ConfirmFlowMap` (bảng người dùng chốt trong chat — bảng luồng; bản thiết kế UX và bảng báo cáo không có endpoint ghi vì BA/UiUx soạn ngầm, người dùng góp ý thiết kế ở POC Review), `POST ReviseBrief`, `POST Approve`, `POST NewChat`, `POST RetryWorkflow`, `GET WorkflowStatus`/`WorkflowStream` (SSE), `GET DocumentRevisions`/`DocumentRevisionDiff`/`DocumentPreview`/`DownloadDocument` | `RequirementsView`; mọi thao tác ghi: `RequirementsManage`. `ChatStream`/`NewChat`/`UploadSource`/`ReviseBrief` còn bị khoá khi bản demo đã được nghiệm thu — xem [workspace-and-poc.md](workspace-and-poc.md#nghiệm-thu-là-công-tắc-hai-chiều-và-nó-khoá-nội-dung). |
| **Chia sẻ POC ra ngoài** | `PocShare` | `GET poc-share/{token}`, `GET {token}/demo`, `GET/POST {token}/comments` (chỉ góp ý của chính link; thêm vào bản nháp), `POST {token}/comments/send` (Send Note), `POST {token}/comments/{id}/delete` (xoá bản nháp chưa gửi) — **`[AllowAnonymous]`**, tách hẳn controller riêng để bề mặt ẩn danh không lẫn vào controller có quyền | — (bảo vệ bằng token + hạn dùng của `PocShareLinks`) |
| **Thử model khác** (hộp thoại trên POC Review) | `PocVariants` | `GET List` (bảng so sánh bản chính + biến thể, kèm danh sách model), `POST Start` (xếp hàng một biến thể dựng lại bản demo bằng model được chọn), `POST Delete` (xoá/huỷ biến thể chưa chạy hoặc đã xong), `GET Demo` (mở bản demo của biến thể — cùng `PocDemoResponse`/CSP sandbox với bản chính). Cơ chế ở [workspace-and-poc.md](workspace-and-poc.md#thử-model-khác-cho-developer-biến-thể-poc) | `ProjectsView` **và** `PocModelExperiment` (hai attribute xếp chồng ở cấp controller — mặc định chỉ Admin vì mỗi lượt tiêu token thật). Không cần `RequirementsManage`/`DeliveryAdvance`: thí nghiệm không đổi gì của dự án. Nút "Thử model khác" trên command bar ẩn khi thiếu quyền |
| **Agent Dashboard** (điều phối delivery) | `AgentDashboard` | `Index`, `GET WorkflowStatus`/`ActiveAgents`/`AgentStats`/`AgentActivity`/`AgentCallLogs`/`CallLogDetail`/`CallLogImage`/`CallLogExport`/`CallLogTurnExport`/`DocumentPreview`, `POST ApproveStage`/`RejectStage`/`RequestRevision`/`RetryWorkflow`/`UpdateDeliveryConfig` | `AgentsView`; các POST cổng duyệt: `DeliveryAdvance` |
| **Agents** (cấu hình agent) | `Agents` | `Index` (`?checklist=true` mở sẵn popup checklist), `Checklist(role)` (bật/tắt bài học của MỘT vai — nút nằm trong panel của vai đang chọn, chỉ hiện với vai có đường học: xem `LearnedChecklistRoles`; trên màn Agents nút mở **popup** nạp từ `ChecklistModal(role, agentId)`, trang riêng là đường lùi khi JS tắt), `POST Update` (model, tools...), `POST SaveChecklist(role, ..., returnAgentId)` (có `returnAgentId` ⇒ quay về Agents và mở lại popup) | `AgentsView` / `AgentsManage` |
| **AI Models** | `Models` | `Index` (kèm khối **Model theo vai trò** — [suất model dùng chung](llm-and-prompts.md#suất-model-dùng-chung-model-theo-vai-trò)), `POST Create`/`Update`/`Delete`/`TestConnection`/`UpdateSlot` | `ModelsView` / `ModelsCreate`/`Edit`/`Delete`; `TestConnection` cần `ModelsCreate` HOẶC `ModelsEdit`; `UpdateSlot` cần `ModelsEdit` (thiếu quyền thì dropdown của khối chỉ để xem) |
| **Usage** (chi phí LLM) | `Usage` | `Index(year?)` — theo model/project/tháng + roll-up phòng ban | `UsageView` |
| **Delivery Quality** | `Quality` | `Index(year?)` — thông lượng, rework, độ tin cậy model | `QualityView` |
| **Prompt Evals** | `Evals` | `Index(runPromptKey?, runStatus?, page, pageSize)`, `POST CreateScenario`/`UpdateScenario`/`DeleteScenario`/`StartRun`/`CancelRun`/`DeleteRun`, `GET RunStatus`/`RunDetail`/`Compare`; kiểm toán trọng tài: `GET JudgeAudits`/`JudgeAudit(id)`, `POST StartJudgeAudit`/`CancelJudgeAudit` | `EvalView` / `EvalManage`; kiểm toán trọng tài cần **thêm** `ProjectsViewAll` (trang hiện lại trích hội thoại của mọi project) |
| **Prompt Studio** | `Prompts` | `Index`, `Detail`, `Diff`, `Download`, `POST Save`/`Activate`/`RevertToFile` | `PromptView` / `PromptManage` |
| **Feedback** | `Feedback` | `Index`, `POST Submit` (kèm files), `POST UpdateStatus` (triage), `GET Attachment`, `POST Delete` | `FeedbackView` / `FeedbackManage` |
| **Notifications** | `Notifications` | `Index`, `GET Feed` (chuông poll), `GET Open` (đánh dấu đọc + đi tới link), `POST MarkAllRead`, `GET/POST Preferences` | chỉ cần đăng nhập (dữ liệu tự lọc theo username) |
| **Settings** | `Settings` | `Index`, `POST Update` — sửa `AllowedCommands`, `AllowedFileExtensions`, trần ngân sách LLM (`Budget`)... ghi ngược vào appsettings qua `AppSettingsFileStore` | `SettingsView` / `SettingsManage` |
| **User Roles** (gán vai trò trên IdentityServer) — bảng role IS4 (`Roles`) nạp khi vào trang; bấm một role mở popup người dùng của role đó (`UsersByRole`), trong popup tìm user LDAP để gán (`Assign`) và xóa từng hàng (`Withdraw`) | `UserRoles` | `Index` (vào được khi có `UserRolesView` **hoặc** `AdministrationManageRoles`), `Roles`, `SearchUsers`, `UsersByRole` (`UserRolesView`), `POST Assign`/`Withdraw` (`UserRolesManage`) — gọi REST API của IS4, **chỉ chạy ở chế độ SSO (`IdentityServer:LocalRole` trống)** | `UserRolesView` / `UserRolesManage` |
| **Roles & Permissions** — popup mở bằng nút chính **Permissions** trên command bar của User Roles, không còn mục menu riêng | `Roles` | `Index` (chỉ chuyển hướng tới `/UserRoles#permissions` cho link cũ), `POST Update(roles, granted)` (lưu ma trận rồi quay về User Roles — chỉ thay quyền của các role đang hiện trên popup). Ma trận do `UserRolesController.Index` nạp sẵn qua `GetRolePermissionMatrixQuery` khi người dùng có quyền; không có thì nút Permissions bị ẩn. **Cột = danh sách role IS4** (cùng nguồn `RolesByAPIName` với bảng role của trang) — xem [ma trận theo role IS4](#ma-trận-quyền-theo-role-identityserver) | `AdministrationManageRoles` (mặc định chỉ Admin) |
| **Logs** — một mục menu, hai tab là link đổi trang (`Views/Logs/_LogsTabs`), tab thiếu quyền bị ẩn | `Logs` | `Index` (chỉ chuyển hướng tới tab đầu tiên được xem: Audit Log, không có thì System Logs) | `AuditView` **hoặc** `SystemLogView` (cổng cấp controller nhận một trong hai) |
| ↳ tab **Audit Log** | `Logs` | `Audit(category?, page, pageSize)` — nhật ký thay đổi cấu hình, lọc theo category | `AuditView` (attribute riêng trên action) |
| ↳ tab **System Logs** | `Logs` | `SystemLogs(level?, q?, page, pageSize)` — log Warning/Error đã chép vào DB, lọc theo mức tối thiểu + tìm phía server trong message/logger/exception/URL, popup chi tiết có nút Copy. Cơ chế ở [operations.md](operations.md#log-warningerror-trong-db-màn-system-logs) | `SystemLogView` (mặc định Admin + TeamDev; quyền riêng vì exception lộ đường dẫn máy chủ, tên bảng, trích dữ liệu) |
| **Hangfire Dashboard** (job theo lịch — đồng bộ dữ liệu tổ chức) | *(middleware Hangfire, không phải controller)* | `/hangfire` — xem lịch `organization-sync`, lịch sử chạy, lỗi; bấm chạy ngay / xoá job. Cơ chế ở [architecture.md](architecture.md#job-chạy-theo-lịch-đi-qua-hangfire) | **Chỉ Admin** (`HangfireDashboardAuthorizationFilter` kiểm role, không qua bảng quyền). Chưa đăng nhập ⇒ về trang Login; vai trò khác ⇒ 403 |
| — | `Home` | `Error` (AllowAnonymous) | — |

---

## Bảo mật: đăng nhập, phân quyền, rào chắn

### Xác thực — hai chế độ, không có mật khẩu trong app

App **không tự quản mật khẩu**: bảng `AppUser` không có cột `PasswordHash`, và không có form
username/password. Đăng nhập rẽ theo **`IdentityServer:LocalRole`** (đổi **không cần build lại**):

| `LocalRole` | Hành vi |
|---|---|
| Có giá trị (repo này mặc định `Admin`) | Cục bộ: `GET /Account/Login` **tự phát cookie** cho user mang **đúng tên vai trò** đó (vd `TeamDev`; tra không phân biệt hoa/thường, chưa có thì tự tạo qua `SsoUserProvisioner` như luồng SSO — DB không seed user nào) và phát claim Role là **role IS4 tương ứng** của vai trò đó (`[SsoRoleClaim]`, vd `TeamDev` ⇒ `HCP_CBO_API.CBO.TEAMDEV`) — dành cho dev/nội bộ. Không có IdP nên **config là nguồn duy nhất của vai trò** ở chế độ này; Admin có toàn bộ quyền từ seed (DB mới) và không tự khóa được ma trận quyền. Nút Logout bị ẩn. Mỗi lần khởi động log `Warning` để máy triển khai không lỡ giữ giá trị dev |
| Trống `""` / thiếu key (repo team) | SSO: challenge sang OpenID Connect của Bosch. Sau khi IdP xác thực, `SsoUserProvisioner` tra user theo claim `username` (≈ NTID) và đồng bộ đơn vị tổ chức từ claim `department` vào `AppUser.OrgUnitName`. **Vai trò không đi qua DB**: mỗi role claim của IdP thành một claim `ClaimTypes.Role` của phiên, giữ **nguyên tên role IS4** (chỉ chuẩn hoá trim + viết HOA — `RoleNames.Normalize`), không ánh xạ qua enum nào ⇒ role mới tạo bên IS4 dùng được ngay |

Rào chắn chung của tầng web:

- Cookie auth: `LoginPath=/Account/Login`, hết hạn 8h **sliding**, `HttpOnly`, `SameSite=Lax`,
  `Secure=Always` (Development thì `SameAsRequest` để chạy HTTP local).
- **Fallback authorization policy**: *mọi* endpoint đòi đăng nhập trừ khi gắn `[AllowAnonymous]` —
  controller mới quên `[Authorize]` vẫn an toàn. Quan trọng vì trang Settings sửa được `AllowedCommands`.
- **Antiforgery tự động**: `AutoValidateAntiforgeryTokenAttribute` global — mọi POST đều được
  CSRF-protect kể cả khi quên attribute.
- Security headers trên mọi response: `X-Content-Type-Options: nosniff`, `X-Frame-Options: SAMEORIGIN`,
  `Referrer-Policy: no-referrer`. Không đặt CSP global (inline script hiện có); HTML do LLM sinh được
  sandbox ở endpoint riêng — `Projects/Mockup` (có đăng nhập) và `poc-share/{token}/demo` (khách). Cả hai
  đi qua **`Controllers/PocDemoResponse`**: header CSP `sandbox` (cố ý KHÔNG có `allow-same-origin`) chỉ
  được viết ở đúng một chỗ, nên siết rào là siết cho cả hai đường cùng lúc. Trang HTML người dùng upload làm
  tài liệu nguồn (mockup) cũng là HTML không tin cậy trên origin của app: `Requirements/SourceContent` gắn
  **cùng hằng số** `PocDemoResponse.SandboxCsp` khi mở bản gốc của nó — thiếu thì script trong file chạy với
  phiên đăng nhập của người xem (stored XSS).

### Phân quyền chiều DỌC — role × quyền mức hành động

- **Role = tên role của API resource trên IdentityServer** (vd `HCP_CBO_API.CBO.ADMIN`), dạng chuẩn hoá
  `RoleNames.Normalize` (trim + viết HOA) dùng chung cho claim của phiên, cột `RolePermission.Role` và cột của
  ma trận — *khác hẳn* `AgentRoleKey` (vai của AI). Enum `UserRole` (**Admin / TeamDev / User**) chỉ còn là
  các role **dựng sẵn**: giá trị hợp lệ của `LocalRole`, role Admin giữ ô khóa (`RoleNames.Admin`), và seed.
- **Vai trò KHÔNG được lưu ở DB** — không bảng, không cột. Chúng chỉ tồn tại trong claim của phiên đăng
  nhập, dựng lại từ đầu ở mỗi lần đăng nhập. Hệ quả phải nhớ: **không truy vấn được "ai đang giữ vai trò
  X"** cho người đang offline, nên mọi thứ cần biết vai trò của người vắng mặt phải dùng tiêu chí khác
  (xem `NotificationService`).
- **Một người giữ nhiều role** (mỗi vai trò phát một claim `ClaimTypes.Role`); quyền hiệu lực = **HỢP
  quyền** của tất cả vai trò, vì quyền giữa các vai trò *giao nhau* chứ không lồng nhau — **không được**
  rút gọn về vai trò "cao nhất". Role chưa có dòng nào trong ma trận đơn giản không cấp gì. Đăng nhập SSO
  mà IdP không phát role claim nào ⇒ phiên **không có quyền gì** (không còn rơi về vai trò mặc định) kèm một
  **log cảnh báo**, để người vận hành biết vì sao user đụng AccessDenied.
- Quyền mức hành động: enum `AppPermission` (30 quyền — xem cột "Quyền" ở bảng trên). `PermissionCatalog`
  (`Domain/Security`) gom quyền theo màn hình để render ma trận + lọc menu sidebar.
- **Một nguồn sự thật**: `IPermissionService` (cache MemoryCache; **mọi role cấu hình được** —
  không có role toàn quyền ngầm định. Chống tự khóa: Admin **luôn** có `AdministrationManageRoles`
  (`PermissionService.AdminLockedPermission`) dù DB không có dòng, popup hiện ô đó dạng khóa),
  dùng bởi filter `[RequirePermission(...)]` và `_Layout.cshtml`. Truyền nhiều quyền
  (`[RequirePermission(A, B)]`) = **cần một trong số đó** (OR); muốn buộc đủ cả thì xếp nhiều attribute (AND).
- Cấu hình runtime ở popup **Permissions** của trang User Roles; lưu xong `InvalidateCache()` ⇒ **hiệu lực ngay, không cần đăng nhập lại**.
  Thiếu quyền ⇒ `/Account/AccessDenied`.
- **Thêm quyền mới**: thêm giá trị `AppPermission` → khai báo vào `PermissionCatalog.Screens` → gắn
  `[RequirePermission]` → (nếu là menu) thêm nhánh `@if` trong `_Layout.cshtml` → seed `DbInitializer` tự
  cấp cho Admin (seed toàn bộ catalog). Seed chỉ chạy khi `RolePermissions` còn trống, nên quyền mới muốn Admin của hệ
  thống ĐANG CHẠY có ngay thì migration phải tự chèn dòng `('HCP_CBO_API.CBO.ADMIN', <quyền>)` khi bảng đã có
  dữ liệu (mẫu: migration `ThuModelChoPoc` cho `PocModelExperiment`, `ThemBangSystemLogs` cho `SystemLogView` —
  hai migration đó chèn `'Admin'` vì chạy TRƯỚC `DoiCotRoleThanhTenRoleIdentityServer`, migration đổi tên cũ
  sang role IS4).

### Ma trận quyền theo role IdentityServer

Popup **Roles & Permissions** không có danh sách role cứng. Mỗi lần mở trang (người có
`AdministrationManageRoles`), `SyncRolePermissionsWithIdentityServerUseCase` gọi
`IdentityServerService.GetAllRolesAsync` (cùng endpoint `RolesByAPIName` với bảng role của trang), rồi
`GetRolePermissionMatrixQuery` lấy mỗi role IS4 làm một cột — tạo role mới bên IS4 rồi mở lại trang là cấu hình
quyền được ngay. Tiêu đề cột là nhãn ngắn (`RoleNames.ShortLabel`: đoạn sau dấu chấm cuối), tên đầy đủ ở tooltip.
Cột **Admin** (`RoleNames.Admin`) luôn có vì giữ ô khóa.

**Role đã mất bên IS4 bị xoá quyền khỏi DB** ngay trong lượt đó (ghi audit `Role/Delete` kèm danh sách quyền
đã xoá) — để quyền mồ côi không tự rơi vào tay người giữ một role IS4 tạo lại trùng tên sau này. Chốt chặn:
chỉ xoá khi danh sách IS4 lấy được và **không rỗng** (`GetAllRolesAsync` trả rỗng cả khi IS4 trả mã lỗi — coi
rỗng là "không biết", nếu không một lần IS4 trục trặc sẽ xoá sạch ma trận), và **không bao giờ** xoá quyền của
role Admin.

IS4 lỗi / không với tới / phiên hết hạn / trả rỗng, hoặc đang ở chế độ cục bộ (bỏ qua lời gọi — không có
access_token, và BaseURL không với tới sẽ treo trang tới timeout) ⇒ không xoá gì, trang vẫn mở, popup lấy cột
từ các role đang có quyền trong DB + Admin và báo "Không lấy được danh sách role từ IdentityServer". Vì vậy form gửi kèm hidden `roles` (các cột đang hiện):
`UpdateRolePermissionsUseCase` **chỉ thay quyền của những role đó**, role vắng mặt trên popup giữ nguyên. Người
chỉ có `AdministrationManageRoles` (không có `UserRolesView`) vẫn được đọc *tên* role IS4 qua đường này — cần
để cấu hình — nhưng không chạm được người dùng của role.

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

- `AiModel.ApiKey` và `Project.GitAccessToken` mã hóa AES trong DB (`AesApiKeyProtector`); khóa từ `Encryption__ApiKeyKey`
  (fail-fast nếu thiếu). Giá trị không có prefix mã hóa được passthrough (tiện seed/test).
- Bí mật chỉ nạp qua env/user-secrets: SMTP password, IdentityServer client secret, URL repo Bosch private.
- Git token của dự án là ô **chỉ-ghi** ở popup *Config delivery*: trang chỉ hiện "đã lưu", không bao giờ render
  giá trị; ô trống khi lưu = giữ nguyên, muốn xoá thì tích "Xoá token đã lưu". Nhân bản dự án **không** chép token.
- Prompt BA **không chứa PII** của Associates — chỉ dữ liệu gộp, tên thật chỉ ở vai trò HoD/manager.
- `AuditLogger` ghi nhật ký thay đổi cấu hình (Settings/Roles/Agent/Model/Prompt) kèm actor + before/after JSON.
