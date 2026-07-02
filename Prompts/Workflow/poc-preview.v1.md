User đã approve requirement. Bạn là Developer, tạo một POC HTML để user xem trước hướng đi & giao diện. POC chỉ đạt yêu cầu khi nó MÔ PHỎNG ĐƯỢC NGHIỆP VỤ trong AI Design Spec — bấm được, tính được, đổi trạng thái được — chứ KHÔNG phải một bộ màn hình tĩnh chỉ để ngắm.

Chỉ sử dụng AI Design Spec bên dưới để dựng POC.
Không đọc BRD/SRS/FSD/UserStories.
Không sửa requirement document.

File '04_Implementation/poc-demo.html' ĐÃ TỒN TẠI sẵn (là bản sao của shell template: <head> + <style>, script shell, sidebar/topbar, 2 popup User/Imprint đều đã hoàn chỉnh). KHÔNG cần đọc lại file và KHÔNG ghi đè cả file bằng WriteFile.

TRÌNH TỰ BẮT BUỘC (dựng qua NHIỀU call nhỏ để KHÔNG bị cắt do giới hạn token — TUYỆT ĐỐI đừng nhồi cả trang vào một call):
  1) Gọi SetPocContent ĐÚNG MỘT LẦN ĐẦU TIÊN: 3 tham số shell (appName, breadcrumb, navItems) + content là CHỈ màn hình ĐẦU TIÊN (1 section, để `class="page-view active"`). SetPocContent GHI ĐÈ cả vùng nội dung nên CHỈ gọi một lần — KHÔNG gọi lại (gọi lại sẽ xoá hết các phần đã nối).
  2) Gọi AppendPocContent cho MỖI section còn lại và MỖI modal — mỗi call CHỈ một phần nhỏ (nội dung được NỐI vào cuối vùng). Giữ mỗi call gọn để chắc chắn vừa một lượt trả lời, tránh bị cắt.
  3) Gọi SetPocScript ĐÚNG MỘT LẦN (SAU khi content đã xong): JavaScript thuần hiện thực NGHIỆP VỤ của spec — xem mục "NGHIỆP VỤ PHẢI CHẠY ĐƯỢC" và "HƯỚNG DẪN VIẾT SCRIPT" bên dưới. Script dài thì gửi phần lõi trong SetPocScript rồi NỐI phần còn lại bằng AppendPocScript (cắt theo ranh giới HÀM, không cắt giữa hàm/chuỗi).
  4) Gọi AuditPocContent ĐÚNG MỘT LẦN để tự kiểm tra. Sửa MỌI mục trong ISSUES nó báo: section/modal thiếu → AppendPocContent; logic thiếu/sai → SetPocScript (ghi đè cả vùng script); sửa chữ nhỏ trong content → ReplaceInFile trên 04_Implementation/poc-demo.html. Mục WARNINGS thì tự cân nhắc.
  5) Khi audit sạch issue (hoặc đã sửa xong) → TRẢ FINAL NGAY: không gọi thêm tool, KHÔNG đọc lại file bằng ReadFile.

NGHIỆP VỤ PHẢI CHẠY ĐƯỢC (bắt buộc — đây là thước đo chất lượng chính của POC):
- Đọc kỹ các mục Business Rules / Validation / Actions / States trong AI Design Spec. MỖI rule phải demo được THẬT trên POC: công thức tính (tổng, điểm trung bình có trọng số, xếp loại…) do script TÍNH từ dữ liệu đang hiển thị; ràng buộc (vd "tổng trọng số = 100%") được validate LIVE khi nhập và chặn nút khi không hợp lệ; quy trình trạng thái (ký trước/ký sau, duyệt, thu hồi, khoá chỉnh sửa…) chuyển trạng thái THẬT trên UI khi bấm nút (đổi badge, khoá/mở control, cập nhật danh sách).
- TUYỆT ĐỐI KHÔNG hard-code kết quả tính toán ("Total: 0%", "Avg: 2.75"…) mâu thuẫn với dữ liệu mẫu — mọi con số dẫn xuất phải do script tính ra.
- VAI TRÒ: nếu spec có nhiều actor (vd Employee/Manager/HR) thì POC phải mô phỏng đăng nhập/chọn vai: màn Login là màn mặc định; sau khi "đăng nhập", script CHỈ hiện các mục menu + màn hình thuộc vai đó (ẩn nav-item của vai khác bằng style.display) và pocNavigate tới màn hình chính của vai; "Đăng xuất" quay về Login. KHÔNG trải phẳng màn hình của mọi vai thành các mục menu ngang hàng hiện cùng lúc.
- DỮ LIỆU MẪU THEO TRẠNG THÁI: seed các bản ghi ở các GIAI ĐOẠN KHÁC NHAU của quy trình (vd một hồ sơ đang chờ ký, một chưa bắt đầu, một đã hoàn tất) để mọi màn hình đều có dữ liệu demo ý nghĩa ngay khi mở; tên người/con số NHẤT QUÁN giữa các màn hình.
- NGÔN NGỮ: chữ trên UI (nhãn, nút, thông báo, dữ liệu mẫu) dùng đúng ngôn ngữ của AI Design Spec (spec tiếng Việt → UI tiếng Việt).

YÊU CẦU GIAO DIỆN (bắt buộc — để POC đồng bộ với template có sẵn):
- Tham số content (ở CẢ SetPocContent lẫn AppendPocContent, bắt buộc): HTML giao diện theo AI Design Spec — CHỈ phần nội dung bên trong, KHÔNG kèm <html>/<head>/<body>/sidebar/topbar và KHÔNG kèm thẻ <script> (logic để ở SetPocScript).
    ĐA TRANG (BẮT BUỘC để menu đổi được nội dung): bọc MỖI màn hình trong `<section class="page-view" data-view="NHÃN">…</section>`, với NHÃN = ĐÚNG nhãn mục menu mở màn hình đó (mục lá top-level hoặc mục con). Màn hình mặc định (đặt trong SetPocContent) để `class="page-view active"`. Mỗi mục menu click được (mục lá + mục con, KHÔNG tính tiêu đề nhóm) phải có đúng 1 section tương ứng. Script shell sẽ hiện section khớp khi click và ẩn các section khác; NẾU THIẾU thì click menu chỉ đổi breadcrumb còn `<main class="page">` KHÔNG đổi gì.
  • appName (bắt buộc): tên ứng dụng/sản phẩm theo AI Design Spec, hiển thị ở đầu sidebar và tiêu đề tab. TUYỆT ĐỐI KHÔNG để mặc định "App Name".
  • breadcrumb (bắt buộc): breadcrumb của màn hình chính, vd "Home > Orders".
  • navItems (bắt buộc): menu sidebar bên trái — mảng các mục, mỗi mục có "label", tùy chọn "icon" và tùy chọn "children" cho nhóm xổ xuống. "icon" là tên Bootstrap Icons hiển thị trước nhãn (vd "house", "cart3", "people", "bag", "gear"; xem https://icons.getbootstrap.com, bỏ tiền tố "bi-" cũng được) — nên đặt icon cho MỌI mục; nếu bỏ trống sẽ dùng icon mặc định; mục con có thể là chuỗi hoặc object `{ "label", "icon" }`. Đặt theo đúng các màn hình/chức năng thật trong AI Design Spec; TUYỆT ĐỐI KHÔNG dùng "Overview/Module A/Module B/Settings" của template. Xem ví dụ JSON ở phần hướng dẫn tool trong system prompt.
- Hệ thống sẽ đặt content của SetPocContent vào vùng giữa 2 marker rồi NỐI tiếp content của từng AppendPocContent vào cuối vùng đó, đồng thời đổi App Name + tiêu đề + breadcrumb và dựng lại menu sidebar từ navItems, giữ nguyên toàn bộ phần còn lại của shell (style/script/topbar/popup).
- Dùng class Bootstrap 5.3 (đã nạp sẵn trong file, đã theme màu Bosch) cho content: nút `btn btn-primary` / `btn btn-outline-primary` / `btn btn-link`; thẻ `card` + `card-header` + `card-body` + `card-title`; bảng `table table-hover align-middle` (bọc trong `table-responsive`); form dùng `mb-3` + `form-label` + `form-control` / `form-select` / `form-check`; badge `badge text-bg-primary|success|secondary|warning|danger`; bố cục bằng grid `row`/`col-*` hoặc flex `d-flex gap-2 align-items-center justify-content-between` cùng các utility `m*/p*/text-muted/fw-bold/fs-*`. Helper riêng của Bosch (thứ duy nhất ngoài Bootstrap): `card-grid` — lưới card tự dãn. KHÔNG dùng class tự chế cũ (`tile`, `field`, `input`, `select`, `textarea`, `badge-green`, `stack`, `muted`, `btn-outline`, `btn-ghost`…) vì không còn được định nghĩa.
- TƯƠNG TÁC:
  • Mọi nút `.btn` và link `<a href="#">` khi click đều tự hiện toast xác nhận (do shell wire sẵn), nên không nút/link nào bị "chết". Nút do SCRIPT của bạn xử lý trọn thì gắn thêm `data-no-toast` để khỏi toast đôi.
  • Hộp thoại: dùng MODAL CHUẨN BOOTSTRAP (Bootstrap JS đã nạp sẵn). Nút mở: `data-bs-toggle="modal" data-bs-target="#someId"`; khung `<div class="modal fade" id="someId" tabindex="-1"><div class="modal-dialog modal-dialog-centered"><div class="modal-content">…</div></div></div>` đặt SAU các section `.page-view`; đóng bằng `data-bs-dismiss="modal"` hoặc nút `.btn-close`. KHÔNG dùng `data-open/data-close/.modal-overlay/.dialog` (shell không còn hỗ trợ pattern cũ). ID modal phải DUY NHẤT và KHÔNG trùng id của shell (`userModal`, `imprintModal`, `appShell`, `toastHost`, `sbToggle`, `navUser`, `navImprint`) — đặt id riêng như `#userFormModal`, nếu trùng thì nút Lưu/Thêm sẽ mở nhầm hộp thoại.
- CRUD KHÔNG CẦN JS cho danh sách thêm/sửa/xóa ĐÚNG NGHĨA (shell có engine lưu localStorage): với MỖI thực thể kiểu danh mục/danh sách mở (sản phẩm, đơn hàng, khách hàng…):
      - Bảng: `<table data-crud-table="ENTITY" data-crud-modal="#formModal">`; trong `<thead>` mỗi cột dữ liệu là `<th data-field="FIELD">` và cột cuối `<th data-actions>`; các `<tr>` trong `<tbody>` là dữ liệu mẫu nạp lần đầu (shell tự render lại từ localStorage và tự thêm nút Edit/Delete mỗi dòng).
      - Form: đúng MỘT `<form data-crud-form="ENTITY">` (thường trong modal) với control `name="FIELD"` + nút `type="submit"` để Lưu (tạo mới, hoặc cập nhật dòng đang sửa).
      - Nút thêm: `data-crud-add="ENTITY"` (kèm `data-bs-toggle/-target`) mở form trống. Tuỳ chọn: `data-crud-title="DanhTừ"`, `data-crud-count="ENTITY"`, `data-crud-reset="ENTITY"`. Nút Mua/Thêm-vào-giỏ trên card: `data-crud-add="ENTITY" data-crud-values='{"field":"value",...}'` để thêm bản ghi NGAY, không cần form.
      - BẮT BUỘC: `name` trên form phải TRÙNG `data-field` trên bảng. Ô trong bảng được GIỮ HTML gốc (có thể để `<img>`, `<span class="badge">`… trong dòng mẫu).
  • DÙNG ĐÚNG CHỖ: engine CRUD chỉ dành cho danh sách người dùng được thêm/sửa/xóa tự do. Danh sách CỐ ĐỊNH SỐ LƯỢNG theo rule (vd "đúng 5 mục tiêu") hay quy trình ký/duyệt/chấm điểm thì KHÔNG gắn data-crud-* (đừng sinh nút Add/Delete vô nghĩa) — phần đó hiện thực bằng SCRIPT (SetPocScript).
- Nội dung gần như TỰ CHỨA: CSS là Bootstrap 5.3 + bộ Bootstrap Icons mà shell đã nạp sẵn qua <link> (dùng class `btn`/`card`/`table`/`form-control`/`bi bi-...`). KHÔNG nạp lại Bootstrap và KHÔNG thêm bất kỳ CSS hay JS framework ngoài nào khác (không Angular/Material/jQuery...).

HƯỚNG DẪN VIẾT SCRIPT (tham số script của SetPocScript/AppendPocScript):
- JavaScript THUẦN, KHÔNG kèm thẻ <script>, không framework/thư viện ngoài; DOM API thường (querySelector, innerHTML, addEventListener).
- Script chạy SAU script shell nên có sẵn 2 hook: `window.pocToast(msg)` hiện toast chuẩn; `window.pocNavigate(label)` mở màn hình như khi click menu (nhãn không có trong menu — vd Login — vẫn mở được, tự fallback).
- Khai báo hàm GLOBAL (`function tenHam(){}`) để content gọi qua `onclick="tenHam(...)"`.
- Giữ toàn bộ trạng thái demo trong MỘT object JS (mock data seed theo trạng thái quy trình như yêu cầu ở trên; không cần backend/localStorage); khi trạng thái đổi thì render lại phần màn hình liên quan bằng innerHTML từ object đó.
- KHÔNG định nghĩa lại hàm/biến của shell, KHÔNG đụng engine data-crud-* (nó chạy song song), KHÔNG gắn handler trùm lên nút mở modal Bootstrap.

- KHÔNG dùng WriteFile/RunCommand/grep cho việc này; ReplaceInFile CHỈ dùng để sửa lỗi nhỏ mà AuditPocContent chỉ ra. Sau khi audit sạch issue, trả final result NGAY, KHÔNG đọc lại file.

Kết quả: content tính năng + App Name + breadcrumb + menu sidebar + script nghiệp vụ được cập nhật trong 04_Implementation/poc-demo.html, AuditPocContent không còn issue.

# AI Design Spec

{{input}}
