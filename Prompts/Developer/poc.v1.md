User đã approve requirement.

Chỉ sử dụng AI Design Spec bên dưới để generate code.
Không đọc BRD/SRS/FSD/UserStories.
Không sửa requirement document.

YÊU CẦU GIAO DIỆN (bắt buộc — để POC đồng bộ với template có sẵn):
- File '03_Implementation/poc-demo.html' ĐÃ TỒN TẠI sẵn (là bản sao của shell template: <head> + <style>, <script>, sidebar/topbar, 2 popup User/Imprint đều đã hoàn chỉnh). KHÔNG đọc lại toàn bộ file và KHÔNG ghi đè cả file.
- Dùng tool SetPocContent ĐÚNG MỘT LẦN với đủ 4 tham số (KHÔNG để nguyên mặc định của template):
  - content (bắt buộc): UI của tính năng theo AI Design Spec — CHỈ phần nội dung bên trong, KHÔNG kèm <html>/<head>/<body>/sidebar/topbar.
    ĐA TRANG (BẮT BUỘC để menu đổi được nội dung): bọc MỖI màn hình trong `<section class="page-view" data-view="NHÃN">…</section>`, với NHÃN = ĐÚNG nhãn mục menu mở màn hình đó (mục lá top-level hoặc mục con trong children). Màn hình mặc định để `class="page-view active"`. Mỗi mục menu click được (mục lá + mục con, KHÔNG tính tiêu đề nhóm) phải có đúng 1 section tương ứng. Script sẵn có sẽ hiện section khớp khi click menu và ẩn các section khác; NẾU THIẾU các section này thì click menu chỉ đổi breadcrumb còn `<main class="page">` KHÔNG đổi gì.
  - appName (bắt buộc): tên ứng dụng/sản phẩm — TUYỆT ĐỐI KHÔNG để "App Name".
  - breadcrumb (bắt buộc): breadcrumb màn hình chính, vd "Home > Orders".
  - navItems (bắt buộc): menu sidebar bên trái — mảng các mục `{ "label": "...", "icon": "...", "children": [...] }`. "icon" (tùy chọn) là tên Bootstrap Icons hiển thị trước nhãn (vd "house", "cart3", "people", "bag", "gear"; xem https://icons.getbootstrap.com, bỏ tiền tố "bi-" cũng được) — nên đặt icon cho MỌI mục; nếu bỏ trống sẽ dùng icon mặc định. "children" là tùy chọn cho nhóm xổ xuống, mỗi mục con là chuỗi hoặc object `{ "label", "icon" }`. Đặt theo màn hình thật; KHÔNG dùng "Overview/Module A/Module B/Settings".
  Hệ thống tự đặt content vào vùng giữa 2 marker, đổi App Name + tiêu đề + breadcrumb và dựng lại menu sidebar từ navItems; phần còn lại của shell (.supergraphic, .sidebar, .topbar, <head>/<style>, <script>, 2 popup User/Imprint) giữ nguyên.
- Dùng class Bootstrap 5.3 (đã nạp sẵn trong file, đã theme màu Bosch) cho content: nút `btn btn-primary` / `btn btn-outline-primary` / `btn btn-link`; thẻ `card` + `card-header` + `card-body` + `card-title`; bảng `table table-hover align-middle` (bọc trong `table-responsive`); form dùng `mb-3` + `form-label` + `form-control` / `form-select` / `form-check`; badge `badge text-bg-primary|success|secondary|warning|danger`; bố cục bằng grid `row`/`col-*` hoặc flex `d-flex gap-2 align-items-center justify-content-between` cùng các utility `m*/p*/text-muted/fw-bold/fs-*`. Helper riêng của Bosch (thứ duy nhất ngoài Bootstrap): `card-grid` — lưới card tự dãn. KHÔNG dùng class tự chế cũ (`tile`, `field`, `input`, `select`, `textarea`, `badge-green`, `stack`, `muted`, `btn-outline`, `btn-ghost`…) vì không còn được định nghĩa.
- TƯƠNG TÁC NÚT (KHÔNG tự viết <script> — shell có sẵn JS lo việc này): mọi nút `.btn` và mọi link hành động dạng `<a href="#">` (vd Edit/Delete trong bảng) khi click sẽ tự hiện toast xác nhận, nên không nút/link nào bị "chết". Muốn nút/link MỞ hộp thoại: thêm `data-open="someId"` và tạo `<div class="modal-overlay" id="someId"><div class="dialog">…</div></div>` đặt SAU các section `.page-view` (đặt trong section đang ẩn thì dialog sẽ không hiện); đóng bằng `data-close="someId"`, nút × hoặc click ra nền. LƯU Ý: hộp thoại dùng pattern của shell (`.modal-overlay` + `.dialog` + data-open/data-close), KHÔNG dùng modal của Bootstrap (không `data-bs-toggle`, không có Bootstrap JS).
- File gần như TỰ CHỨA: style/script đã có sẵn trong file; CSS là Bootstrap 5.3 + bộ Bootstrap Icons mà shell đã nạp sẵn qua <link> (dùng class `btn`/`card`/`table`/`form-control`/`bi bi-...`). KHÔNG nạp lại Bootstrap và KHÔNG thêm bất kỳ CSS/JS framework ngoài nào khác (không Angular/Material/jQuery...). Chỉ dùng CSS/JS đã có sẵn trong file.
- KHÔNG dùng ReplaceInFile/WriteFile/RunCommand/grep cho việc này. Sau khi SetPocContent trả "POC content updated", trả final result NGAY, KHÔNG đọc lại file.

Ví dụ action (chú ý: mỗi mục lá — Dashboard, All Orders, Create Order, Settings — có 1 section page-view, NHÃN khớp đúng nhãn menu):
{"type":"tool","tool":"SetPocContent","args":{"content":"<section class=\"page-view active\" data-view=\"Dashboard\"><div class=\"card-grid\">...</div></section><section class=\"page-view\" data-view=\"All Orders\"><table class=\"table\">...</table></section><section class=\"page-view\" data-view=\"Create Order\"><div class=\"field\">...</div></section><section class=\"page-view\" data-view=\"Settings\">...</section>","appName":"Order Management","breadcrumb":"Home > Orders","navItems":[{"label":"Dashboard","icon":"speedometer2"},{"label":"Orders","icon":"bag","children":[{"label":"All Orders","icon":"list-ul"},{"label":"Create Order","icon":"plus-circle"}]},{"label":"Settings","icon":"gear"}]}}

Kết quả: content tính năng + App Name + breadcrumb + menu sidebar được cập nhật trong 03_Implementation/poc-demo.html.

# AI Design Spec

{{aiDesignSpec}}
