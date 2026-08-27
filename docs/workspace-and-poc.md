# Workspace & sản phẩm sinh ra

## Bố cục

Mỗi project một thư mục dưới `AgentWorkspace:RootPath`, tên = `{tên-đã-chuẩn-hóa}-{8-ký-tự-đầu-của-Id}` (không đụng nhau khi hai tên chuẩn hóa giống nhau):

```
{RootPath}/{project-key}/
  01_Requirement/     # Product Brief (draft/ + V1, V2...), BRD/SRS/FSD/UserStories
  02_Design/          # AI Design Spec theo V{n}
  03_Architecture/    # Đề xuất kiến trúc của Tech Lead
  04_Implementation/  # poc-demo.html (POC) + poc-ui-conventions.json + src/ (code đa file) + code-review.md
  05_Test/            # Test cases + báo cáo test
```

(Danh sách phase khai báo ở `Services/Artifacts/ProjectWorkspaceLayout.cs`; mỗi phase có `draft/` và các thư mục version `V{n}`.)

## POC demo

- File `04_Implementation/poc-demo.html` — seed từ `Prompts/Design/poc-template.html` ở bước PocPreview; hai vùng marker do `PocTemplate.cs` quản: `POC_CONTENT` (HTML) và `POC_SCRIPT` (JS; shell expose `window.pocToast`/`window.pocNavigate`/`window.pocRole`/`window.pocSetRole`).
- Yêu cầu của bước POC: hiện thực **Business Rules của spec thành hành vi thật** (tính toán, validate, chuyển trạng thái, dữ liệu đổi theo vai) chứ không chỉ màn hình tĩnh; agent tự soát bằng `AuditPocContent` (`PocAudit.cs` đối chiếu cả độ phủ với "Screens To Generate" + "BR-n" của spec, do `PocSpec.cs` parse).
- **Menu gom theo LÔ, không rải phẳng** (`PocNavGroups` + `PocAudit.CheckNavGrouping`): hai bảng của buổi phỏng vấn đẻ ra màn hình hàng loạt — mỗi thông tin kiểu CHỌN nguồn "ứng dụng tự quản lý" thành một màn `"<tên> Catalog"` ([`EntityMapBuilder.ManagedListScreens`](requirement-flow.md#tên-màn-hình-là-nhãn-menu-của-bản-demo-nên-nó-ngắn-và-bằng-tiếng-anh)), mỗi dòng bảng báo cáo thành một màn `"<tên> Report"`. Một dự án nhân sự bình thường có 5–8 danh mục và 3–5 báo cáo, nên để phẳng thì sidebar của bản demo dài gấp đôi phần nghiệp vụ thật và người xem phải cuộn qua một dãy màn CRUD giống hệt nhau mới tới được luồng chính — đúng thứ họ mở demo để xem. Từ **3 màn hình cùng lô** trở lên, `poc-preview.v1.md` bắt gom hết vào ĐÚNG MỘT mục `children` (shell đã có sẵn nhóm xổ xuống), và audit báo ISSUE khi chúng còn nằm trần ở menu gốc hoặc bị chia ra nhiều nhóm. Nhãn NHÓM do agent đặt theo ngôn ngữ UI của spec ("Danh mục"/"Báo cáo"); nhãn mục CON vẫn là tên màn hình nguyên văn vì nó còn phải khớp `data-view` — tiêu đề nhóm KHÔNG phải màn hình nên không có section. Phân loại tất định theo TÊN và cố ý hẹp: `dashboard`/`overview` KHÔNG tính là báo cáo (một "Employee Dashboard" thường là màn chủ của một vai), vì một cổng bắt nhầm là một cổng agent học cách phớt lờ. Mục lá đầu tiên là mục mở demo — tiêu đề nhóm không bao giờ nhận `active` (`PocTemplate.RenderNav`), nếu không thì một menu bắt đầu bằng nhóm mở ra với mục đang sáng không ứng với màn nào. Và chỉ nhóm CHỨA mục đang mở mới xổ sẵn, các nhóm khác để đóng: nhóm đầu tiên luôn xổ sẵn (luật cũ) trả nguyên dãy màn danh mục lên sidebar, đúng thứ việc gom nhóm sinh ra để thu lại. Người xem bấm tiêu đề nhóm để xổ; shell tự mở nhóm khi một màn bên trong được mở bằng code (`activateNav`), nên lượt CLICK MENU và lượt lái UAT không bị nhóm đang đóng chặn.
- **Đổi vai bằng khối VIEW AS, KHÔNG có màn đăng nhập** (`PocTemplate.ReplaceRoles` + engine trong shell template): vai của spec (`## 6b. Permission Matrix`, `PocSpec.Roles` đọc ra) được `SetPocContent('roles')` dựng thành các nút `.view-as-item` ghim **cuối sidebar**, vai đầu tiên là vai mở demo. Quyền xem khai báo bằng `data-roles="Vai A,Vai B"` trên mục menu (`PocNavItem.Roles`) và trên `<section class="page-view">`; không khai báo = mọi vai đều thấy. Shell tự ẩn mục menu ngoài vai, mở màn đầu tiên của vai, đặt `body[data-poc-role]` rồi gọi `window.pocOnRoleChange(role)` / bắn `poc:rolechange` để script nghiệp vụ render lại dữ liệu theo vai. Một màn hình của vai khác được mở BẰNG CODE (`pocNavigate`, lượt kiểm runtime) thì shell chuyển vai theo màn đó thay vì hiện một màn trống.
  Vì sao bỏ màn Login giả: POC không có backend nên "đăng nhập" chỉ là một cửa gác không kiểm gì — nó bắt người xem demo bấm thêm một lượt trước khi thấy nghiệp vụ, và **giấu luôn phần còn lại khỏi chính các cổng tự kiểm**. Lượt CLICK MENU chỉ thấy được cái form đó; còn lượt lái UAT thì tệ hơn: tập điều khiển của nó không có `<select>` (xem lượt bấm thử bên dưới) nên bước "đăng nhập với vai Quản lý" thực tế bấm nút Đăng nhập với vai mặc định đang chọn — kịch bản của vai này chạy dưới vai khác mà vẫn báo `clicked`. Nay mỗi vai là một nút mang đúng tên vai nên lái đúng vai, và `RunUatStepAsync` chấm bước đổi vai bằng `window.pocRole()` chứ không bằng "chữ trên màn có đổi không" (hai vai có thể cùng mở một màn).
- **Lượt ĐỔI VAI** (`PocRuntimeChecker.CheckRoleSwitchingAsync`, chạy sau lượt CLICK MENU): bấm thật từng vai và báo ISSUE khi vai không đổi được, khi **sidebar trống trơn với vai đó**, hoặc khi đổi vai xong vẫn đứng ở màn hình của vai khác. Phần tĩnh (`PocAudit.CheckRoles`) soát trước ba thứ rẻ hơn: spec có ≥2 vai mà demo không khai báo vai nào, `data-roles` gõ sai tên vai (mục/màn hình biến mất với MỌI vai), và một màn Login **spec không hề yêu cầu** (WARNING).
- **Kiểm ở hai bề rộng**: `PocRuntimeChecker` đi qua từng màn hình ở 1440px rồi mở lại toàn bộ ở **390px** (điện thoại) — tràn ngang ở bề rộng nào cũng thành ISSUE, và ảnh mobile cũng được đưa cho tầng Visual QA. Trước đây mọi thứ chỉ kiểm ở desktop nên lớp lỗi "vỡ trên màn hẹp" không cổng nào thấy.
- **Lượt BẤM THỬ THEO KỊCH BẢN NGHIỆM THU** (`PocRuntimeChecker.DriveUatScenariosAsync` + `RunUatStepAsync`, chạy trước lượt CLICK MENU; kết quả ra `UatDriveResults` và dòng "Máy bấm thử theo kịch bản" trên POC Review): lấy chính bộ kịch bản UAT sinh TRƯỚC khi POC được dựng, mỗi kịch bản một lần tải trang sạch, đi từng bước — tìm điều khiển ứng với bước → click → so màn đang mở + modal trước/sau. Chỉ kết luận hai khuyết tật không thể chối cãi: **thiếu điều khiển** (nhãn trong ngoặc kép của bước không có ở đâu trên POC) và **nút chết** (bấm được mà không đổi gì). Toast của shell bị loại khỏi phép so vì shell tự toast cho mọi `.btn`, kể cả nút chưa nối logic.
  Bốn luật tìm-điều-khiển giữ cho cổng này không báo oan, mỗi luật vá đúng một đường đã từng đánh trượt cả bộ kịch bản của một POC chạy được:
  - **Mục menu nằm trong tập điều khiển.** `RenderNav` dựng mục menu bằng `<div class="nav-item">` nên nó không thuộc `button/a/.btn`; thiếu nó thì bước "Mở màn hình X" — bước mở đầu của gần như mọi kịch bản — không khớp được gì. Tiêu đề NHÓM bị loại (chỉ xổ/thu, không mở màn nào), mục con của nhóm **chưa xổ** vẫn tính là bấm được vì shell bắt click bằng delegation rồi tự mở nhóm; chỉ mục bị `applyRoleToNav` đặt `display:none` mới là ngoài tầm với. Nhãn đọc bằng `textContent` khi `innerText` rỗng — mục trong nhóm chưa xổ không có innerText.
  - **Nhãn trùng tên một `data-view` ⇒ ưu tiên mục menu** hơn một nút cùng tên trong nội dung: "Mở màn hình 'Duyệt đơn'" phải mở màn, không phải duyệt đại một bản ghi.
  - **Bước đã nêu nhãn trong ngoặc mà nhãn đó không phải điều khiển ⇒ bỏ qua**, không rơi xuống khớp ngược. Khớp ngược (nhãn nút nào xuất hiện nguyên văn trong câu bước) chỉ dùng cho bước KHÔNG có ngoặc — dùng cả cho bước có ngoặc thì "Mở màn hình 'HRBP Approval'" bấm trúng nút vai **HRBP** rồi chấm cú no-op đó là nút chết.
  - **Bấm lại điều khiển đang `active` (vai đang chọn, mục menu đang mở) là bỏ qua, không phải nút chết**: vai đầu tiên chính là vai demo mở lên, nên mọi kịch bản mở đầu bằng "chọn vai <vai mặc định>" sẽ trượt sạch nếu chấm no-op đó là khuyết tật.
- **NEO CHỈ CHỖ cho từng bước nghiệm thu** (`UatAnchor` + cổng `PocUatAnchors`, kiểm TĨNH nên không cần browser): mỗi bước của mỗi kịch bản UAT phải có một phần tử mang `data-uat="{số kịch bản}.{số bước}"` (đánh số **từ 1**, theo đúng thứ tự khối UAT trong prompt; một phần tử phục vụ nhiều bước thì ghi nhiều mã cách nhau bằng dấu cách — đúng ngữ nghĩa `[data-uat~="1.4"]`). Bước thao tác neo vào chính nút/ô nhập/mục menu người dùng bấm (bước nhập cả cụm trường ⇒ thẻ `<form>` bao quanh); bước KIỂM TRA neo vào phần tử **hiển thị kết quả** cần đối chiếu, không được bỏ trống. Cổng báo cả hai phía: bước thiếu neo, và mã neo không ứng với bước nào (agent đánh số 0-based — lỗi im lặng tệ nhất, vì HTML nhìn vẫn "có neo" mà không mã nào tra ra phần tử). Neo phục vụ hai chỗ: trang POC Review tô sáng đúng phần tử khi người nghiệm thu bấm vào một bước, và `RunUatStepAsync` click theo neo **trước** khi rơi xuống lượt đoán theo nhãn chữ.
  Vì sao là cổng chứ không phải "có thì tốt": neo là thứ duy nhất nối một câu tiếng Việt trong checklist với một phần tử cụ thể. Bản đầu tiên của tính năng chỉ chỗ tự **đoán** phần tử bằng cách so từ của câu bước với chữ trên các nút/ô bảng — trùng đúng một từ là đủ để khoanh vàng nhầm chỗ ("Kiểm tra JD được tạo với mã HcP-JD-XXX" khớp trúng một ô bảng bất kỳ có chữ "JD"), còn bước kiểm tra thì vốn không có nút nào để trỏ. Chỉ chỗ SAI tệ hơn không chỉ chỗ, nên đường đoán đã bị gỡ hẳn thay vì siết ngưỡng.
- **Lượt CLICK MENU** (`PocRuntimeChecker.CheckNavClickRoutingAsync`, chạy sau lượt lái UAT): bấm THẬT từng mục menu đang hiển thị và so màn hình đang mở với nhãn mục đó. Lượt đi màn hình ở trên gọi `window.pocNavigate()` bằng JS nên nó MÙ với lớp lỗi "click menu chết" — script nghiệp vụ dựng lại sidebar làm mất handler của shell, hoặc gắn handler riêng gọi `pocNavigate()` ngay trong lúc xử lý click của chính mục đó (click tổng hợp lồng nhau bị cờ *click in progress* của DOM nuốt). Người xem demo thấy breadcrumb đổi mà nội dung đứng yên; nay thành ISSUE. Bản thân shell cũng đã sửa: nav bắt click bằng **delegation** và `pocNavigate` gọi thẳng hàm mở màn thay vì `item.click()`.
- **Dữ liệu mẫu THẬT + ngôn ngữ UI** (`PocSampleDataCheck`, chạy trong `PocAudit`): text bóc từ Excel/Word người dùng đính kèm vốn CHỈ được nạp vào prompt sinh spec (`RealSampleDataReader` — cùng hàm cho cả hai đầu) mà không có gì kiểm chứng, nên POC vẫn dễ demo bằng "Sản phẩm A / Nguyễn Văn B" — lớp lỗi rẻ nhất để sửa nhưng đắt nhất về niềm tin: người dùng nghiệp vụ mở demo thấy dữ liệu bịa là mất tin, dù mọi công thức đều đúng. Ba phép scan tất định trên vùng `POC_CONTENT` (không tính shell — shell có chữ mẫu riêng), và cố tình dè dặt (chỉ ISSUE khi bằng chứng rõ ràng):
  - **Không dùng gì từ tài liệu** ⇒ ISSUE (kèm vài giá trị thật để agent seed lại); dùng ít ⇒ WARNING; không có tài liệu nào ⇒ bỏ qua hẳn.
  - **Placeholder kinh điển** ("Nguyễn Văn A", "Product B", "Lorem ipsum", `@example.com`) ⇒ ISSUE khi ĐÃ có tài liệu thật để dùng, WARNING khi không.
  - **Spec tiếng Việt mà chữ HIỂN THỊ của POC không có lấy một dấu** ⇒ ISSUE. Chỉ tính chữ hiển thị: một `data-view="Đăng nhập"` không chứng minh nhãn là tiếng Việt.
- **Lịch sử các vòng dựng** (`PocSnapshots`): mỗi task `PocPreview` xong thì `poc-demo.html` được chụp thành `04_Implementation/poc-history/poc-demo.V{n}.html` (giữ 10 bản mới nhất). Vòng "Yêu cầu chỉnh sửa" ghi đè thẳng lên bản hiện tại, nên không có bản chụp thì người nghiệm thu ở vòng sau chỉ còn bản bàn giao bằng chữ của agent để tin. Trang POC Review liệt kê các vòng (mở lại qua `Mockup?version=n` — cùng quyền + sandbox, số vòng chỉ dùng để tra trong danh sách file có thật) kèm diff **màn hình thêm/bỏ** so với vòng liền trước. Dựng lại POC từ đầu ⇒ `PocSnapshots.Reset` chạy cùng `PocVerification.Reset`.
- **Chống hồi quy giữa các vòng sửa**: `poc-verification.json` giữ vòng kiểm mới nhất, các vòng cũ rơi vào `poc-verification-history.json`. Mỗi lượt audit so với vòng trước (`PocVerification.DetectRegressions`) và báo mục từng PASS mà nay FAIL **hoặc biến mất** (xoá assertion cũng bị tính là hồi quy) — mục `REGRESSIONS` trong báo cáo cho agent, và một khối riêng trên trang POC Review. Khi POC được dựng lại từ đầu, `PocVerification.Reset` xoá cả hai file để không so với một bản POC không còn tồn tại.
- Xem POC: `GET /Projects/Mockup?projectId=` — endpoint **sandbox riêng** (HTML do LLM sinh không được thả vào layout chính).
- **Review POC (ghim ghi chú lên phần tử)**: `GET /Projects/PocReview?projectId=` nhúng POC trong iframe ở chế độ review (`Mockup?review=True` tiêm `wwwroot/js/poc-annotator.js` lúc phục vụ — file trên đĩa không đổi). Người xem bật "chế độ ghim" (nút bật/tắt trên **command bar**, cạnh "Mở tab riêng" — cả hai là hành động cấp trang nên đứng ở vùng phải của thanh lệnh chứ không thành một hàng nút riêng bên trên iframe), click phần tử → annotator gửi mô tả (màn hình `data-view`, nhãn, CSS selector, vị trí %) lên trang cha qua postMessage → lưu bảng `PocComments`. Pin đánh số vẽ ngay trên phần tử. Sandbox giữ nguyên (origin opaque, không cookie) — mọi thao tác ghi đều từ trang cha. Các ghi chú `Open` được gom vào "Yêu cầu chỉnh sửa" tại cổng POC (xem [delivery-pipeline.md](delivery-pipeline.md#cổng-duyệt-gates--trạng-thái-waitingforhuman)).
  - **Bấm vào một BƯỚC ⇒ POC chỉ chỗ.** `poc-review.js` gửi xuống iframe **mã neo** của bước (`data-anchor="2.3"`, do Razor in ra từ chỉ số gốc kịch bản/bước — cùng `UatAnchor` mà prompt và cổng audit dùng), annotator mở đúng màn hình rồi tô sáng phần tử `[data-uat~="2.3"]`. READ-ONLY: chỉ tô sáng, **không thao tác thay người dùng** — họ vẫn tự bấm để kiểm chứng nghiệp vụ thật; và đi theo nhịp người xem (một lần bấm = một lần chỉ chỗ), không có lượt tự chạy hết kịch bản theo đồng hồ. Không tìm được thì **nói thẳng bằng chữ** ngay dưới bước đó (`.uat-step-hint`) chứ không nháy cả trang cho có phản hồi, và phân biệt ba tình huống khác hẳn nhau với người dùng: bản demo **không có neo nào** (dựng trước cơ chế này — đừng bắt họ thử lại), **thiếu đúng bước này** (cổng audit đã báo), và **phần tử chưa hiện** (làm các bước trước đã).
  - **Bố cục hai cột, mỗi cột một câu hỏi.** Cột trái (`.poc-main-col`) là **bản demo + những gì nói về chính bản demo**: khung iframe, rồi ngay dưới nó thẻ "Ghi chú trên POC" (danh sách ghi chú chung, ô nhập, hai nút đóng vòng "Bản demo đã đạt" / "Gửi ghi chú cho đội xử lý", bảng lịch sử ghi chú). Cột phải (`.poc-comments-panel`) chỉ còn phần **đối chiếu yêu cầu**: kịch bản kiểm thử + các vòng đã dựng. Ghi chú ở cột trái vì một mục trong danh sách và một pin vẽ trên demo là cùng một thứ nhìn từ hai phía — để chúng ở hai cột là bắt mắt người dùng nhảy qua lại; và vì trước đây nó nằm SAU toàn bộ danh sách kịch bản, nên POC càng nhiều kịch bản thì đúng hai thao tác KẾT THÚC buổi review càng bị đẩy sâu. Hai hệ quả kỹ thuật: (a) `#pocReviewRoot #pocFrame` trừ thêm chiều cao so với trang khách để tiêu đề ghi chú ló lên trên mép màn hình — không ai cuộn xuống tìm thứ mình không biết là có; (b) click một mục ghi chú sẽ kéo khung demo về tầm mắt (`scrollIntoView`) **chỉ khi** nó đã trôi qua mép trên, nếu không cú nháy pin bên trong iframe là vô hình.
  - **Mỗi thẻ kịch bản là một `<details>` gập được**, mặc định chỉ mở kịch bản CHƯA XONG đầu tiên; đóng/mở tay nhớ trong `localStorage` theo project (`poc-uat-open-<projectId>`, khóa là `data-index` = chỉ số gốc), và tick nốt bước cuối thì thẻ **tự gập** — trừ khi nó đang giữ ô nhập ghi chú hoặc đã có ghi chú ghim bên trong (gập lúc đó là giấu mất thứ đang cần nhìn). Lý do: một POC thật có 8-10 kịch bản × 5-8 bước, mở hết là hơn năm mươi dòng trong một cột hẹp, và panel không bao giờ ngắn đi trong lúc review. Dòng tiêu đề khi gập phải tự đủ để quyết định có mở hay không, nên nó mang **tiến độ theo bước của riêng kịch bản** ("3/7 bước") và **số ghi chú đang nằm trong thân thẻ** — thẻ gập là giấu ghi chú, badge là thứ duy nhất báo còn gì bên trong. Kéo theo: tiêu đề panel đếm **kịch bản** (`(3/8)`) chứ không đếm bước, vì đơn vị người review đi theo là kịch bản, đúng bằng số thẻ đang đóng phía dưới.
  - **Ghi chú của một kịch bản nằm TRONG thẻ kịch bản đó.** Bấm "Báo lỗi kịch bản này" ⇒ `poc-review.js` *chuyển* ô nhập (`#pocCommentForm` — chỉ có MỘT trên trang, dùng chung với đường ghim trên POC) vào `.uat-scenario-form` của chính thẻ đang bấm, và các ghi chú đã ghim cho kịch bản render vào `.uat-scenario-notes` của thẻ đó thay vì rơi xuống danh sách chung cuối cột. Lý do: người review đối chiếu TỪNG kịch bản, để ô nhập và ghi chú ở cuối cột là bắt họ cuộn đi rồi cuộn về mới biết mình vừa ghi gì cho kịch bản nào. Hai hệ quả kỹ thuật: (a) form được **chuyển chỗ chứ không nhân bản** — hai form đồng thời nghĩa là hai `pendingPick`, hai listener submit và một antiforgery token bị chia đôi; chỗ đứng mặc định giữ bằng một comment node để lúc đóng còn biết trả về đâu; (b) mọi handler của một mục ghi chú delegate từ **gốc trang** `#pocReviewRoot` — tổ tiên chung của cả hai chỗ, mà từ lúc danh sách chung sang cột trái thì hai chỗ đó ở hai CỘT khác nhau; delegate từ một cột là mất trắng handler (xóa / "vẫn chưa đạt" / click nháy pin) của cột kia.
  - **Lịch sử ghi chú theo phiên bản Product Brief** thay cho panel "Nhật ký vòng sửa" cũ (`GetPocNoteHistoryQuery`, bảng ở cuối cột trái, gom theo `BriefVersion`, bản mới nhất mở sẵn). Ba nguồn về một dòng thời gian vì người truy lại chỉ có một câu hỏi — *bản V{n} từng bị chê gì và ai xử lý ra sao*: ghi chú Brief (`Target = Brief`), ghi chú ghim trên POC (kể cả dòng đã thu hồi), và các vòng Dev chỉnh demo (`AgentTask` có `RevisionFeedback` — bàn giao toàn văn của agent là cột "đã xử lý" của chính dòng đó, còn `PocComment.AddressedNote` chỉ là bản cắt 1500 ký tự hiện cạnh ghi chú). Panel cũ chỉ có nửa câu chuyện phía agent. **Bảng chỉ đọc**: không dòng nào bị xoá — bỏ một ghi chú là thu hồi mềm (`WithdrawnAtUtc`), và nút 🗑 chỉ còn hiện với ghi chú `Open`. Vòng sửa đứng ở version của chính các ghi chú nó mang đi (`PocComment.RevisionTaskId`); vòng chạy bằng nhận xét gõ tay không suy ra được version nên rơi vào nhóm "không rõ phiên bản" thay vì bị đoán bừa.
  - **Danh sách ghi chú KHÔNG lọc theo version.** Ghi chú của các bản Brief trước vẫn nằm trong danh sách làm việc, chỉ gắn thêm nhãn version (`.poc-badge.version`, ghi chú của bản đang xem thì không gắn). Lọc đi sẽ đúng là thứ người dùng phàn nàn — approve xong là "mất hết" — và ghi chú chưa gửi của bản trước sẽ không còn đường nào gửi đi.
  - **Ghi chú đã gửi về Requirement rời danh sách làm việc** (`activeComments()` trong `poc-review.js`, lọc `Status = RoutedToRequirement`) — rời cả pin trên bản demo, vì số pin và số mục trong danh sách là cùng một dãy. Trạng thái đó đã hết đường đi ở trang này: không thu hồi được (🗑 chỉ hiện với `Open`), không mở lại được (`ReopenPocCommentUseCase` chỉ nhận `Addressed`/`Sent`), không lượt gửi nào gom nó nữa (triage/dispatch chỉ quét `Open`) — trong khi bảng lịch sử ngay bên dưới đã giữ nguyên văn nó kèm neo *màn hình · phần tử*. Để lại là một bản sao chiếm chỗ của những ghi chú CÒN phải xử lý. Không im lặng: danh sách in một dòng đếm "*n* ghi chú đã gửi về Requirement" trỏ xuống bảng lịch sử, đếm sống theo dữ liệu vừa nạp (bấm "Gửi ghi chú" chỉ nạp lại danh sách, bảng lịch sử là bản render lúc mở trang). Chỉ đúng trạng thái này — `Sent` còn phải chờ kiểm lại, `Addressed` còn nút "vẫn chưa đạt". Trang khách (`poc-share.js`) KHÔNG lọc: ở đó không có bảng lịch sử, giấu đi là mời khách ghim lại đúng góp ý đã gửi.
  - Ghi chú tìm về đúng thẻ bằng **tiêu đề kịch bản** trong `PocComment.ElementLabel` (`"Kịch bản: <title>"`) — `ElementPath` rỗng vì không click phần tử nào trong POC, nên đó là thứ duy nhất đi qua DB. Đổi tiền tố ấy là làm mồ côi mọi ghi chú kịch bản đã lưu. Kịch bản biến mất ở vòng POC mới ⇒ không khớp thẻ nào và ghi chú rơi về danh sách chung (fail-open, không nuốt mất ghi chú).
- **Một đường đóng vòng cho người dùng nghiệp vụ** ngay tại trang POC Review (cần `RequirementsManage`), đi qua hai bước:
  - `POST /Projects/TriagePocFeedback` — `TriagePocFeedbackUseCase` phân loại TỪNG ghi chú `Open` (`poc-feedback-triage.v1.md`) thành "lỗi trình bày của bản demo" hay "tài liệu yêu cầu hiểu sai", trả về bảng đề xuất cho hộp xác nhận. Chỉ đọc + gọi model, **không đổi trạng thái gì**; người dùng đổi nhóm được từng dòng. Model hỏng / chưa cấu hình BA ⇒ mọi ghi chú rơi về nhóm RẺ kèm cờ `classified = false` (fail-safe: không tự đề xuất đường đắt bằng một kết quả phân loại không có).
  - `POST /Projects/DispatchPocFeedback` — `DispatchPocFeedbackUseCase` gửi theo đúng bảng người dùng vừa xác nhận: nhóm "chỉnh demo" đi `RequestStageRevisionUseCase` (`onlyStage: PocPreview`, đếm chung trần `MaxRevisionRounds`; rào `onlyStage` để quyền "sửa demo" không nới thành quyền điều khiển các bước kỹ thuật phía sau), nhóm "hiểu sai yêu cầu" đi `RoutePocFeedbackToRequirementUseCase` (soạn một lượt user gửi BA bằng `poc-feedback-compose.v1.md` rồi chạy lại workflow soạn draft). **Mỗi đường chỉ nhận ĐÚNG tập con của nó.**
  - **Precedence** khi cả hai nhóm đều có ghi chú: chạy đường tài liệu, và GIỮ NGUYÊN `Open` các ghi chú nhóm chỉnh demo — POC sắp dựng lại từ tài liệu đã sửa nên vá HTML lúc đó vừa phí một lượt trong trần, vừa cho ra bản vá bị bỏ đi ngay. Hộp xác nhận nói rõ điều này trước khi gửi.
  - Vì sao gộp từ hai nút: hai nút cũ (`RequestPocFix` / `RoutePocFeedbackToRequirement`) bắt người xem demo tự phân loại ghi chú của mình, và **cả hai đều nuốt trọn mọi ghi chú `Open`** — một buổi review lẫn hai loại thì không nút nào đúng. Nặng nhất là đường Requirement cũ: nó tự lọc bằng LLM nhưng vẫn đánh dấu TẤT CẢ ghi chú `Open` thành `RoutedToRequirement`, kể cả các ghi chú thẩm mỹ đã bị loại khỏi tin nhắn gửi BA — chúng biến mất khỏi đường vá POC và `ReopenPocCommentUseCase` cũng không mở lại được (chỉ nhận `Addressed`/`Sent`), tức là mất trắng.
- **Nghiệm thu bản demo** (`POST /Projects/AcceptPoc`, quyền `RequirementsManage`): điểm DỪNG của hành trình phía người yêu cầu. Trước đây trang chỉ có các đường "còn sai chỗ này" (ghim ghi chú / nhờ Dev chỉnh / gửi về Requirement) mà không có đường nào nói "được rồi": cổng duyệt thật nằm ở Agent Dashboard sau quyền `DeliveryAdvance`, nên đội delivery phải đi hỏi miệng và người yêu cầu không có cách nào tự nói "xong". `AcceptPocUseCase` ghi `Project.PocAcceptedAtUtc/PocAcceptedBy` và báo cho người có quyền duyệt (`NotificationType.PocAccepted`) — **KHÔNG tự đẩy pipeline**: đi tiếp vẫn là quyết định ở cổng POC, để một cú bấm của người dùng nghiệp vụ không âm thầm khởi động các bước đắt tiền.
- Khi task là revision, worker **bỏ qua re-seed** POC để không ghi đè sản phẩm cũ về placeholder.

## Khung Bosch & tải source

- `Project.IsUseBoschTemplate = true` (mặc định) ⇒ `BoschTemplateSeeder` clone repo khung chuẩn (backend .NET + Angular) từ `BoschTemplate:BackendRepoUrl/FrontendRepoUrl` vào workspace làm skeleton (idempotent; URL trống thì bỏ qua). Pipeline dùng prompt bản `-bosch`.
- **Tải code sinh ra**: `GET /Projects/DownloadSource?projectId=` — `ImplementationSourcePackager` nén `04_Implementation/src/` thành zip.

---

### Sửa thông tin dự án (và bất biến "tên dự án = tên thư mục workspace")
Trang Projects sửa được Name / Description / đơn vị yêu cầu ngay tại danh sách (modal, quyền
`ProjectsEdit` + `IProjectAccessGuard` nên User thường chỉ sửa project của mình). Ba field kỹ thuật
(Generation Mode, Backend/Frontend Git) **không** ở đây — chúng thuộc `UpdateDeliveryConfigUseCase` ở
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
- **Chỉ phần yêu cầu** — trí nhớ hội thoại BA, các lượt chat, file nguồn và sáu bảng đã chốt; workspace chỉ
  chép `00_Source` rồi dựng lại bộ khung 5 giai đoạn. Bản sao chạy lại delivery từ đầu.

Bốn thứ **không bao giờ** đi theo bản sao, mỗi thứ vì một hậu quả cụ thể:

| Không chép | Vì |
|---|---|
| `AgentModelCallLogs` | nguồn số liệu của Usage + Delivery Quality — chép sang là nhân đôi chi phí đã tiêu trong báo cáo của cả tổ chức |
| `PocShareLinks` | `Token` là link công khai đang sống (unique index); nhân bản nó là mở thêm một cửa vào bản demo mà người tạo link không biết |
| Task ở `Queued`/`Running`/`Retrying` | `AgentTaskWorker` poll `Status == Queued` **toàn cục**, không theo project ⇒ task chép sang bị nhặt ngay và bắn lời gọi LLM thật. Run của chúng chép sang ở trạng thái `Canceled`; riêng `WaitingForHuman` **giữ nguyên** vì đó chính là cổng duyệt người ta nhân bản để thử. Lưu ý `AgentTaskStatus` **không có** giá trị `Canceled` — hủy là việc của `WorkflowRunStatus` |
| `PocAcceptedAtUtc`/`PocAcceptedBy` | chữ ký nghiệm thu của một người thật cho một bản demo cụ thể |

Ngược lại, `ChecklistGapHarvested` được đặt **true** và `PocFeedbackHarvestedCount` đặt bằng số ghi chú
thực sự chép sang: cả hai đều là con trỏ của các đường ghi vào `AgentChecklistItem` **dùng chung cho mọi
dự án**, nên để chúng ở 0/false sẽ khiến cùng một buổi phỏng vấn đẻ ra hai lần cùng một bài học. Cùng lý
do, mọi con trỏ harvest khác (`SummarizedTurnCount`, `UserMemoryHarvestedTurnCount`,
`CoverageHarvestedTurnCount`…) được chép **nguyên giá trị**, không reset về 0.

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
  có tài khoản mở được bản demo và ghim góp ý bằng tên mình. Token luôn có hạn dùng, thu hồi được, và
  chỉ mở đúng ba thứ của MỘT project (trang xem, `poc-demo.html`, danh sách góp ý). Toàn bộ bề mặt
  cho khách gom trong một controller để đọc một file là thấy hết; sandbox CSP của bản demo giữ nguyên
  như đường có đăng nhập.
- Ô "Gửi cho ai" của hộp thoại tạo link là autocomplete lấy gợi ý từ bảng `Associates`
  (`SearchAssociatesQuery` + `Projects/SearchAssociates`) — nhãn link chỉ có ích khi cùng một người
  luôn được ghi cùng một cách. Vẫn cho gõ tự do vì khách ngoài công ty không có trong danh bạ. Danh bạ
  dùng chung cả công ty nhưng cửa vào kẹp theo project + `RequirementsManage`, và chỉ trả tên/email/
  đơn vị/chức danh — không mở thêm một đường tra cứu hồ sơ nhân sự.

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
trình bày, không đổi nghiệp vụ). Khác [`PocFeedbackMemoryService`](requirement-flow.md#các-cơ-chế-trí-nhớ) — vốn bồi
bài học vào checklist phỏng vấn của BA cho **các dự án sau** — bộ này giữ quy ước cho **chính dự án này**.

Fail-open như mọi tầng bộ nhớ: model lỗi/không đọc nổi ⇒ giữ nguyên bộ cũ. Và một kết quả **nghèo hơn**
bộ đang lưu bị từ chối — model được yêu cầu xuất lại toàn bộ bộ đã gộp, nên ít đi nghĩa là nó vừa đánh
rơi quy ước cũ, chứ không phải người dùng đổi ý. Trần 24 quy ước (giữ mới nhất): bộ này đi vào prompt của
mọi vòng dựng nên để phình vô hạn là lấy dần chỗ của chính AI Design Spec. Dự án chưa từng đi đường
"chỉnh bản demo" thì không có file, khối prompt rỗng, prompt POC y như trước.

### Parity Brief ↔ Spec soát ba tầng, không chỉ màn hình
Spec là **đầu vào duy nhất** của bước dựng POC, nên thứ gì rơi rụng ở biên Brief→Spec thì POC thiếu
luôn và mọi cổng phía sau đều mù (chúng chỉ so POC với spec). `SpecBriefParityChecker` vốn chỉ so danh
sách màn hình; nay ba tầng theo thứ tự "mất mát đắt dần": **màn hình** → **quy tắc nghiệp vụ**
(`## Quy tắc cần nhớ` ↔ `§ 10`) → **câu nghiệm thu** (`Hoàn thành khi` ↔ `§ 14`). Rule bị mất là bản
demo bấm được nhưng sai nghiệp vụ — đúng lớp lỗi mà audit POC không thể thấy vì nó chấm theo spec.

Màn hình so bằng `PocSpec.Matches` (dùng chung với audit POC). Rule và AC là **câu**, không phải nhãn
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
Vòng "Yêu cầu chỉnh sửa" **ghi đè thẳng** lên `poc-demo.html`, nên bản người nghiệm thu vừa xem biến
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

---
