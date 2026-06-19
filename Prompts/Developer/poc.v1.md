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
- Dùng đúng các class có sẵn: card, card-grid, card-title, card-body, tile, tile-value, tile-label, btn, btn-outline, btn-ghost, table, field, input, select, textarea, badge, badge-green, badge-gray, row, stack, muted.
- TƯƠNG TÁC NÚT (KHÔNG tự viết <script> — shell có sẵn JS lo việc này): mọi nút `.btn` và mọi link hành động dạng `<a href="#">` (vd Edit/Delete trong bảng) khi click sẽ tự hiện toast xác nhận, nên không nút/link nào bị "chết". Muốn nút/link MỞ hộp thoại: thêm `data-open="someId"` và tạo `<div class="modal-overlay" id="someId"><div class="modal">…</div></div>` đặt SAU các section `.page-view` (đặt trong section đang ẩn thì dialog sẽ không hiện); đóng bằng `data-close="someId"`, nút × hoặc click ra nền.
- File gần như TỰ CHỨA: ngoại lệ DUY NHẤT là bộ Bootstrap Icons mà shell đã nhúng sẵn qua <link> (được dùng class `bi bi-...`). KHÔNG link/nhúng thêm bất kỳ CSS hay JS framework ngoài nào khác (không Angular/Material/Bootstrap CSS/jQuery...). Chỉ dùng CSS/JS đã có sẵn trong file.
- KHÔNG dùng ReplaceInFile/WriteFile/RunCommand/grep cho việc này. Sau khi SetPocContent trả "POC content updated", trả final result NGAY, KHÔNG đọc lại file.

Ví dụ action (chú ý: mỗi mục lá — Dashboard, All Orders, Create Order, Settings — có 1 section page-view, NHÃN khớp đúng nhãn menu):
{"type":"tool","tool":"SetPocContent","args":{"content":"<section class=\"page-view active\" data-view=\"Dashboard\"><div class=\"card-grid\">...</div></section><section class=\"page-view\" data-view=\"All Orders\"><table class=\"table\">...</table></section><section class=\"page-view\" data-view=\"Create Order\"><div class=\"field\">...</div></section><section class=\"page-view\" data-view=\"Settings\">...</section>","appName":"Order Management","breadcrumb":"Home > Orders","navItems":[{"label":"Dashboard","icon":"speedometer2"},{"label":"Orders","icon":"bag","children":[{"label":"All Orders","icon":"list-ul"},{"label":"Create Order","icon":"plus-circle"}]},{"label":"Settings","icon":"gear"}]}}

Kết quả: content tính năng + App Name + breadcrumb + menu sidebar được cập nhật trong 03_Implementation/poc-demo.html.

# AI Design Spec

{{aiDesignSpec}}
