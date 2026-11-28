# Workspace & sản phẩm sinh ra

## Bố cục

Mỗi project một thư mục dưới `AgentWorkspace:RootPath`, tên = `{tên-đã-chuẩn-hóa}-{8-ký-tự-đầu-của-Id}` (không đụng nhau khi hai tên chuẩn hóa giống nhau):

```
{RootPath}/{project-key}/
  01_Requirement/     # Product Brief (draft/ + V1, V2...), BRD/SRS/FSD/UserStories
  02_Design/          # AI Design Spec theo V{n}
  03_Architecture/    # Đề xuất kiến trúc của Tech Lead
  04_Implementation/  # poc-demo.html (POC) + poc-ui-conventions.json + src/ (code đa file, LÀ GIT REPO — xem "Repo đích của dự án") + code-review.md + build-report.md (kết quả cổng biên dịch)
  05_Test/            # Test cases + báo cáo test
```

(Danh sách phase khai báo ở `Services/Artifacts/ProjectWorkspaceLayout.cs`; mỗi phase có `draft/` và các thư mục version `V{n}`.)

Biến thể POC thử model **không** nằm trong cây này mà ở `{RootPath}/.poc-variants/{projectId:N}/{variantId:N}/` — xem [Thử model khác cho Developer](#thử-model-khác-cho-developer-biến-thể-poc).

File đính kèm của tab Feedback cũng nằm dưới thư mục gốc, ở `{RootPath}/.feedback-uploads/{feedbackId:N}/` (`WorkspacePathResolver.GetFeedbackUploadPath`). Dấu chấm giữ nó không trùng key dự án; tool agent chỉ thấy thư mục project nên không đụng tới. Hệ quả cần biết: dọn/sao lưu thư mục gốc là dọn/sao lưu cả file feedback. `StoredPath` là đường dẫn tuyệt đối nên đổi `RootPath` trên màn Settings không làm hỏng việc tải file cũ, và xóa phản hồi dọn theo `StoredPath` chứ không theo gốc hiện tại.

## POC demo

- File `04_Implementation/poc-demo.html` — seed từ `Prompts/Design/poc-template.html` ở bước PocPreview; hai vùng marker do `PocTemplate.cs` quản: `POC_CONTENT` (HTML) và `POC_SCRIPT` (JS; shell expose `window.pocToast`/`window.pocNavigate`/`window.pocRole`/`window.pocSetRole`).
- **Giao diện chung do shell lo, không do model** (`<style>` của template, khối "Modern content layer"): shell theme lại chính các class Bootstrap mà model vốn đã viết — nền trang xám nhạt với bề mặt trắng, `.table-responsive` thành thẻ trắng bo góc (prompt đã bắt bọc mọi bảng trong nó; nằm trong `card`/`modal` thì phẳng lại để khỏi hộp lồng hộp), badge trạng thái nhạt màu chữ đậm (có chấm đầu khi nằm trong ô bảng), tab gạch chân một dòng cuộn ngang, nút/ô nhập bo góc. Cách này nâng MỌI bản demo mới lên cùng lúc mà không cần model học markup mới, và không phụ thuộc model nhớ luật trình bày. Rule `.nav-item` của sidebar là global nên từng đè lên `<li class="nav-item">` của tab Bootstrap (padding sidebar, nền hover tối — các tab cách nhau ~40px); shell reset nó trong `.page`/`.modal`. Mọi code kiểm/lái menu đều chọn qua `.sidebar-nav .nav-item` nên reset này không chạm tới chúng. Ô bảng chỉ chứa MỘT token (mã, ngày, số tiền — không khoảng trắng, ≤24 ký tự) được script shell gắn `text-nowrap`: bảng nhiều cột bị ép thì trình duyệt ngắt "JD-0013"/"2024-04-01" ở dấu gạch, và CSS không phân biệt được một giá trị với một đoạn chữ. Script trang hay vẽ lại dòng bằng `innerHTML`, nên shell theo dõi `.page` bằng `MutationObserver` (chỉ `childList`, gom vào một khung hình). Phần bố cục mà theme không tự suy ra được thì prompt dạy qua vài khối dựng sẵn, mục "BỐ CỤC THEO DẠNG MÀN" của `poc-preview.v1.md`: `.page-head` ở đầu mọi màn, `.toolbar` + `.search-box` trên bảng, ô hai dòng `.cell-main`/`.cell-sub`, `.avatar`, `.kv` cho màn chi tiết, `.steps` cho vị trí của bản ghi trong luồng duyệt, `.timeline` cho lịch sử, `.empty-state`, `.board` cho kanban. Prompt cũng chốt bảng màu badge THEO NGHĨA trạng thái (một trạng thái không đổi màu giữa hai màn) và cấm tự tô bằng `style="…"`. Dòng rỗng của engine CRUD dùng `.empty-state`.
- **Bộ dashboard dựng sẵn trong shell** (CSS `.kpi-grid`/`.kpi`/`.chart-card`/`.meter` + `window.pocChart(target, spec)` trong script shell): prompt cấm nạp thư viện ngoài, nên trước đây màn tổng quan chỉ là dãy `card` có con số trơn hoặc biểu đồ model tự chế bằng div — đúng màn người xem dùng để đánh giá bản demo "có chuyên nghiệp không". `pocChart` vẽ SVG không phụ thuộc gì: `bar` (nhóm/`stacked`), `line`, `area`, `hbar`, `donut` (≤6 phần, phần dư gom "Other"), `spark` (xu hướng nhỏ trong thẻ KPI); tự lo trục, lưới, chú thích, nhãn giá trị chọn lọc và tooltip. Màu lấy từ `--chart-1…8` theo **thứ tự chuỗi** (xanh Bosch đầu tiên; thứ tự đã kiểm độ tách màu cho người mù màu giữa các màu kề nhau), không bao giờ sinh màu thứ 9. Biểu đồ nằm trên màn đang ẩn được vẽ khi màn mở (`ResizeObserver`); lần vẽ lại bị dời sang khung hình kế để observer không báo lỗi "ResizeObserver loop" — lỗi đó đi qua listener `error` và tầng runtime sẽ tính là lỗi JS của demo. Biểu đồ KHÔNG chèn bảng ẩn cho trình đọc màn hình (tầng runtime đọc mọi `<table>` của màn để soát dữ liệu chéo) mà ghi tóm tắt số liệu vào `aria-label` của SVG. Cách dùng dạy ở mục "MÀN DASHBOARD" của `poc-preview.v1.md`; màn mẫu "Monthly Summary" của template minh hoạ bằng `data-chart` khai báo (template không có script trang).
- Yêu cầu của bước POC: hiện thực **Business Rules của spec thành hành vi thật** (tính toán, validate, chuyển trạng thái, dữ liệu đổi theo vai) chứ không chỉ màn hình tĩnh; agent tự soát bằng `AuditPocContent` (`PocAudit.cs` đối chiếu cả độ phủ với "Screens To Generate" + "BR-n" của spec, do `PocSpec.cs` parse).
- **Phần markup của audit truy vấn DOM thật** (`PocDom` + AngleSharp, CSS selector), không quét chuỗi. Khung vỏ do `PocTemplate` dựng nên đúng là máy sinh, nhưng **nội dung feature bên trong là do model viết** — và đó là loại markup làm regex gãy: thuộc tính đứng trước `class` (`<div data-roles="X" class="nav-item">` không khớp mẫu chuỗi cố định), dấu `>` trong giá trị thuộc tính cắt thẻ sớm, `id="…"` nhắc trong literal JavaScript bị tính là id trùng, `data-id` bị nhận nhầm là `id`, bảng lồng bảng làm phạm vi quét bảng ngoài cụt. Khi regex gãy ở đây nó **không báo lỗi — nó báo "OK"**, đúng chế độ hỏng mà audit sinh ra để chặn. Đi kèm: bỏ bước xóa `<!-- -->`/`<style>`/`<script>` bằng regex trước khi quét (comment là node riêng, nội dung script/style là văn bản) và bộ đếm độ sâu `<div>` tự viết để tìm thẻ đóng. Cây ở đây khớp với cây Chromium mà `PocRuntimeChecker` lái, nên hai tầng kiểm cùng nhìn một thứ. `PocSpec.cs` **không** thuộc diện này — nó parse Markdown của spec, không phải HTML.
- **Mọi hành động của bản thiết kế UX phải có mặt trên demo** (`PocAudit.CheckBlueprintActions`, đọc mục `## 6c. UX Blueprint` qua `PocSpec.Actions`): mỗi dòng của §6c là một hành động có ID tác vụ, màn, vị trí và nhãn — [bản thiết kế](requirement-flow.md#bản-thiết-kế-ux-ba-dựng-danh-mục-tác-vụ-uiux-tổ-chức-màn-hình) đã quyết mỗi bước của mỗi luồng làm ở đâu, nên thiếu một nút là thiếu một bước người dùng không làm được. Nhãn được tìm NGUYÊN VĂN (không phân biệt hoa thường, gộp khoảng trắng) trong section của đúng màn đó, trong modal, hoặc trong script logic (dòng dựng bằng JS chở nút trong template string). Không thấy ở đâu ⇒ ISSUE; chỉ thấy ở chỗ khác trên demo ⇒ WARNING (nút có, nhưng nhiều khả năng nằm sai màn). Đây là thước sớm và chính xác hơn của cùng lỗi mà lượt lái UAT sẽ báo là "thiếu điều khiển" ở một bước. Tầng chấm hình (`UiUx/poc-visual-review.v1.md`) bổ sung phần audit không thấy được: bố cục lệch dạng màn, nút đặt sai vùng.
- **Menu gom theo LÔ, không rải phẳng** (`PocNavGroups` + `PocAudit.CheckNavGrouping`): dự án nào cũng có màn hình theo lô — danh mục "ứng dụng tự quản lý", báo cáo cần màn riêng `"<tên> Report"` (phần báo cáo còn lại gom trên một màn `"… Dashboard"` ở menu gốc — xem [bảng báo cáo](requirement-flow.md#bảng-báo-cáo-gom-về-một-màn-hình-tổng-quan)). Bản thiết kế UX đã gom danh mục đơn giản vào một màn `Master Data` có tab (lint `catalog-sprawl`) và tự gom ≥3 màn danh mục/báo cáo thành một nhóm menu (`UxBlueprintBuilder.Repair`, cùng ngưỡng với cổng này), nên ở đây cổng là lưới thứ hai cho bản demo đi lệch thiết kế. Một dự án nhân sự bình thường có 5–8 danh mục và vài báo cáo riêng, nên để phẳng thì sidebar của bản demo dài gấp đôi phần nghiệp vụ thật và người xem phải cuộn qua một dãy màn CRUD giống hệt nhau mới tới được luồng chính — đúng thứ họ mở demo để xem. Từ **3 màn hình cùng lô** trở lên, `poc-preview.v1.md` bắt gom hết vào ĐÚNG MỘT mục `children` (shell đã có sẵn nhóm xổ xuống), và audit báo ISSUE khi chúng còn nằm trần ở menu gốc hoặc bị chia ra nhiều nhóm. Nhãn NHÓM do agent đặt theo ngôn ngữ UI của spec ("Danh mục"/"Báo cáo"); nhãn mục CON vẫn là tên màn hình nguyên văn vì nó còn phải khớp `data-view` — tiêu đề nhóm KHÔNG phải màn hình nên không có section. Phân loại tất định theo TÊN và cố ý hẹp: `dashboard`/`overview` KHÔNG tính là báo cáo (một "Employee Dashboard" thường là màn chủ của một vai), vì một cổng bắt nhầm là một cổng agent học cách phớt lờ. Mục lá đầu tiên là mục mở demo — tiêu đề nhóm không bao giờ nhận `active` (`PocTemplate.RenderNav`), nếu không thì một menu bắt đầu bằng nhóm mở ra với mục đang sáng không ứng với màn nào. Và chỉ nhóm CHỨA mục đang mở mới xổ sẵn, các nhóm khác để đóng: nhóm đầu tiên luôn xổ sẵn (luật cũ) trả nguyên dãy màn danh mục lên sidebar, đúng thứ việc gom nhóm sinh ra để thu lại. Người xem bấm tiêu đề nhóm để xổ; shell tự mở nhóm khi một màn bên trong được mở bằng code (`activateNav`), nên lượt CLICK MENU và lượt lái UAT không bị nhóm đang đóng chặn.
- **Đổi vai bằng khối VIEW AS, KHÔNG có màn đăng nhập** (`PocTemplate.ReplaceRoles` + engine trong shell template): vai của spec (`## 6b. Permission Matrix`, `PocSpec.Roles` đọc ra) được `SetPocContent('roles')` dựng thành các nút `.view-as-item` ghim **cuối sidebar**, vai đầu tiên là vai mở demo. Quyền xem khai báo bằng `data-roles="Vai A,Vai B"` trên mục menu (`PocNavItem.Roles`) và trên `<section class="page-view">`; không khai báo = mọi vai đều thấy. Shell tự ẩn mục menu ngoài vai, mở màn đầu tiên của vai, đặt `body[data-poc-role]` rồi gọi `window.pocOnRoleChange(role)` / bắn `poc:rolechange` để script nghiệp vụ render lại dữ liệu theo vai. Một màn hình của vai khác được mở BẰNG CODE (`pocNavigate`, lượt kiểm runtime) thì shell chuyển vai theo màn đó thay vì hiện một màn trống.
  **VIEW AS chỉ chứa TÊN VAI, không bao giờ tên người.** Spec hay liệt kê người dùng demo vì có màn lọc theo người đang đăng nhập, và agent từng dựng VIEW AS bằng chính danh sách đó: tên nhân viên thật hiện trên một bản demo đem đi trình bày, còn quyền gắn vào người thì không đối chiếu được với bảng phân quyền (gán thêm người vào vai cũng không thấy gì). Màn "của mình" / "của đơn vị" vì thế coi người đang xem là một bản ghi mẫu cố định của vai (script đọc `pocRole()`), không hiện tên ở bộ chọn. Chặn ở cả hai đầu: prompt spec cấm mục 9 liệt kê persona có tên, và audit báo ISSUE cho mục VIEW AS không phải vai của spec (dưới đây).
  Vì sao bỏ màn Login giả: POC không có backend nên "đăng nhập" chỉ là một cửa gác không kiểm gì — nó bắt người xem demo bấm thêm một lượt trước khi thấy nghiệp vụ, và **giấu luôn phần còn lại khỏi chính các cổng tự kiểm**. Lượt CLICK MENU chỉ thấy được cái form đó; còn lượt lái UAT thì tệ hơn: tập điều khiển của nó không có `<select>` (xem lượt bấm thử bên dưới) nên bước "đăng nhập với vai Quản lý" thực tế bấm nút Đăng nhập với vai mặc định đang chọn — kịch bản của vai này chạy dưới vai khác mà vẫn báo `clicked`. Nay mỗi vai là một nút mang đúng tên vai nên lái đúng vai, và lượt lái chấm bước đổi vai bằng `window.pocRole()` chứ không bằng "chữ trên màn có đổi không" (hai vai có thể cùng mở một màn).
- **Lượt ĐỔI VAI** (`PocRuntimeChecker.CheckRoleSwitchingAsync`, chạy sau lượt CLICK MENU): bấm thật từng vai và báo ISSUE khi vai không đổi được, khi **sidebar trống trơn với vai đó**, hoặc khi đổi vai xong vẫn đứng ở màn hình của vai khác. Phần tĩnh (`PocAudit.CheckRoles`) soát trước những thứ rẻ hơn: spec có ≥2 vai mà demo không khai báo vai nào; **mục VIEW AS không phải vai của spec** (ISSUE — gần như luôn là tên người; tập vai hợp lệ là `PocSpec.Roles` cộng `PocSpec.Actors` đọc từ `## 2. Target Users / Actors`, so khớp mờ như tên màn hình để "Nhân viên" khớp "Nhân viên (người cần tủ)"); `data-roles` gõ sai tên vai (mục/màn hình biến mất với MỌI vai), và một màn Login **spec không hề yêu cầu** (WARNING).
- **Kiểm ở hai bề rộng**: `PocRuntimeChecker` đi qua từng màn hình ở 1440px rồi mở lại toàn bộ ở **390px** (điện thoại) — tràn ngang ở bề rộng nào cũng thành ISSUE, và ảnh mobile cũng được đưa cho tầng Visual QA. Trước đây mọi thứ chỉ kiểm ở desktop nên lớp lỗi "vỡ trên màn hẹp" không cổng nào thấy.
- **Tầng Visual QA chỉ chạy khi kết quả của nó còn dùng được** (`WorkspaceTools.PlanVisualReview`). Đây là lời gọi model đắt nhất của một vòng audit (ảnh mọi màn hình + spec, purpose `UiUxPocVisualReview`); ca JD Library 2026-11-26 chạy nó 4 lần cho một bản demo, hai lần liền nhau cách chưa tới một phút. Ba nhánh: **dùng lại** khi vòng kiểm trước đã chấm đúng bản demo này (`PocVerificationSummary.VisualContentHash` trùng dấu vân tay file — ca thường gặp: audit cuối của agent rồi cổng UAT của worker audit lại bản không đổi; issue/cảnh báo cũ được nối lại vào báo cáo); **hoãn** (`VISUAL CHECK: DEFERRED`) khi demo còn lỗi runtime, kịch bản gãy hoặc thiếu màn của spec — vòng sửa sau đổi chính các màn đó; **chấm** khi còn lại. Issue tĩnh (wiring, ngôn ngữ nhãn…) cố ý không hoãn: chúng có thể còn tới vòng cuối, hoãn theo chúng là bỏ hẳn tầng visual ở đúng những bản demo cần nó nhất.
- **Lượt BẤM THỬ THEO KỊCH BẢN NGHIỆM THU** (`PocRuntimeChecker.DriveUatScenariosAsync`, chạy trước lượt CLICK MENU; kết quả ra `UatDriveResults` và dòng "Máy bấm thử theo kịch bản" trên POC Review): lấy chính bộ kịch bản UAT sinh TRƯỚC khi POC được dựng — **hết cả bộ** (trước đây chỉ 5 kịch bản đầu, kịch bản 6–8 là chỗ cổng mù) — mỗi kịch bản một lần tải trang sạch. Lượt này chỉ ghi kết quả; issue do `PocUatCoverage` sinh, gộp với mục `pocScenarios()` của cùng kịch bản — xem [delivery-pipeline.md](delivery-pipeline.md#kịch-bản-nghiệm-thu-uat-là-đích-của-bước-poc-không-phải-phụ-lục).
  - **Bộ lái là một chương trình JS riêng** (`Services/Artifacts/PocUatDriver.js`, nhúng vào assembly, nạp bằng `AddInitScript`) — `window.__pocUat.act(thao tác, neo)` làm MỘT thao tác của kế hoạch (`UatScenario.StepActions`, xem [delivery-pipeline.md](delivery-pipeline.md#kịch-bản-nghiệm-thu-uat-là-đích-của-bước-poc-không-phải-phụ-lục)) và chỉ trả **sự kiện**: `ok`, `missing` (thứ cần thao tác không có ở đâu trên trang), `dead` (bấm đúng điều khiển mà không có gì đổi), `teleport` (màn không có trên menu, máy mở thẳng), `unresolved` (máy không làm được), `seen`/`notseen` (bước nhìn). Tìm theo **nhãn nguyên văn** (chuẩn hoá NFC, bỏ `*`/`:` cuối nhãn), ưu tiên vùng người dùng đang nhìn: modal đang mở → màn đang mở → cả trang. Ô nhập tìm theo `<label for>`, `<label>` bọc ngoài, `aria-label`/`aria-labelledby`, `placeholder`, và nhãn của khối nhóm chỉ chứa đúng ô đó; giá trị gán qua setter gốc rồi phát `input`+`change`; ô không nhận giá trị (ô ngày nhận chữ) là `unresolved`, không phải lỗi POC. Dòng bảng bắt click bằng `addEventListener` không lộ qua selector nào ⇒ bấm phần tử nhỏ nhất chứa chữ định danh rồi leo lên dòng/thẻ; vì là phỏng đoán nên bấm mà không đổi gì là `unresolved`, không phải nút chết.
  - **"Có phản ứng" = có thứ NHÌN THẤY được đổi**: HTML (cả thuộc tính) của màn đang mở + modal, vị trí cuộn của trang và mọi khung chứa màn, class của `<html>`/`<body>`, `location.hash`. Chỉ so chữ (như bản đầu) thì mọi nút "cuộn tới / tô sáng khung" bị báo oan là chết — ca thật: nút «Xem chi tiết JD» tô viền + cuộn tới bảng lịch sử. Chờ tối đa ~1,2s, dừng sớm ngay khi trang phản ứng. Toast của shell (`#toastHost`) nằm ngoài chữ ký vì shell tự toast cho mọi `.btn`, kể cả nút chưa nối logic.
  - **Chỉ kết luận khi trạng thái còn tin được** (`UatScenarioDrive`, thuần C#, test không cần browser): `missing`/`dead` là khuyết tật CHỈ KHI mọi thao tác trước đó đều `ok`. Gặp `unresolved`/`teleport` thì từ đó máy chạy thử nốt nhưng không chấm (kết quả vẫn PASS, chi tiết ghi "không kết luận được từ bước n (lý do)"), lỗi JS phát sinh sau thời điểm đó cũng không tính cho kịch bản. Bước `check` không thấy chữ chỉ là "lưu ý" trong chi tiết, không bao giờ là lỗi. Báo cáo gãy kèm đường máy đã đi ("trước đó máy đã: chọn vai «HR» → mở màn «Danh sách» → bấm «ĐƠN-09»") — agent sửa không nhớ bản demo, biết đường đi là biết trang đang ở trạng thái nào.
  - **Bộ kịch bản cũ không có kế hoạch** ⇒ `window.__pocUat.legacy(câu, neo)` đoán MỘT điều khiển từ câu: nhãn trong ngoặc (`"…"`, `'…'`, `“…”`, `‘…’`, `«…»`) trước, rồi khớp ngược. Câu nêu từ hai nhãn trở lên ("Nhập Name là 'X', để Status là 'Active', bấm Save") thường là dữ liệu + thao tác mà máy chỉ bấm được một thứ ⇒ không kết luận, và hết tin cậy từ bước đó.
  Bốn luật tìm-điều-khiển giữ cho cổng này không báo oan, mỗi luật vá đúng một đường đã từng đánh trượt cả bộ kịch bản của một POC chạy được:
  - **Mục menu nằm trong tập điều khiển.** `RenderNav` dựng mục menu bằng `<div class="nav-item">` nên nó không thuộc `button/a/.btn`; thiếu nó thì bước "Mở màn hình X" — bước mở đầu của gần như mọi kịch bản — không khớp được gì. Tiêu đề NHÓM bị loại (chỉ xổ/thu, không mở màn nào), mục con của nhóm **chưa xổ** vẫn tính là bấm được vì shell bắt click bằng delegation rồi tự mở nhóm; chỉ mục bị `applyRoleToNav` đặt `display:none` mới là ngoài tầm với. Nhãn đọc bằng `textContent` khi `innerText` rỗng — mục trong nhóm chưa xổ không có innerText.
  - **Nhãn trùng tên một `data-view` ⇒ ưu tiên mục menu** hơn một nút cùng tên trong nội dung: "Mở màn hình 'Duyệt đơn'" phải mở màn, không phải duyệt đại một bản ghi.
  - **Bước đã nêu nhãn trong ngoặc mà nhãn đó không phải điều khiển ⇒ bỏ qua**, không rơi xuống khớp ngược. Khớp ngược (nhãn nút nào xuất hiện nguyên văn trong câu bước) chỉ dùng cho bước KHÔNG có ngoặc — dùng cả cho bước có ngoặc thì "Mở màn hình 'HRBP Approval'" bấm trúng nút vai **HRBP** rồi chấm cú no-op đó là nút chết.
  - **Bấm lại điều khiển đang `active` (vai đang chọn, mục menu đang mở) là bỏ qua, không phải nút chết**: vai đầu tiên chính là vai demo mở lên, nên mọi kịch bản mở đầu bằng "chọn vai <vai mặc định>" sẽ trượt sạch nếu chấm no-op đó là khuyết tật.
- **NEO CHỈ CHỖ cho từng bước nghiệm thu** (`UatAnchor` + cổng `PocUatAnchors`, kiểm TĨNH nên không cần browser): mỗi bước của mỗi kịch bản UAT phải có một phần tử mang `data-uat="{số kịch bản}.{số bước}"` (đánh số **từ 1**, theo đúng thứ tự khối UAT trong prompt; một phần tử phục vụ nhiều bước thì ghi nhiều mã cách nhau bằng dấu cách — đúng ngữ nghĩa `[data-uat~="1.4"]`). Bước thao tác neo vào chính nút/ô nhập/mục menu người dùng bấm (bước nhập cả cụm trường ⇒ thẻ `<form>` bao quanh); bước KIỂM TRA neo vào phần tử **hiển thị kết quả** cần đối chiếu, không được bỏ trống. Cổng báo cả hai phía: bước thiếu neo, và mã neo không ứng với bước nào (agent đánh số 0-based — lỗi im lặng tệ nhất, vì HTML nhìn vẫn "có neo" mà không mã nào tra ra phần tử). Neo phục vụ hai chỗ: trang POC Review tô sáng đúng phần tử khi người nghiệm thu bấm vào một bước, và lượt lái click theo neo **trước** khi rơi xuống lượt tìm theo nhãn chữ — với thao tác `click` khi bước có đúng MỘT thao tác bấm. Lượt lái chỉ nhận phần tử neo BẤM ĐƯỢC: tự nó (và nhãn không lệch hẳn chữ của thao tác), hoặc điều khiển bên trong mang đúng nhãn, hoặc điều khiển DUY NHẤT bên trong. Neo đặt trên một khối gánh neo của nhiều bước không được hiểu thành "bấm nút đầu tiên trong khối" — cách lượt lái từng bấm nhầm một nút bất kỳ rồi đi lạc cả kịch bản.
  Vì sao là cổng chứ không phải "có thì tốt": neo là thứ duy nhất nối một câu tiếng Việt trong checklist với một phần tử cụ thể. Bản đầu tiên của tính năng chỉ chỗ tự **đoán** phần tử bằng cách so từ của câu bước với chữ trên các nút/ô bảng — trùng đúng một từ là đủ để khoanh vàng nhầm chỗ ("Kiểm tra JD được tạo với mã HcP-JD-XXX" khớp trúng một ô bảng bất kỳ có chữ "JD"), còn bước kiểm tra thì vốn không có nút nào để trỏ. Chỉ chỗ SAI tệ hơn không chỉ chỗ, nên đường đoán đã bị gỡ hẳn thay vì siết ngưỡng.
- **Lượt CLICK MENU** (`PocRuntimeChecker.CheckNavClickRoutingAsync`, chạy sau lượt lái UAT): bấm THẬT từng mục menu đang hiển thị và so màn hình đang mở với nhãn mục đó. Lượt đi màn hình ở trên gọi `window.pocNavigate()` bằng JS nên nó MÙ với lớp lỗi "click menu chết" — script nghiệp vụ dựng lại sidebar làm mất handler của shell, hoặc gắn handler riêng gọi `pocNavigate()` ngay trong lúc xử lý click của chính mục đó (click tổng hợp lồng nhau bị cờ *click in progress* của DOM nuốt). Người xem demo thấy breadcrumb đổi mà nội dung đứng yên; nay thành ISSUE. Bản thân shell cũng đã sửa: nav bắt click bằng **delegation** và `pocNavigate` gọi thẳng hàm mở màn thay vì `item.click()`.
- **Dữ liệu mẫu THẬT** (`PocSampleDataCheck`, chạy trong `PocAudit`): text bóc từ Excel/Word người dùng đính kèm vốn CHỈ được nạp vào prompt sinh spec (`RealSampleDataReader` — cùng hàm cho cả hai đầu) mà không có gì kiểm chứng, nên POC vẫn dễ demo bằng "Sản phẩm A / Nguyễn Văn B" — lớp lỗi rẻ nhất để sửa nhưng đắt nhất về niềm tin: người dùng nghiệp vụ mở demo thấy dữ liệu bịa là mất tin, dù mọi công thức đều đúng. Hai phép scan tất định trên vùng `POC_CONTENT` (không tính shell — shell có chữ mẫu riêng), và cố tình dè dặt (chỉ ISSUE khi bằng chứng rõ ràng):
  - **Không dùng gì từ tài liệu** ⇒ ISSUE (kèm vài giá trị thật để agent seed lại); dùng ít ⇒ WARNING; không có tài liệu nào ⇒ bỏ qua hẳn.
  - **Placeholder kinh điển** ("Nguyễn Văn A", "Product B", "Lorem ipsum", `@example.com`) ⇒ ISSUE khi ĐÃ có tài liệu thật để dùng, WARNING khi không.
- **Ngôn ngữ của POC: nhãn giao diện LUÔN tiếng Anh, chỉ nội dung nghiệp vụ được tiếng Việt** (`PocUiLanguageCheck`, chạy trong `PocAudit` ở mọi lượt, không cần bối cảnh). Cùng một luật với giao diện của chính app ([architecture.md](architecture.md#ngôn-ngữ-giao-diện-tiếng-anh-trừ-màn-requirements)), áp cho mọi POC và mọi màn/component mà POC sinh ra sau này, bất kể spec viết bằng ngôn ngữ nào:
  - **Tiếng Anh** — nút, link hành động, tiêu đề trang/khối/modal, nhãn ô nhập, placeholder, tooltip (`title`), `aria-label`, tiêu đề cột, tab, chip trạng thái, dòng gợi ý của dropdown (`-- Select JD --`), nhãn mục và nhãn NHÓM menu (`Catalogs`/`Reports`), tên vai ở VIEW AS, thông báo kiểm lỗi/toast/xác nhận/trạng thái rỗng.
  - **Được tiếng Việt** — NỘI DUNG của bản ghi: dữ liệu mẫu (tên chức danh, phòng ban, mô tả công việc, lý do từ chối), giá trị danh mục trong dropdown, văn bản tài liệu người dùng gửi.
  - Vì sao: bản demo từng trộn "Tạo JD" cạnh "Submit" và "Version History" — đọc như ba ứng dụng ghép lại. Khung tiếng Anh chở dữ liệu tiếng Việt là hình dạng của ứng dụng thật đơn vị sẽ nhận. Luật cũ ("spec tiếng Việt ⇒ UI tiếng Việt, trừ bốn loại TÊN") đã gỡ cùng phép kiểm "UI không có lấy một dấu".
  - Chữ khung đến từ thượng nguồn nên thượng nguồn cũng tiếng Anh: tên màn/đối tượng/thông tin/trạng thái (`ux-blueprint.v1.md`, `ai-design-spec.v1.md` — vốn đã thế), **nhãn hành động** của bản thiết kế UX (`ux-blueprint.v1.md` — POC chép nguyên văn lên nút, `CheckBlueprintActions` đòi khớp đúng chữ), nhãn nhóm menu mà `UxBlueprintBuilder.GroupBatches` tự gom, **tên vai** (`table-flow-map.v1.md` → `permission-matrix-draft.v1.md` → VIEW AS) và quy ước giao diện gộp từ góp ý (`poc-ui-convention.v1.md` giữ ý của góp ý đòi nhãn tiếng Việt bằng một nhãn tiếng Anh rõ hơn). Prompt sinh POC (`poc-preview.v1.md`) và Visual QA (`poc-visual-review.v1.md`) mang cùng định nghĩa.
  - Phép kiểm tất định chỉ nhìn chỗ CHẮC CHẮN là nhãn trong `POC_CONTENT` — `button`/`.btn`, `th`, `label`/`.form-label` (trừ label bọc checkbox/radio: đó là tên một giá trị), `legend`, `.modal-title`, tab, `.badge`, `.dropdown-item`, `option[value=""]`, thuộc tính `placeholder`/`title`/`aria-label` — cộng nhãn nhóm menu; một dấu tiếng Việt ở đó ⇒ ISSUE (gom thành một mục, kèm tối đa 8 ví dụ). Cố ý KHÔNG đọc: tiêu đề `h1`–`h6` (khung chi tiết thường mang tên bản ghi), `option` có giá trị (bản ghi danh mục), nhãn mục lá menu (là tên màn của spec, phải khớp `data-view`), và chữ script dựng lúc chạy (lẫn với dữ liệu seed tiếng Việt hợp lệ — phần đó do prompt và Visual QA giữ).
- **Lịch sử các vòng dựng** (`PocSnapshots`): mỗi task `PocPreview` xong thì `poc-demo.html` được chụp thành `04_Implementation/poc-history/poc-demo.V{n}.html` (giữ 10 bản mới nhất). Vòng "Request revision" ghi đè thẳng lên bản hiện tại, nên không có bản chụp thì người nghiệm thu ở vòng sau chỉ còn bản bàn giao bằng chữ của agent để tin. Trang POC Review liệt kê các vòng (mở lại qua `Mockup?version=n` — cùng quyền + sandbox, số vòng chỉ dùng để tra trong danh sách file có thật) kèm diff **màn hình thêm/bỏ** so với vòng liền trước. Dựng lại POC từ đầu ⇒ `PocSnapshots.Reset` chạy cùng `PocVerification.Reset`.
- **Cổng kịch bản nghiệm thu**: lượt agent xong, worker tự audit thêm một lần và chỉ mở cổng duyệt khi mọi kịch bản UAT đạt — còn gãy thì tự giao Developer sửa ngay trong task (tối đa `MaxPocUatFixRounds` vòng) rồi kiểm lại. Thước là trường `UatIssues` của `poc-verification.json`. Chi tiết: [delivery-pipeline.md](delivery-pipeline.md#cổng-kịch-bản-nghiệm-thu-poc-chỉ-tới-tay-người-dùng-khi-bộ-uat-đạt).
- **Chống hồi quy giữa các vòng sửa**: `poc-verification.json` giữ vòng kiểm mới nhất, các vòng cũ rơi vào `poc-verification-history.json`. Mỗi lượt audit so với vòng trước (`PocVerification.DetectRegressions`) và báo mục từng PASS mà nay FAIL **hoặc biến mất** (xoá assertion cũng bị tính là hồi quy) — mục `REGRESSIONS` trong báo cáo cho agent, và một khối riêng trên trang POC Review. Khi POC được dựng lại từ đầu, `PocVerification.Reset` xoá cả hai file để không so với một bản POC không còn tồn tại.
- **Định dạng các file JSON trong `04_Implementation/`** (`poc-verification.json`, `poc-verification-history.json`, `uat-scenarios.json`): thụt dòng và ghi bằng `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` để tiếng Việt, dấu nháy, `<`/`>` giữ nguyên ký tự thay vì `\uXXXX` — đây là file người review mở ra đọc tay. An toàn vì chúng chỉ được đọc lại bằng `JsonSerializer`, không nhúng thẳng vào HTML. File ghi bằng phiên bản cũ (còn escape) vẫn đọc được bình thường.
- Xem POC: `GET /Projects/Mockup?projectId=` — endpoint **sandbox riêng** (HTML do LLM sinh không được thả vào layout chính).
- **Review POC (ghim ghi chú lên phần tử)**: `GET /Projects/PocReview?projectId=` nhúng POC trong iframe ở chế độ review (`Mockup?review=True` tiêm `wwwroot/js/poc-annotator.js` lúc phục vụ — file trên đĩa không đổi). Người xem bật "chế độ ghim" (icon công tắc `#pinModeBtn` nổi ở góc trên phải khung demo, đứng đầu `.poc-frame-actions` — bên trái "mở tab riêng"; trạng thái bật đọc được ngay trên icon: pin rỗng/đặc, nền xanh bám `aria-pressed`, `aria-label`/`title` đổi theo), click phần tử → annotator gửi mô tả (màn hình `data-view`, nhãn, CSS selector, vị trí %) lên trang cha qua postMessage → lưu bảng `PocComments`. Pin đánh số vẽ ngay trên phần tử. Sandbox giữ nguyên (origin opaque, không cookie) — mọi thao tác ghi đều từ trang cha. Các ghi chú `Open` được gom vào "Request revision" tại cổng POC (xem [delivery-pipeline.md](delivery-pipeline.md#cổng-duyệt-gates--trạng-thái-waitingforhuman)).
  - **Bấm vào một BƯỚC ⇒ POC chỉ chỗ.** `poc-review.js` gửi xuống iframe **mã neo** của bước (`data-anchor="2.3"`, do Razor in ra từ chỉ số gốc kịch bản/bước — cùng `UatAnchor` mà prompt và cổng audit dùng), annotator mở đúng màn hình rồi tô sáng phần tử `[data-uat~="2.3"]`. READ-ONLY: chỉ tô sáng, **không thao tác thay người dùng** — họ vẫn tự bấm để thấy nghiệp vụ chạy thật; và đi theo nhịp người xem (một lần bấm = một lần chỉ chỗ), không có lượt tự chạy hết kịch bản theo đồng hồ. Không tìm được thì **nói thẳng bằng chữ** ngay dưới bước đó (`.uat-step-hint`) chứ không nháy cả trang cho có phản hồi, và phân biệt ba tình huống khác hẳn nhau với người dùng: bản demo **không có neo nào** (dựng trước cơ chế này — đừng bắt họ thử lại), **thiếu đúng bước này** (cổng audit đã báo), và **phần tử chưa hiện** (làm các bước trước đã).
  - **Bố cục hai cột, mỗi cột một câu hỏi.** Cột trái (`.poc-main-col`) là **bản demo + những gì nói về chính bản demo**: khung iframe, rồi ngay dưới nó thẻ "Ghi chú trên POC" — **bảng ghi chú** (xem dưới) và nút đóng vòng "Gửi *n* ghi chú cho đội xử lý". Cột phải (`.poc-comments-panel`) chỉ còn **hướng dẫn sử dụng** + các vòng đã dựng. Ghi chú ở cột trái vì một dòng trong bảng và một pin vẽ trên demo là cùng một thứ nhìn từ hai phía — để chúng ở hai cột là bắt mắt người dùng nhảy qua lại; và vì trước đây nó nằm SAU toàn bộ danh sách kịch bản, nên POC càng nhiều kịch bản thì đúng hai thao tác KẾT THÚC buổi review càng bị đẩy sâu. Hai hệ quả kỹ thuật: (a) `#pocReviewRoot #pocFrame` **cao hơn** trang khách (`calc(100vh - 210px)`, tối thiểu 640px) — số trừ chỉ còn đủ cho header + page-head + command bar, không trừ thêm để chừa chỗ cho thẻ ghi chú phía dưới, vì một khung demo lùn buộc người xem cuộn BÊN TRONG iframe, tức cuộn một trang nằm trong một trang; (b) click một dòng ghi chú sẽ kéo khung demo về tầm mắt (`scrollIntoView`) **chỉ khi** nó đã trôi qua mép trên, nếu không cú nháy pin bên trong iframe là vô hình.
  - **Thẻ chứa khung demo không có padding, và mang ba icon "chế độ ghim" + "mở tab riêng" + "toàn màn hình" ở góc trên phải.** `.poc-frame-panel.card` đặt `padding: 0` (áp cho cả trang khách `#pocShareRoot`): bản demo tự mang supergraphic + topbar + sidebar của nó, nên viền trắng của `.card` chỉ là một cái khung thứ hai lồng ngoài cái khung đã có — và nó ăn mất chiều cao của thứ duy nhất trên trang này đáng được chỗ. Mọi nút chỉ tác động lên bản demo là icon nổi trong `.poc-frame-actions` neo vào **góc trên phải** khung (góc trên trái là header sidebar của chính POC — tên ứng dụng + nút gập `X`; đặt icon bấm được của trang cha sát một nút bấm được của demo là mời bấm nhầm, còn mép phải chỉ có chữ BOSCH tĩnh), không nằm trên command bar: công tắc ghim đổi cách click vào bản demo được hiểu, hai nút kia mở/phóng bản demo — đặt chúng chạm vào thứ chúng tác động thì người dùng khỏi phải đối chiếu một nhãn chữ ở tận thanh lệnh với chỗ mình sắp bấm. Ghim đứng đầu hàng vì là công tắc dùng nhiều nhất trong buổi review; khi trang khoá (đã nghiệm thu) icon bị `disabled`, vẫn hiện để người dùng biết nó ở đâu. Command bar vì thế chỉ còn nút có soi quyền và chỉ render khi có ít nhất một nút. Trang khách (`PocShare/Open`) dùng **cùng hàng icon** (ghim + mở tab riêng, không có toàn màn hình) để người gửi link và người nhận link thấy cùng một chỗ bấm.
  - **Hộp thoại "Technical details"** (nút trên command bar; hàng tab kiểu gạch chân, các tab chia đều bề ngang) gom phần người dùng nghiệp vụ không đọc được. Ba tab đọc — chỉ `DeliveryAdvance`, nội dung server dựng sẵn, JS chỉ đóng/mở và đổi tab: **Máy đã tự kiểm** (vòng tự kiểm cuối), **Đối chiếu spec** (màn hình + business rule chắt từ AI Design Spec, kèm kịch bản UAT kiểm), **Thiết kế màn hình** (bản thiết kế UX `Project.UxBlueprint` chỉ đọc: chỉ số, menu theo vai, mỗi màn một bảng hành động, hành trình từng luồng, phát hiện của lint — xem [requirement-flow.md](requirement-flow.md#bản-thiết-kế-ux-ba-dựng-danh-mục-tác-vụ-uiux-tổ-chức-màn-hình)). Tab không có dữ liệu thì hiện nhưng `disabled`. Tab thứ tư **Try another model** — chỉ `PocModelExperiment` — là bảng so sánh model (xem [Thử model khác cho Developer](#thử-model-khác-cho-developer-biến-thể-poc)); `poc-variants.js` chỉ nạp/poll khi tab này đang hiện (nghe sự kiện `poctech:panelshown`/`poctech:closed` do `poc-review.js` phát trên `#pocTechModal`). Tab mở sẵn theo thứ tự tự kiểm → spec → thiết kế → thử model; nút hiện khi người xem có ít nhất một nhóm tab (ba tab đọc có dữ liệu, hoặc quyền thử model).
    - **Mở tab riêng** là `<a target="_blank">` chứ không phải nút JS — giữ nguyên chuột giữa/Ctrl+click và không phụ thuộc `poc-review.js`.
    - **Toàn màn hình** (`#pocFullscreenBtn`) buộc phải là nút JS, và nó phóng **cả thẻ** `.poc-frame-panel` chứ không phóng riêng iframe: pin ghi chú và khung tô sáng lúc "chỉ chỗ" là do TRANG CHA vẽ đè lên khung demo, phóng mỗi iframe sẽ đẩy chúng xuống dưới lớp toàn màn hình — mất đúng thứ người ta đang review. Hai đường vì Fullscreen API có thể bị chính sách trình duyệt từ chối: đường thật (`requestFullscreen`, CSS `:fullscreen`) và đường dự phòng khi promise reject (`.is-fullscreen` — lớp phủ `position: fixed`, thoát bằng Esc do handler của trang). Trạng thái nút luôn đọc lại từ DOM (`document.fullscreenElement` + class) và nghe `fullscreenchange`, vì người dùng thoát bằng Esc hoặc nút của trình duyệt thì không cú click nào chạy qua handler — một biến cờ riêng sẽ lệch ngay lần đầu.
  - **Cột phải là HƯỚNG DẪN SỬ DỤNG, không phải danh sách câu nghiệm thu.** Ngoài đời, có bản demo rồi thì BA ngồi cạnh và dẫn người dùng đi; panel "📘 Hướng dẫn sử dụng" là buổi dẫn đó viết sẵn: mỗi thẻ là MỘT VIỆC người dùng làm được (một kịch bản của `uat-scenarios.json`), giữ đúng thứ tự file — prompt `uat-scenarios.v1.md` đã xếp kịch bản theo trình tự một buổi demo. Trang không còn khung "câu nghiệm thu" — lý do ở [delivery-pipeline.md](delivery-pipeline.md#kịch-bản-nghiệm-thu-uat-là-đích-của-bước-poc-không-phải-phụ-lục). Panel là **một partial dùng chung** `Views/Shared/_PocGuide.cshtml` cho cả POC Review lẫn trang khách — người gửi link và người nhận link thấy cùng một buổi dẫn. Mã neo `UatAnchor.Token(i, j)` và `data-index` lấy từ chỉ số gốc trong file JSON; đánh số lại là lệch neo agent đã gắn.
  - Đây là bản hướng dẫn để đọc và làm theo, **không phải phiếu nghiệm thu**: không có checkbox, không đếm tiến độ, không gom nhóm theo màn hình. Tiêu đề panel đếm số việc và **không gập** (panel là lý do cột phải tồn tại). Bước vừa bấm để xem chỗ được tô nền (`li.is-current`) — mỗi lúc chỉ một bước — để người xem nối chỗ đang sáng trên bản demo với câu đang đọc. Chỗ nào chưa đúng thì ghi qua Add Note của bảng ghi chú.
  - **Mỗi thẻ là một `<details>` gập được**, server mặc định chỉ mở thẻ đầu tiên; trên POC Review đóng/mở tay nhớ trong `localStorage` theo project (`poc-uat-open-<projectId>`, khóa là `data-index`), trang khách thì không nhớ. Lý do gập: một POC thật có 8 kịch bản × 5-7 bước, mở hết là hơn năm mươi dòng trong một cột hẹp. Tiêu đề thẻ là chữ trong `.uat-title` (đậm 600), **không bọc `<b>`**: `<b>` là `bolder` nên từ 600 nhảy lên 900, mặt chữ đó của Bosch Sans thiếu glyph tiếng Việt và các chữ có dấu rơi sang font khác.
  - **Thẻ kịch bản không có nút báo lỗi riêng.** Nút "Chỗ này chưa đúng" (ô nhập chuyển vào trong thẻ, ghi chú render trong thẻ) đã gỡ: mọi ghi chú đi qua **Add Note** của bảng, chọn đúng màn hình. Hai đường nhập cho cùng một việc là hai chỗ người dùng phải nhớ ghi chú của mình nằm ở đâu. Ghi chú kịch bản đã lưu từ trước (`ElementLabel = "Kịch bản: <title>"`) vẫn là một dòng của bảng, cột Item need fixing in nguyên nhãn đó.
  - **Bảng ghi chú thay cho hai tab "Đang xử lý" / "Lịch sử"** (partial `Views/Shared/_PocNotesTable.cshtml` + `wwwroot/js/poc-notes.js`, **dùng chung với trang khách**). Cột: `#` · Product Brief Version · Screen · Item need fixing · Note · Date Note · User Note · Status · Action. Mỗi ghi chú là MỘT dòng suốt đời của nó — hai tab cũ bắt người dùng đoán một ghi chú đang nằm tab nào, và cùng một ghi chú từng bị in ở cả hai. Hai câu hỏi của hai tab cũ nay là hai bộ lọc: Status ("còn gì phải gửi?" = Draft) và Version ("bản V{n} từng bị chê gì?"). Bảng **luôn hiện**, kể cả khi chưa có ghi chú: danh sách rỗng là một dòng "No items to display." trải hết các cột (lọc không còn dòng nào thì "No notes match the filter.") — để người dùng thấy ngay bảng sẽ chứa gì thay vì một câu chữ lơ lửng. Nút Add Note cao bằng hai dropdown lọc bên cạnh.
    - Nguồn: `ListPocCommentsQuery` với `includeBriefNotes: true` — ghi chú POC **và** ghi chú Brief (Screen = "Product Brief", Item = đoạn trích `Quote`), bỏ dòng đã thu hồi mềm. Bảng không có dòng "vòng Dev sửa" và không hiện bàn giao của Dev (`AddressedNote` vẫn được ghi, chỉ không hiển thị); `GetPocNoteHistoryQuery` đã gỡ.
    - **Status là phép chiếu của `PocCommentStatus`** (`PocNoteDisplayStatus.Of`, tính ở server): `Open` → **Draft** (chưa gửi cho đội xử lý); `Sent` / `Deferred` / `RoutedToRequirement` → **Waiting** (đã gửi, chưa áp); `Addressed` → **Apply**. Ghi chú Brief: Waiting khi còn đóng dấu `draft`, Apply khi bản nháp nó chê đã được duyệt thành `V{n}`. Enum KHÔNG đổi (lưu DB dạng chuỗi); chi tiết đường đi (Dev đang sửa / chờ bản demo dựng lại / BA đang sửa tài liệu) ở tooltip của ô Status.
    - **Ghi chú gửi về Requirement có điểm kết thúc**: vòng DỰNG MỚI bản demo (task `PocPreview` không có `RevisionFeedback`) chạy xong thì `PocDeferredFeedbackService.MarkRoutedAppliedAsync` chuyển các ghi chú POC `RoutedToRequirement` tạo trước lúc vòng đó bắt đầu sang `Addressed` (+ `RevisionTaskId` = task dựng). Không có bước này chúng kẹt Waiting mãi dù bản demo mới đã mang thay đổi — trước đây danh sách giấu chúng đi chính vì thế.
    - **Action**: nút **Delete** (chỉ icon thùng rác, nhãn ở tooltip) chỉ ở dòng chưa tới tay agent nào — Draft, hoặc Waiting mà còn **hoãn** (`Deferred`) chờ bản demo dựng lại. `PocNoteDisplayStatus.IsWithdrawable` là định nghĩa DUY NHẤT, dùng cho cả `CanWithdraw` của bảng lẫn `WithdrawPocCommentUseCase`. Ví dụ: một lượt gửi 1 ghi chú hiểu sai yêu cầu + 2 ghi chú chỉnh trình bày ⇒ cả ba Waiting; hai ghi chú trình bày (hoãn) xoá được cho tới khi bản demo dựng lại, ghi chú về BA thì không (BA đã nhận nó — xoá dòng không rút được nó khỏi hội thoại). Ghi chú `Sent` cũng không xoá được: Dev đang sửa theo nó. Dòng **Apply** của ghi chú POC có nút **"✗ Vẫn chưa đạt"** (`ReopenPocCommentUseCase`) — đường duy nhất nói một ghi chú đã áp mà chưa đúng ý, đưa nó về Draft. Trang khách chỉ có nút Delete cho bản nháp chưa gửi — xem [bản nháp của khách](#bản-nháp-của-khách-và-send-note).
    - **Cột `#` là số pin**: đánh theo thứ tự tạo trên TOÀN bảng, không đánh lại theo bộ lọc. Pin chỉ vẽ cho ghi chú POC neo vào một phần tử (`ElementPath` khác rỗng); ghi chú gõ qua Add Note và ghi chú Brief mang số viền rỗng (`.no-pin`) — vẽ chúng ở toạ độ 0,0 là chồng một cụm pin lên góc header bản demo. Bấm dòng có pin ⇒ nháy pin; bấm pin ⇒ nháy dòng (bỏ lọc trước nếu dòng đang bị lọc giấu).
    - **Popup Add Note** (Screen + Note): dropdown Screen là các `data-view` của CHÍNH file `poc-demo.html` (`GetPocReviewQuery.Screens`, qua `PocContentScreens.ExtractFromDocument` — bỏ HTML comment, vì template có một dòng mẫu `data-view="LABEL"` trong comment đầu file) cộng option **Chung** (`PageView` rỗng), mặc định là màn hình đang mở trong iframe (annotator báo qua `poc-ready`/`poc-view`). Lấy từ Spec/UAT thì tên có thể lệch nhãn `data-view` mà annotator dùng để mở màn hình. Ghi chú qua popup không có neo phần tử. Ghim một phần tử trên demo mở **cùng popup**, khi đó dropdown Screen nhường chỗ cho dòng "Ghi chú cho: màn hình · phần tử". Trang đã nghiệm thu ⇒ Add Note `disabled`.
    - Version luôn hiện ở cột riêng (không còn nhãn chỉ gắn cho thế hệ trước), và **bảng không lọc theo version mặc định**: lọc đi là đúng thứ người dùng phàn nàn — approve xong là "mất hết" — và ghi chú Draft của bản trước sẽ không còn đường nào gửi đi.
    - **Nút gửi đứng cuối thẻ, mang số ghi chú sẽ đi** ("Gửi 3 ghi chú…", đếm ghi chú Draft — triage/dispatch quét đúng tập `Open`). Kết quả lượt gửi / lỗi phân loại in ở `#pocSendFeedbackHint`, **ngoài** khối nút: gửi xong là hết ghi chú Draft nên khối nút bị ẩn đúng lúc cần đọc thông báo.
- **Một đường đóng vòng cho người dùng nghiệp vụ** ngay tại trang POC Review (cần `RequirementsManage`). Nút "Send note" (số ghi chú Draft sẽ đi nằm ở tooltip) chỉ hiện khi danh sách CÓ ghi chú `Open` — cả hai bước dưới đây chỉ quét đúng trạng thái đó, nên với danh sách rỗng nút ấy là lời mời bấm vào chỗ không có gì để bấm. Khối được render sẵn `hidden` rồi `renderList()` mở ra sau mỗi lượt nạp/ghim/thu hồi, chứ không quyết ở Razor: danh sách nạp bằng `fetch` sau khi trang render, và số ghi chú đổi ngay trong buổi review mà không tải lại trang. Hai bước:
  - `POST /Projects/TriagePocFeedback` — `TriagePocFeedbackUseCase` phân loại TỪNG ghi chú `Open` (`poc-feedback-triage.v1.md`) vào một trong BA nhóm: "lỗi trình bày của bản demo", "**sửa thiết kế màn hình**" (nghiệp vụ đúng, cách tổ chức chưa hợp lý: gộp/tách màn, chuyển nút sang màn khác, đổi dạng màn, menu khó tìm) hay "tài liệu yêu cầu hiểu sai", trả về bảng đề xuất cho hộp xác nhận (ba nhóm, mỗi dòng có nút chuyển sang hai nhóm kia). Chỉ đọc + gọi model, **không đổi trạng thái gì**; người dùng đổi nhóm được từng dòng. Model hỏng / chưa cấu hình BA ⇒ mọi ghi chú rơi về nhóm RẺ kèm cờ `classified = false` (fail-safe: không tự đề xuất đường đắt bằng một kết quả phân loại không có).
  - `POST /Projects/DispatchPocFeedback` — `DispatchPocFeedbackUseCase` gửi theo đúng bảng người dùng vừa xác nhận: nhóm "chỉnh demo" đi `RequestStageRevisionUseCase` (`onlyStage: PocPreview`, đếm chung trần `MaxRevisionRounds`; rào `onlyStage` để quyền "sửa demo" không nới thành quyền điều khiển các bước kỹ thuật phía sau), nhóm "hiểu sai yêu cầu" đi `RoutePocFeedbackToRequirementUseCase` (soạn một lượt user gửi BA bằng `poc-feedback-compose.v1.md` rồi chạy lại workflow soạn draft), nhóm "sửa thiết kế" đi `RoutePocFeedbackToDesignUseCase` (ghi góp ý vào `UxBlueprint.DesignNotes`, chạy lượt thiết kế chế độ `feedback` — `UxDesignService.RedesignFromFeedbackAsync` — rồi soạn lại draft; ghi chú mang `Route = Design` và cùng vòng đời với đường tài liệu). **Mỗi đường chỉ nhận ĐÚNG tập con của nó.** Thiết kế và tài liệu cùng kéo theo soạn lại Brief + dựng lại POC nên chạy chung một lượt: thiết kế chạy TRƯỚC (bản thiết kế mới phải có mặt khi Brief soạn lại), Brief soạn đúng một lần; đường thiết kế hỏng ⇒ dừng hẳn, không ghi chú nào đổi trạng thái. Đường tài liệu hỏng SAU khi thiết kế đã áp ⇒ vẫn soạn lại cho bản thiết kế mới, ghi chú tài liệu còn `Open` để gửi lại.
  - **Precedence** khi nhóm chỉnh demo đi cùng nhóm tài liệu hoặc nhóm thiết kế: chạy đường tài liệu/thiết kế, và **hoãn** các ghi chú nhóm chỉnh demo (`Deferred`) — POC sắp dựng lại từ tài liệu đã sửa nên vá HTML lúc đó vừa phí một lượt trong trần, vừa cho ra bản vá bị bỏ đi ngay. Ghi chú hoãn tự đi vào vòng dựng mới bản demo sau khi requirement được duyệt lại, xem [Ghi chú chỉnh trình bày bị hoãn tới bản demo dựng lại](#ghi-chú-chỉnh-trình-bày-bị-hoãn-tới-bản-demo-dựng-lại). Hoãn chỉ xảy ra SAU khi đường tài liệu chạy được: đường đó hỏng thì mọi ghi chú vẫn `Open`. Hộp xác nhận nói rõ điều này trước khi gửi.
  - Vì sao gộp từ hai nút: hai nút cũ (`RequestPocFix` / `RoutePocFeedbackToRequirement`) bắt người xem demo tự phân loại ghi chú của mình, và **cả hai đều nuốt trọn mọi ghi chú `Open`** — một buổi review lẫn hai loại thì không nút nào đúng. Nặng nhất là đường Requirement cũ: nó tự lọc bằng LLM nhưng vẫn đánh dấu TẤT CẢ ghi chú `Open` thành `RoutedToRequirement`, kể cả các ghi chú thẩm mỹ đã bị loại khỏi tin nhắn gửi BA — chúng biến mất khỏi đường vá POC và `ReopenPocCommentUseCase` cũng không mở lại được (chỉ nhận `Addressed`/`Sent`), tức là mất trắng.
- **Nghiệm thu bản demo** (`POST /Projects/AcceptPoc`, quyền `RequirementsManage`): điểm DỪNG của hành trình phía người yêu cầu. Trước đây trang chỉ có các đường "còn sai chỗ này" (ghim ghi chú / nhờ Dev chỉnh / gửi về Requirement) mà không có đường nào nói "được rồi": cổng duyệt thật nằm ở Agent Dashboard sau quyền `DeliveryAdvance`, nên đội delivery phải đi hỏi miệng và người yêu cầu không có cách nào tự nói "xong". `AcceptPocUseCase` ghi `Project.PocAcceptedAtUtc/PocAcceptedBy` và báo cho người có quyền duyệt (`NotificationType.PocAccepted`) — **KHÔNG tự đẩy pipeline**: đi tiếp vẫn là quyết định ở cổng POC, để một cú bấm của người dùng nghiệp vụ không âm thầm khởi động các bước đắt tiền.
- Khi task là revision, worker **bỏ qua re-seed** POC để không ghi đè sản phẩm cũ về placeholder. Lượt **chạy tiếp** sau khi task hỏng giữa chừng (Thử lại / app restart) cũng bỏ qua re-seed và bỏ qua sinh lại UAT — xem [delivery-pipeline.md](delivery-pipeline.md#thử-lại-là-chạy-tiếp-checkpoint-của-lượt-agent).

### Dựng POC bằng agent nhiều bước: gom việc vào ít lượt

Mọi vòng của bước PocPreview — dựng mới, "Request revision", chạy tiếp, và mỗi lượt
[thử model khác](#thử-model-khác-cho-developer-biến-thể-poc) — đều đi agent loop. Đừng tìm `PocOneShotBuilder`
(đường "một lượt sinh cả bản demo + vài lượt sửa", Purpose `PocBuild`/`PocRepair`): đã gỡ, vì một lời gọi
phải stream trọn vùng nội dung + vùng script nên chạy quá trần ~300s của gateway Model Farm, bị cắt và mất
trắng trước khi rơi về agent. Agent loop mỗi lượt model gửi
lại toàn bộ lịch sử, nên **số lượt** quyết định chi phí. Nhưng lượt cũng không được quá to: gateway Model
Farm cắt một lời gọi chưa trả header sau ~300s (ca thật: `claude-sonnet-5-5` gom 3–5 màn vào lượt đầu, bị
reset ở giây ~305), và model ra ~135 token/giây — nên mỗi lượt giữ dưới ~12k token output. Trình tự trong
`poc-preview.v1.md` nhắm ~8–10 lượt cho spec ~10 màn hình, thay vì một lượt cho mỗi việc nhỏ:

| Lượt (spec ~10 màn) | Việc |
|---|---|
| 1 | `SetPocContent` — shell + nhóm 2–3 màn đầu |
| 2–5 | `AppendPocContent` — mỗi lượt MỘT call gom 2–3 màn hoặc toàn bộ modal (không phải một call mỗi section) |
| 6(–7) | `SetPocScript(script, audit: true)` — ghi logic **và** tự kiểm trong cùng một kết quả tool; script dài thì lõi + `AppendPocScript(audit: true)` ở lượt sau |
| 8–9 | `FixPocAndAudit(edits[], appendContent, appendScript)` — MỘT call cho cả vòng sửa, tự audit lại |
| cuối | trả lời bàn giao |

- **`audit: true`** trên `SetPocScript`/`AppendPocScript` (mặc định `false`): ghi xong thì chạy luôn
  `AuditPocContent` và nối báo cáo vào kết quả — bỏ hẳn lượt chỉ để gọi audit.
- **`FixPocAndAudit`** thay chuỗi `ReplaceInFile` lẻ + `AuditPocContent`: các cặp `{find, replace}` áp bằng
  `PocTextEdits.Apply` (khớp đúng một chỗ trong vùng nội dung/vùng script — luật của `ReplaceInFile` —
  không tìm trong shell; chỗ không áp được được báo lại, các chỗ khác vẫn áp), rồi nối `appendContent`/
  `appendScript`, ghi file một lần và tự audit. Không áp được gì ⇒ không ghi, **không audit** (đỡ một lần chạy
  trình duyệt headless cho bản demo y nguyên) và trả danh sách lỗi để model sửa lại.
- Model vẫn được gọi nhiều tool trong một lượt trả lời (chạy tuần tự đúng thứ tự viết) — prompt nói rõ để
  model tận dụng. `AuditPocContent` gọi riêng vẫn còn, nhưng mô tả tool khuyên không dùng riêng.
- **Vòng dựng mới chỉ thấy bộ tool POC** (`WorkspaceTools.PocBuildToolNames`: `SetPocContent`,
  `AppendPocContent`, `SetPocScript`, `AppendPocScript`, `FixPocAndAudit`, `AuditPocContent`) qua
  `AgentRunOverrides.AllowedTools` — cả ở `AgentTaskWorker` lẫn `PocVariantRunner`. Ca thật
  (`claude-sonnet-5-5`, 13 lượt): 5 lượt đi săn MỘT lỗi cú pháp JS — `SearchInFiles` tìm `";;"`, `ReadFile` cả
  `poc-demo.html` (prompt +53k token cho mọi lượt sau), tự viết `chk.js` rồi `RunCommand node`. Model tự viết
  toàn bộ nội dung nên không cần đọc lại. Vòng "Request revision" giữ đủ tool: model chưa thấy bản demo đang có.
- **Lỗi JS có vị trí**: tầng runtime cài một listener `error` trước mọi script của trang (lấy `lineno`/`colno`,
  thứ `PageError` của Playwright không đưa — lỗi cú pháp thì không có cả stack) và `PocScriptErrorLocator` đổi
  dòng tài liệu thành dòng của vùng `POC_SCRIPT` kèm dòng code và dòng trước. Mục này đứng ĐẦU danh sách
  runtime issue (một lỗi cú pháp làm chết cả script); lỗi nằm trong script shell thì không có mục vị trí.
- Trần `DeliveryPipeline.PocStepBudget` giữ nguyên: nó là trần cho model không theo hướng dẫn gom việc, không
  phải mức tiêu.

## Khung Bosch & tải source

- `Project.IsUseBoschTemplate = true` (mặc định) ⇒ `BoschTemplateSeeder` clone repo khung chuẩn (backend .NET + Angular) từ `BoschTemplate:BackendRepoUrl/FrontendRepoUrl` rồi **chép nội dung** (bỏ `.git`) vào repo đích làm skeleton (idempotent — thư mục đã có file ngoài `.git` thì bỏ qua; URL trống cũng bỏ qua). Pipeline dùng prompt bản `-bosch`.
- **Tải code sinh ra**: `GET /Projects/DownloadSource?projectId=` — `ImplementationSourcePackager` nén `04_Implementation/src/` thành zip.

### Repo đích của dự án

Thư mục gốc workspace **không phải** git repo. Các repo nằm dưới `04_Implementation/src`, và
`Services/Artifacts/ProjectRepositoryLayout.cs` là **nơi duy nhất** khai báo dự án có mấy repo, ở đâu,
đẩy lên remote nào:

| Generation Mode | Repo đích | Remote |
|---|---|---|
| Khung Bosch | `04_Implementation/src/backend` + `04_Implementation/src/frontend` | `Project.BackendGitUrl` / `FrontendGitUrl` |
| Không dùng khung Bosch | `04_Implementation/src` (agent sinh cả cây code vào đây) | `Project.BackendGitUrl` |

`ProjectRepositorySeeder` chạy **trước bước Implementation** và **một lần nữa ở bước Pull Request** để mỗi
repo đích: là git repo thật, có remote trỏ về repo CỦA DỰ ÁN, và có `user.name`/`user.email` để
`git commit` không fail vì thiếu danh tính (chỉ đặt — `ICOGenerator` / `icogenerator@localhost` — khi máy
chủ chưa cấu hình sẵn `git config --global user.name/user.email`). Clone dùng tài khoản Git của dự án nếu có
(xem [delivery-pipeline.md](delivery-pipeline.md#tài-khoản-git-và-nhánh-đích-của-dự-án)); `git init` đặt nhánh
đầu tiên trùng nhánh đích của dự án. Ba nhánh của nó:

- Thư mục **trống** + đã có Git URL ⇒ `git clone` repo dự án. Nhánh feature mọc từ lịch sử của chính repo
  đích nên PR là một diff đọc được, không phải hai lịch sử không liên quan. Clone fail ⇒ **ném lỗi**, task
  dừng ngay ở Implementation thay vì để agent sinh 40 file rồi mới phát hiện không có chỗ nào để bàn giao.
- Thư mục **có sẵn file** nhưng chưa phải repo (workspace tạo trước cơ chế này) ⇒ `git init` **tại chỗ**,
  giữ nguyên code.
- **Chưa có Git URL** ⇒ vẫn `git init` để commit được; cổng duyệt chỉ đòi URL ngay trước bước Pull Request
  nên bước Implementation thường chạy trước lúc URL được điền.

Chạy lại là chuyện thường (revision, retry, lần thứ hai ở bước PR) nên seeder **idempotent**: repo đã có
thì chỉ cập nhật remote.

Hai hệ quả cần nhớ khi đụng vào vùng này:

- **Skeleton Bosch là NỘI DUNG, không phải repo.** Trước đây template được clone thẳng vào chỗ code, nên
  `origin` của thư mục làm việc là repo khung chuẩn — bước Pull Request đẩy nhánh feature của khách hàng
  lên chính repo template. Nay template được clone ra thư mục tạm rồi chép sang (bỏ `.git`).
- **Mọi tool git nhận `repoPath`** (xem [agents-and-tools.md](agents-and-tools.md#danh-mục-tool-hiện-có)):
  không còn mặc định "gốc workspace", vì mặc định đó chính là lỗi cũ.

---

### Tạo & sửa thông tin dự án (và bất biến "tên dự án = tên thư mục workspace")
Modal **New Project** nhận đúng ba field: **Name** (bắt buộc), Description và **Org Unit** — đơn vị yêu
cầu **bắt buộc** từ lúc tạo, vì ghi chú đơn vị nạp cho BA, tên phòng ban thật trong tài liệu và bảng
"Usage by department" đều đọc từ `Project.OrgUnitCode` (xem
[requirement-flow.md](requirement-flow.md#bối-cảnh-tổ-chức-bosch-orgunitsassociates--prompt-ba--tài-liệu--usage)). `CreateProjectUseCase` chỉ nhận
mã có thật trong `OrgUnits`: thiếu hoặc mã lạ/đã xóa mềm ⇒ `OrgUnitRequired`, **không** tạo project (form
đánh dấu `required` chỉ là lớp tiện dụng). Bảng `OrgUnits` chưa đồng bộ được đơn vị nào ⇒ modal nói thẳng
lý do và khóa nút tạo thay vì đưa ra một dropdown rỗng.

Trang Projects sửa được Name / Description / đơn vị yêu cầu ngay tại danh sách (modal, quyền
`ProjectsEdit` + `IProjectAccessGuard` nên User thường chỉ sửa project của mình). Các field kỹ thuật
(Generation Mode, Backend/Frontend Git, nhánh đích, tài khoản Git) **không** ở đây — chúng thuộc `UpdateDeliveryConfigUseCase` ở
Agent Dashboard; mỗi màn hình sửa đúng phần của mình.

Điểm cần biết trước khi đụng vào luồng này: **tên thư mục workspace dẫn xuất từ TÊN dự án**
(`WorkspacePathResolver.GetWorkspaceFolder(id, name)` — mọi đường dẫn tài liệu/POC tính lại từ đó mỗi
lần cần). Vì vậy đổi tên mà không đổi thư mục = mọi đường dẫn trỏ sang thư mục trống, tài liệu/POC đã
sinh coi như mất. `UpdateProjectUseCase` giữ hai bên khớp nhau bằng ba chốt:

- Đổi thư mục **TRƯỚC** khi lưu DB (`IArtifactStorage.TryRenameProjectWorkspace`); thất bại ⇒ trả
  `WorkspaceRenameFailed` và **không lưu gì** (giữ tên cũ, dữ liệu còn nguyên chỗ). Lưu DB lỗi sau đó ⇒
  đổi thư mục về tên cũ rồi mới ném lỗi.
- Đang có workflow chạy (run mới nhất chưa Completed/Failed/Canceled) ⇒ **chặn đổi TÊN**
  (`RenameBlockedByRunningWorkflow`): agent nền giải đường dẫn workspace một lần lúc bắt đầu task rồi
  ghi file suốt run. Description/đơn vị yêu cầu vẫn sửa được bình thường (UI cũng khóa sẵn ô Name).
- "Chưa có gì trên đĩa" / hai key trùng nhau / `RootPath` cấu hình sai ⇒ coi như **không có gì phải
  đổi** (true), không chặn việc sửa — cùng tinh thần best-effort với lúc tạo project.

### Nhân bản dự án (thử nhiều tình huống trên cùng một điểm xuất phát)
`POST /Projects/Clone` (`CloneProjectUseCase`, quyền `ProjectsCreate` + `IProjectAccessGuard`) tạo một dự
án mới từ một dự án đã có, để thử nhánh khác mà không phải phỏng vấn lại buổi BA đã tốn tiền model. Hai
phạm vi, chọn ngay ở modal:

- **Bản sao đầy đủ** — hội thoại, tài liệu (kèm `ProjectDocumentRevisions`), file nguồn, workflow và ghi
  chú POC, cộng cả cây workspace. Rẽ nhánh từ đúng chặng dự án gốc đang đứng.
- **Chỉ phần yêu cầu** — trí nhớ hội thoại BA, các lượt chat, file nguồn và các bảng của buổi phỏng vấn (bảng luồng người dùng chốt, ba bảng BA soạn ngầm); workspace chỉ
  chép `00_Source` rồi dựng lại bộ khung 5 giai đoạn. Bản sao chạy lại delivery từ đầu.

Bốn thứ **không bao giờ** đi theo bản sao, mỗi thứ vì một hậu quả cụ thể:

| Không chép | Vì |
|---|---|
| `AgentModelCallLogs` | nguồn số liệu của Usage + Delivery Quality — chép sang là nhân đôi chi phí đã tiêu trong báo cáo của cả tổ chức |
| `PocShareLinks` | `Token` là link công khai đang sống (unique index); nhân bản nó là mở thêm một cửa vào bản demo mà người tạo link không biết |
| Task ở `Queued`/`Running`/`Retrying` | `AgentTaskWorker` poll `Status == Queued` **toàn cục**, không theo project ⇒ task chép sang bị nhặt ngay và bắn lời gọi LLM thật. Run của chúng chép sang ở trạng thái `Canceled`; riêng `WaitingForHuman` **giữ nguyên** vì đó chính là cổng duyệt người ta nhân bản để thử. Lưu ý `AgentTaskStatus` **không có** giá trị `Canceled` — hủy là việc của `WorkflowRunStatus` |
| `PocAcceptedAtUtc`/`PocAcceptedBy` | chữ ký nghiệm thu của một người thật cho một bản demo cụ thể |

Ngược lại, `ChecklistGapHarvested` được đặt **true** và `PocFeedbackHarvestedCount` đặt bằng số ghi chú
POC thực sự chép sang: cả hai đều là con trỏ của các đường ghi vào `AgentChecklistItem` **dùng chung cho
mọi dự án**, nên để chúng ở 0/false sẽ khiến cùng một buổi phỏng vấn đẻ ra hai lần cùng một bài học. Cùng
lý do, mọi con trỏ harvest khác (`SummarizedTurnCount`, `UserMemoryHarvestedTurnCount`,
`CoverageHarvestedTurnCount`…) được chép **nguyên giá trị**, không reset về 0. Còn hai HÀNG ĐỢI học đang mở
(`PendingChecklistHarvestVersion`, `PendingPocFeedbackHarvest`) thì **không chép**:
bản sao chưa đi qua cổng duyệt nào, mà bằng chứng của những cổng đó thuộc về dự án gốc và sẽ được chính nó
chắt lọc — xem [Vòng học chạy ở cổng duyệt](requirement-flow.md#vòng-học-chạy-ở-cổng-duyệt).

Ba bất biến kỹ thuật:

- **Chép đĩa TRƯỚC, lưu DB SAU** (`IArtifactStorage.TryCopyProjectWorkspace`) — cùng kỷ luật với đổi tên ở
  trên. Chép hỏng ⇒ `WorkspaceCopyFailed`, không lưu gì (một project trỏ vào thư mục trống không tự lành
  được). Lưu DB lỗi sau đó ⇒ `TryDeleteProjectWorkspace` dọn thư mục vừa chép rồi mới ném.
- **Viết lại đường dẫn tuyệt đối đã lưu**: `ProjectSourceFile.StoredPath` và `ProjectDocument.FilePath` mang
  key thư mục của dự án gốc; không đổi thì bản sao đọc — và xóa — file thật của dự án gốc. Tên thư mục con
  `{id:N}` dưới `00_Source` giữ nguyên id CŨ: không chỗ nào suy ngược thư mục từ `Id`, mọi nơi đều lấy
  `Path.GetDirectoryName(StoredPath)`.
- **Remap id trong `AgentConversation.Attachments`** (JSON `ChatAttachment[]` trỏ về `ProjectSourceFile`) và
  đọc hội thoại bằng `IgnoreQueryFilters()` — bảng đó có global filter `ArchivedAt == null`, bỏ lượt đã
  archive sẽ làm lệch mọi con trỏ đếm-theo-`CreatedAt` vừa chép sang.

Các thư mục sinh lại được (`WorkspaceFileFilter.RegenerableDirectories`: `node_modules`, `bin`, `obj`,
`.git`, `.vs`) không đi theo bản sao. Hệ quả cần biết: skeleton Bosch trong bản sao có đủ file nhưng không
còn `.git`, và `BoschTemplateSeeder` bỏ qua thư mục đích đã có file nên sẽ **không** clone lại — muốn git
sạch thì xóa `04_Implementation/src` trong bản sao.

### Vòng phản hồi POC hai chiều + link chia sẻ cho người ngoài hệ thống
- `PocComment` có thêm trạng thái `Addressed` (+ thời điểm + bàn giao của agent): vòng chỉnh sửa POC
  chạy xong thì các ghi chú đã gửi chuyển sang "đã sửa — mời kiểm lại", và người review mở lại được
  đúng cái **chưa đạt** (`ReopenPocCommentUseCase`) thay vì ghim ghi chú mới trùng nội dung.
- `PocVerification.DetectFixes` là chiều ngược của `DetectRegressions`: "đã sửa được gì so với vòng
  trước" — thứ người review vòng thứ hai luôn hỏi đầu tiên.
- `PocShareLink` + `PocShareController` (`[AllowAnonymous]`, route `poc-share/{token}`): người không
  có tài khoản mở được bản demo và ghim góp ý — đứng tên người nhận link. Token luôn có hạn dùng, thu hồi được, và
  chỉ mở đúng ba thứ của MỘT project (trang xem, `poc-demo.html`, góp ý của CHÍNH link đó). Toàn bộ bề mặt
  cho khách gom trong một controller để đọc một file là thấy hết; bản demo phục vụ qua CÙNG
  `PocDemoResponse` với đường có đăng nhập nên không thể có hai mức rào chắn khác nhau (xem
  [screens-and-permissions.md](screens-and-permissions.md)).
- Trang khách (`PocShare/Open`) dùng **cùng bố cục hai cột** với POC Review: khung demo rồi ngay dưới
  nó **cùng bảng ghi chú** (`_PocNotesTable` + `poc-notes.js` ở chế độ khách: chỉ góp ý của chính link,
  cột Action chỉ có Delete cho bản nháp, không "Vẫn chưa đạt", không ghi chú Brief, popup Add Note không hỏi tên, cũng không nhắc lại tên — ghi chú tự đứng tên người nhận link; trang khách nạp riêng `dropdown.js` vì
  `_GuestLayout` không có), cột phải chỉ có hướng dẫn sử dụng — không có kịch bản
  nào thì bỏ hẳn cột phải (`.poc-review-layout.no-side`). `_GuestLayout` chạy **full width** (không
  `max-width`): trang gần như chỉ có bản demo, mà bản demo là một ứng dụng có sidebar + bảng nhiều cột,
  kẹp giữa màn hình rộng chỉ để lại hai dải trống trong khi chính bản demo bị bóp.
- Hướng dẫn sử dụng trên trang khách là **cùng partial** `_PocGuide` với POC Review nên cũng **chỉ chỗ**
  như vậy: `poc-share.js` gửi cùng lệnh `poc-tour-step` và hiện cùng ba câu `.uat-step-hint` khi trượt.
  Tên message giữa trang cha và `poc-annotator.js` (`poc-ready`,
  `poc-exit-mode`, `poc-tour-result`…) phải khớp đúng từng chữ ở cả hai trang: lệch một chữ thì tính
  năng im lặng biến mất, không có lỗi nào.
- Ô "Gửi cho ai" của hộp thoại tạo link là autocomplete lấy gợi ý từ bảng `Associates`
  (`SearchAssociatesQuery` + `Projects/SearchAssociates`) — nhãn link chỉ có ích khi cùng một người
  luôn được ghi cùng một cách. Vẫn cho gõ tự do vì khách ngoài công ty không có trong danh bạ. Danh bạ
  dùng chung cả công ty nhưng cửa vào kẹp theo project + `RequirementsManage`, và chỉ trả tên/email/
  đơn vị/chức danh — không mở thêm một đường tra cứu hồ sơ nhân sự.
- Ô "Gửi cho ai" (**Send to**, lưu ở `PocShareLink.Label`) là **bắt buộc**: mỗi link là của MỘT người,
  nên mọi góp ý gửi qua link đó đứng tên người ấy (`Khách · {Send to}`) — `ResolvePocShareTokenQuery`
  trả `PocShareAccess(ProjectId, RecipientName)` và `PocShareController.AddComment` lấy tên từ link, không
  tin ô khách tự gõ. Hệ quả: muốn biết góp ý của ai thì mỗi người nhận một link riêng, đừng gửi chung một
  link cho cả nhóm. Link cũ tạo khi chưa bắt buộc (không có `Label`) vẫn hiện ô "Tên của bạn" như trước.

#### Bản nháp của khách và Send Note

Góp ý khách ghi qua link **chưa tới tay người tạo dự án** cho tới khi khách bấm **Send Note**, và mỗi khách
**chỉ thấy góp ý của mình**; người tạo dự án (POC Review) thấy mọi góp ý ĐÃ GỬI của mọi link.

- **Danh tính của khách là link, không phải tên**: `PocComment.PocShareLinkId` đóng dấu link đã ghi, và
  `PocShareAccess` mang `LinkId`. Hai link có thể cùng tên người nhận nên lọc theo `Khách · {tên}` là lộ
  góp ý của người khác. Ghi chú khách tạo trước khi có cột này (không mang link) vẫn nằm ở POC Review
  nhưng không hiện trên trang khách nào.
- **Bản nháp là một giá trị enum** — `PocCommentStatus.GuestDraft` — không phải cờ riêng: mọi đường gửi cho
  agent (triage, dispatch, `RequestStageRevisionUseCase`) lọc `Status == Open`, nên bản nháp tự đứng ngoài
  chúng; quên lọc ở một chỗ mới cũng không làm câu khách gõ dở lọt sang Dev/BA. Các chỗ đọc KHÔNG lọc
  trạng thái thì loại `GuestDraft` tường minh: `ListPocCommentsQuery` (bảng POC Review), bằng chứng của
  `PocFeedbackMemoryService` (con trỏ harvest vẫn đếm qua nó), đếm ghi chú ở `GetDeliveryQualityQuery`,
  và `CloneProjectUseCase` không chép nó (link của nó không được chép sang bản sao).
- **Send Note** (`SubmitGuestPocCommentsUseCase`, `POST poc-share/{token}/comments/send`) chuyển MỌI bản
  nháp của link đó `GuestDraft → Open` một lượt. Từ đó chúng là Draft bình thường ở POC Review và người tạo
  dự án quyết có gửi cho đội xử lý hay không — khách không gửi thẳng cho agent. `CreatedAt` giữ nguyên lúc
  gõ: số `#` và con trỏ harvest đều xếp theo nó. Nút đứng cuối bảng, mang số bản nháp ("Send Note (3)"),
  ẩn khi không còn bản nháp — cùng kiểu nút gửi của POC Review.
- **Delete** (`DeleteGuestPocCommentUseCase`, `POST poc-share/{token}/comments/{id}/delete`) chỉ nhận bản
  nháp của chính link đó và luôn **xoá thật** (chưa ai ngoài người gõ thấy nó), lùi con trỏ harvest như
  `WithdrawPocCommentUseCase`. Ghi chú của link khác trả NotFound giống ghi chú không tồn tại. Đã gửi thì
  hết xoá — muốn rút thì báo đội dự án.
- **Trạng thái phía khách** (`PocNoteDisplayStatus.ForGuest`): `GuestDraft` → Draft, `Addressed` → Apply,
  còn lại → Waiting. Khác phép chiếu của POC Review ở đúng một chỗ: `Open` là Waiting với khách (đã gửi,
  nằm ở tay đội dự án) dù vẫn là Draft với người tạo dự án.
- Nghiệm thu khoá cả hai thao tác (`PocAcceptanceGate`) như mọi đường ghi chú khác.

### Nghiệm thu là công tắc HAI CHIỀU, và nó KHOÁ nội dung

Nút **Approve POC** đứng ở **nút chính của command bar** trang POC Review (cùng thanh với "Share"),
không còn nằm lọt trong panel ghi chú ở cột trái. Hai nút không bao giờ cùng xuất hiện: đã nghiệm thu ⇒ chỉ có **Withdraw Approve**; chưa ⇒ chỉ có **Approve POC**. Cả hai đòi
`RequirementsManage` — khoá này thuộc phía người yêu cầu, không phải một cổng của delivery.

Bấm Approve cũng đưa dự án sang chặng **POC Approve** — chặng cuối của
[`ProjectStatus`](overview.md#chặng-của-dự-án-projectstatus). Trang POC Review **không** in badge chặng ở
đầu trang (chỉ danh sách Projects in): trạng thái nghiệm thu đã được nói kỹ hơn ngay tại chỗ người dùng
vấp phải nó — khối "✓ Đã nghiệm thu" trong thẻ ghi chú, kèm ai chốt, lúc nào và điều gì đang khoá. Badge
chỉ nói được tên chặng, nên hai chỗ cùng nói một trạng thái thì chỗ mờ hơn là nhiễu.

Bấm Approve là **chốt nội dung lại**: từ lúc đó cả hai phía đều ngừng nhận thay đổi.

| Bị khoá | Endpoint |
|---|---|
| Chat BA, mở chat mới, đính kèm tài liệu cho BA | `ChatStream`, `NewChat`, `UploadSource` |
| Ghi chú trên bản xem trước Product Brief (ghi vào cùng transcript) | `ReviseBrief` |
| Ghim / thu hồi / mở lại ghi chú POC, kể cả qua link chia sẻ | `AddPocComment`, `WithdrawPocComment`, `ReopenPocComment`, `poc-share/{token}/comments` (+ `/send`, `/{id}/delete` của khách) |
| Gửi ghi chú đi xử lý (khởi động vòng sửa POC hoặc vòng sửa tài liệu) | `DispatchPocFeedback` |

Chốt nằm ở **tầng use case** (`PocAcceptanceGate`), không ở controller và càng không ở JavaScript: đường
ghim ghi chú có cửa thứ hai là link chia sẻ cho khách ẩn danh — khách không thấy nút khoá nào, nên khoá
chỉ ở giao diện là khoá giả. Giao diện (ô nhập `disabled`, biển báo trên khung chat, ẩn nút "Báo lỗi kịch
bản", `data-annotatable="false"` cho bản draft) chỉ để người dùng biết **vì sao** không gõ được thay vì gõ
xong mới nhận lỗi.

**Withdraw Approve** (`WithdrawPocAcceptanceUseCase`) xoá dấu nghiệm thu và là cửa mở khoá **duy nhất**.
Nó phải tồn tại: từ khi một cú bấm đóng băng cả hai màn hình, một chiều đi không có chiều về nghĩa là
người phát hiện thêm điểm sai sau khi đã nói "được rồi" không còn đường nào để nói. Chiều đi báo cho đội
delivery (`PocAccepted`) thì chiều về cũng báo (`PocAcceptanceWithdrawn`) — họ có thể đang đứng ở cổng POC
và sắp bấm duyệt dựa trên lời vừa bị rút. Cả hai chiều **không đụng pipeline**: các bước đã chạy thì không
lùi lại được bằng một cú bấm ở đây.

Cả hai chiều hỏi lại bằng **hộp thoại của app** (`#pocConfirmModal`, `.modal` như các hộp khác của trang),
không phải `confirm()` của trình duyệt: hộp mặc định dán tên miền lên đầu, bỏ hết xuống dòng của câu giải
thích và chỉ bày được "OK/Cancel", nên người dùng đọc xong vẫn không thấy mình đang OK cái gì. Hộp của app
bày đủ câu hỏi (tiêu đề), hậu quả (`.modal-note warning`) và nút chính mang đúng nhãn thao tác. Một hộp
dùng chung cho cả hai chiều — hai nút không bao giờ cùng có mặt — và `askConfirm` trả lời bằng
`Promise<boolean>` để chỗ gọi giữ nguyên hình dạng "hỏi rồi mới làm". Lỗi từ server hiện **trong chính hộp
đó** thay vì `alert()`.

Chữ trong hộp (kể cả `message` mà `AcceptPoc`/`WithdrawPocAcceptance` trả về) để **tiếng Anh** — ngoại lệ
có chủ đích của quy ước tiếng Việt — cho khớp nhãn nút trên command bar (`Approve POC` / `Withdraw
Approve`): bắt người dùng tự dịch nhãn ngay tại khoảnh khắc cần chắc tay là một bước thừa.

Sau mỗi lần bấm, JS **tải lại trang** thay vì tự đảo nhãn nút: trạng thái khoá chạm tới gần như mọi thứ
trên trang (nút gửi ghi chú, chế độ ghim, nút thu hồi từng dòng, biển trạng thái), nên vá từng chỗ bằng JS
là dựng một bản sao thứ hai của luật khoá — bản sao sẽ trôi lệch khỏi server ngay lần sửa sau.

### Góp ý giao diện sống sót qua một vòng dựng lại POC (`poc-ui-conventions.json`)
Hai đường của popup "Gửi ghi chú đi xử lý" ghi vào hai chỗ khác hẳn nhau, và đó là chỗ từng rò: đường
**"Nhờ đội Dev chỉnh bản demo"** chỉ vá `poc-demo.html`, không đụng Brief/Spec; còn đường **"Gửi về
Requirement"** dẫn tới duyệt lại tài liệu, và mỗi vòng dựng POC MỚI thì `EnsureDesignAssetsAsync` **ghi
đè cả `poc-demo.html`** về shell template. Input của vòng dựng mới chỉ có AI Design Spec + kịch bản UAT +
dữ liệu mẫu — không có file POC cũ, không có transcript, không có `PocComment` cũ (chúng đã sang
`Addressed` nên `TriagePocFeedbackUseCase` cũng không gom lại vì nó chỉ query `Open`). Kết quả: mọi góp ý
giao diện người dùng đã chấp nhận biến mất, và họ gặp lại đúng lỗi đã góp ý một lần rồi.

`PocUiConventionService` đóng chỗ rò bằng cách để thứ phải sống sót nằm **ngoài file bị sinh lại**: sau
mỗi vòng chỉnh sửa POC, các ghi chú vừa được sửa (`Sent`, đọc TRƯỚC khi chúng chuyển `Addressed`) được
chắt lọc thành **quy ước trình bày dùng lại được** (`poc-ui-convention.v1.md`) và lưu ở
`04_Implementation/poc-ui-conventions.json`. Mọi vòng dựng POC sau — mới lẫn chỉnh sửa — nối bộ này vào
prompt qua `PocUiConventionService.BuildPromptBlock` + `WorkflowTaskPromptBuilder`, kèm hai rào: chỉ áp
dụng khi màn hình tương ứng còn trong spec, và **spec luôn thắng khi mâu thuẫn** (quy ước nói về cách
trình bày, không đổi nghiệp vụ). Khác [`PocFeedbackMemoryService`](requirement-flow.md#vòng-học-chạy-ở-cổng-duyệt) — vốn đợi tới mốc duyệt
bản demo rồi mới bồi bài học vào checklist phỏng vấn của BA cho **các dự án sau** — bộ này chạy ngay sau
mỗi vòng sửa và giữ quy ước cho **chính dự án này**.

Fail-open như mọi tầng bộ nhớ: model lỗi/không đọc nổi ⇒ giữ nguyên bộ cũ. Và một kết quả **nghèo hơn**
bộ đang lưu bị từ chối — model được yêu cầu xuất lại toàn bộ bộ đã gộp, nên ít đi nghĩa là nó vừa đánh
rơi quy ước cũ, chứ không phải người dùng đổi ý. Trần 24 quy ước (giữ mới nhất): bộ này đi vào prompt của
mọi vòng dựng nên để phình vô hạn là lấy dần chỗ của chính AI Design Spec. Dự án chưa từng đi đường
"chỉnh bản demo" thì không có file, khối prompt rỗng, prompt POC y như trước.

### Ghi chú chỉnh trình bày bị hoãn tới bản demo dựng lại
Lượt gửi có nhóm "chỉnh demo" đi cùng nhóm tài liệu hoặc nhóm thiết kế thì nhóm "chỉnh demo" không đi Dev ngay (precedence ở trên) mà sang `Deferred`
(`Route = FixPoc`). Giữ chúng `Open` như trước thì người dùng phải tự nhớ gửi lại sau khi duyệt lại
requirement, và bản demo mới dựng lại vẫn mắc đúng các lỗi trình bày họ đã chỉ ra.

`PocDeferredFeedbackService` mang chúng vào **vòng dựng mới** bản demo kế tiếp (task `PocPreview` không
có `RevisionFeedback`) — không phải một vòng chỉnh sửa riêng chạy sau:
- Lúc duyệt requirement chưa có bản demo nào để vá: vòng dựng mới ghi đè `poc-demo.html` về template.
- Một vòng sửa riêng tốn một lượt trong trần `MaxRevisionRounds` trước khi người dùng kịp xem bản mới.
- Neo của ghi chú (selector, toạ độ) nói về bản demo CŨ, nên trong prompt chúng chỉ còn là chữ
  (màn hình + phần tử + nội dung, qua `PocCommentDigest`), **không có selector**. Hai rào giống bộ quy ước
  trình bày: chỉ áp khi màn hình còn trong spec, và spec thắng khi mâu thuẫn. Khối nối sau khối quy ước
  (`WorkflowTaskPromptBuilder`, tham số `deferredFeedbackBlock`).

Vòng dựng chạy **xong** ⇒ `MarkSentAsync` chuyển các ghi chú nó đã mang (và còn `Deferred` — người dùng
có thể rút một ghi chú trong lúc bản demo đang dựng) sang `Sent` + `RevisionTaskId` = task dựng, rồi đi
đúng đường đóng vòng của một lần sửa POC: chắt thành quy ước trình bày (`PocUiConventionService`) →
`Addressed`, người dùng kiểm lại bằng nút "vẫn chưa đạt". Vòng dựng hỏng ⇒ không đổi gì, vòng dựng sau
mang lại. Cùng lúc đó ghi chú đã gửi về Requirement được đóng thành `Addressed` (`MarkRoutedAppliedAsync`,
xem bảng ghi chú ở trên).

Ghi chú `Deferred` không vào hộp phân loại (triage/dispatch chỉ quét `Open`), hiện **Waiting** trên bảng
ghi chú (tooltip "chờ bản demo dựng lại"), và **xoá được** (thu hồi mềm) — chúng có thể nằm chờ lâu, và người đọc bản mô tả mới có thể
thấy góp ý ấy không còn đúng.

### Parity Brief ↔ Spec soát hai tầng, không chỉ màn hình
Spec là **đầu vào duy nhất** của bước dựng POC, nên thứ gì rơi rụng ở biên Brief→Spec thì POC thiếu
luôn và mọi cổng phía sau đều mù (chúng chỉ so POC với spec). `SpecBriefParityChecker` vốn chỉ so danh
sách màn hình; nay hai tầng theo thứ tự "mất mát đắt dần": **màn hình** → **quy tắc nghiệp vụ**
(`## Quy tắc cần nhớ` ↔ `§ 8`). Rule bị mất là bản
demo bấm được nhưng sai nghiệp vụ — đúng lớp lỗi mà audit POC không thể thấy vì nó chấm theo spec.

Màn hình so bằng `PocSpec.Matches` (dùng chung với audit POC). Rule là **câu**, không phải nhãn
ngắn, nên so bằng `TextSimilarity` (bằng nhau → chứa nhau → đủ tỷ lệ từ chung): spec diễn đạt lại cùng
một quy tắc bằng từ ngữ kỹ thuật hơn là chuyện bình thường, và một cổng kêu ở mọi lượt là một cổng sẽ
bị bỏ qua. `PocUatCoverage` nay dùng chung `TextSimilarity` thay vì bản sao riêng của nó.

**Vòng sửa không được hạ cấp chính bản spec.** Phát hiện lệch thì BA sửa đúng một vòng, và vòng đó phải
xuất lại **toàn bộ** spec — output dài nhất cả tuyến, nên phản hồi bị cắt giữa chừng là chuyện thường
(`LlmJson.ExtractObject` gặp ngoặc không cân thì coi như không có JSON). Kết quả vòng sửa vì vậy đọc
bằng bản strict `RequirementResponseParser.TryParseAiDesignSpec`: không đọc được ⇒ **giữ nguyên bản
vòng đầu**, log ghi "Không đọc được kết quả vòng sửa spec — giữ nguyên bản đã sinh." Nếu đường này rơi
vào khung dự phòng như lượt sinh đầu, nó lấy bản chép lại Product Brief đè lên một spec đang dùng được,
và nhìn từ log thì triệu chứng giống hệt một spec thật còn thiếu mục ("vẫn còn lệch") — cùng kỷ luật
với `TryParseProductBrief` ở vòng sửa Product Brief.

### Mỗi vòng dựng POC được chụp lại (`PocSnapshots`)
Vòng "Request revision" **ghi đè thẳng** lên `poc-demo.html`, nên bản người nghiệm thu vừa xem biến
mất. `PocVerification` giữ được lịch sử **kết quả kiểm** (rule pass/fail, hồi quy) nhưng không giữ
chính bản demo: từ vòng thứ hai trở đi người review chỉ còn bản bàn giao bằng chữ của agent để tin, và
phải rà lại cả POC từ đầu.

`PocSnapshots.TryCapture` chụp `poc-demo.html` thành `04_Implementation/poc-history/poc-demo.V{n}.html`
ngay khi mỗi task `PocPreview` hoàn tất (giữ 10 bản mới nhất). Trang POC Review liệt kê các vòng, mở
lại được từng bản qua `Mockup?version=n` — cùng quyền, cùng rào sandbox với bản hiện tại; số vòng chỉ
dùng để **tra trong danh sách file có thật**, không bao giờ ghép vào đường dẫn. Kèm theo là diff cấu
trúc với vòng liền trước: **màn hình thêm/bỏ** (`PocSnapshots.Diff`) — đơn vị mà người nghiệm thu nói
được thành lời, khác diff từng dòng HTML vốn báo "khác nhau toàn bộ" ở mọi vòng. Khi POC được dựng lại
từ đầu, `PocSnapshots.Reset` chạy cùng `PocVerification.Reset` vì cùng một lý do: các bản chụp của một
POC không còn tồn tại chỉ tạo ra so sánh vô nghĩa.

### Thử model khác cho Developer (biến thể POC)
Câu hỏi "model nào hợp với bước POC" không trả lời được bằng cách đổi model của agent Developer rồi dựng
lại: `Agent.AiModelId` là cấu hình **toàn cục**, đổi là mọi task Developer của các dự án khác đang chạy cũng
đổi model theo; còn dựng lại POC qua pipeline thì `PocVerification.Reset` + `PocSnapshots.Reset` xoá sạch
đúng thứ cần đặt cạnh nhau để so. Nên đây là một đường **thí nghiệm** tách hẳn khỏi pipeline: tab "Try
another model" trong hộp thoại "Technical details" trang POC Review (quyền `PocModelExperiment`, mặc định
chỉ Admin — xem [screens-and-permissions.md](screens-and-permissions.md)) chứa ô chọn model + bảng so sánh.

**Một lượt thử** (`StartPocVariantUseCase` → dòng `PocVariants` Queued → `PocVariantWorker` →
`PocVariantRunner`) dựng lại bản demo **từ đầu** bằng model được chọn:

- **Cùng đầu vào với bản chính** — AI Design Spec lấy từ chính task `PocPreview` đã DỰNG MỚI bản chính
  (`SourceTaskId`; bỏ qua các vòng "Request revision" vì chúng vá trên bản có sẵn), cùng prompt template,
  cùng bộ UAT + quy ước trình bày đang có (chỉ ĐỌC, không sinh lại), cùng dữ liệu mẫu thật, cùng đường dựng
  ([agent nhiều bước](#dựng-poc-bằng-agent-nhiều-bước-gom-việc-vào-ít-lượt) với cùng ngân sách bước
  `DeliveryPipeline.PocStepBudget` và cùng bộ tool POC), cùng `PocDemoSeeder` và cùng tầng tự kiểm `AuditPocContent`.
  Thứ duy nhất khác là model. Agent xong thì runner **tự chạy thêm một vòng `AuditPocContent` chốt** (fail-open):
  bảng đọc `poc-verification.json`, nên một model bỏ qua bước tự kiểm (hoặc sửa tiếp sau lần audit cuối)
  sẽ ra cột trống/lệch với bản demo thật — đúng loại model cần bị lộ ra, không được trốn khỏi bảng.
- **Thư mục riêng ngoài workspace dự án** — `{RootPath}/.poc-variants/{projectId:N}/{variantId:N}/`
  (`WorkspacePathResolver.GetPocVariantPath`), agent chạy với thư mục này làm gốc
  (`AgentRunOverrides.WorkspacePath` → `WorkspaceTools.SetWorkspacePath`). Ngoài chứ không trong
  `04_Implementation/`: bên trong thì các bước Implementation/BugFix thấy thêm hàng loạt `poc-demo.html` lạ
  qua `ListFiles`/`SearchInFiles`, và bản nhân bản dự án chép theo. Khoá theo `projectId` để đổi tên dự án
  (đổi tên thư mục workspace) không làm mồ côi biến thể.
- **Không đổi gì của dự án** — không đụng `poc-demo.html`, bản chụp các vòng, kết quả tự kiểm, ghi chú POC
  (kể cả ghi chú hoãn), trạng thái run/cổng duyệt, trần `MaxRevisionRounds`, `AgentConversations`; model
  thử chỉ áp cho lượt này (`AgentRunOverrides.Model`). Các lời gọi model ghi `Purpose = PocVariant` và vẫn
  tính vào chi phí dự án (trang Usage, Budget guard) — thí nghiệm tiêu tiền thật.
- **Hai giới hạn tiêu tiền** — mỗi dự án chỉ một lượt Queued/Running, giữ tối đa 10 biến thể
  (`StartPocVariantUseCase.MaxVariantsPerProject`; muốn thử thêm thì xoá bớt). Cho phép thử LẠI đúng model
  đang dùng: hai lượt cùng model là cách duy nhất biết chênh lệch giữa các model có lớn hơn độ dao động của
  chính một model không.
- **Worker tuần tự toàn hệ thống** — `PocVariantWorker` tách khỏi `AgentTaskWorker` (lượt thử không thuộc
  run nào, không dẫn tới cổng duyệt) và chạy mỗi lần một biến thể, vì chi phí được chốt theo khung giờ chạy
  (log `Purpose = PocVariant` của dự án trong `[StartedAt, FinishedAt]`) — hai lượt chồng nhau sẽ cộng lẫn
  tiền của nhau. Restart giữa chừng ⇒ lượt Running thành Failed (không resume).

**Bảng so sánh** (`ListPocVariantsQuery`) xoay: mỗi CỘT một bản demo, mỗi HÀNG một tiêu chí — đọc ngang là
so các model trên cùng một tiêu chí, ô tốt nhất được tô (chỉ xét cột đã có số đo; một cột hoặc mọi cột bằng
nhau thì không tô). Tiêu chí lấy từ `poc-verification.json` của từng bên nên bản chính và biến thể chấm
bằng **cùng một thước**: màn hình phủ theo spec, kịch bản UAT có mục PASS, máy bấm thử theo kịch bản,
business rule self-test, luồng end-to-end **ngoài** bộ nghiệm thu, số điểm chưa đạt; cộng thời gian dựng,
số lời gọi model, token và chi phí. Hai tiêu chí cố ý không đếm chồng nhau:
- **Luồng end-to-end chỉ đếm các mục `pocScenarios()` không khớp kịch bản UAT nào**
  (`PocVerificationSummary.ScenarioResultsOutsideUat`, tách bằng `PocUatCoverage.OutsideUat`). Mục khớp
  UAT đã nằm ở dòng "kịch bản UAT có mục PASS"; đếm chung thì hai dòng gần như luôn bằng nhau, còn
  mẫu số do agent tự đặt nên bỏ sót kịch bản khó vẫn ra 100%. File tự kiểm ghi trước khi có trường này
  và dự án có UAT ⇒ ô hiện "—" (không tách lại được), chạy lại audit là có số. Dòng tương ứng trên tab
  "Máy đã tự kiểm" của POC Review cũng chỉ đếm phần ngoài UAT.
- **Số điểm chưa đạt đếm mỗi kịch bản gãy một lần** — xem
  [delivery-pipeline.md](delivery-pipeline.md#kịch-bản-nghiệm-thu-uat-là-đích-của-bước-poc-không-phải-phụ-lục). Cột bản chính lấy chi phí từ log của chính task dựng nó (cùng run, cùng agent, trong
khung giờ task, `Purpose` là `AgentRun` — hoặc `PocBuild`/`PocRepair` với các bản chính dựng trước khi đường dựng một lượt bị gỡ) và ghi model **thật** đã trả lời các lời gọi đó, không phải model đang gán cho Developer;
nếu bản chính đã qua các vòng chỉnh sửa thì header cột nói rõ, vì số đo của nó là của bản hiện tại còn
biến thể là MỘT lượt dựng. Lượt đang chạy vẫn hiện số đo (tầng tự kiểm ghi file sau mỗi vòng audit) kèm
dòng tiến độ mới nhất (`PocVariantProgress`, singleton trong bộ nhớ); hộp thoại poll 5 giây khi có lượt
chờ/chạy và ngừng khi đóng. Mỗi cột mở được bản demo của nó (`PocVariants/Demo` — cùng `PocDemoResponse`,
cùng CSP sandbox với bản chính).

Không có thao tác "dùng biến thể này làm bản chính": mục đích là chọn model — chọn xong thì đổi model của
Developer ở màn **Agents** như bình thường. Xoá biến thể dọn cả thư mục của nó; chi phí đã tiêu vẫn nằm ở
`AgentModelCallLogs`.

---
