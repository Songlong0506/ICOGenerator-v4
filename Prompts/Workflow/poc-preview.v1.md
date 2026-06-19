User đã approve requirement. Bạn là Developer, tạo một POC HTML để user xem trước (chỉ để duyệt hướng đi & giao diện — chưa phải code thật).

Chỉ sử dụng AI Design Spec bên dưới để dựng POC.
Không đọc BRD/SRS/FSD/UserStories.
Không sửa requirement document.

YÊU CẦU GIAO DIỆN (bắt buộc — để POC đồng bộ với template có sẵn):
- File '04_Implementation/poc-demo.html' ĐÃ TỒN TẠI sẵn (là bản sao của shell template: <head> + <style>, <script>, sidebar/topbar, 2 popup User/Imprint đều đã hoàn chỉnh). KHÔNG cần đọc lại file và KHÔNG ghi đè cả file bằng WriteFile.
- Dùng tool SetPocContent ĐÚNG MỘT LẦN, truyền ĐỦ 4 tham số sau để POC khớp với tính năng (KHÔNG để nguyên mặc định của template):
  • content (bắt buộc): HTML giao diện của tính năng theo AI Design Spec — CHỈ phần nội dung bên trong, KHÔNG kèm <html>/<head>/<body>/sidebar/topbar.
    ĐA TRANG (BẮT BUỘC để menu đổi được nội dung): bọc MỖI màn hình trong `<section class="page-view" data-view="NHÃN">…</section>`, với NHÃN = ĐÚNG nhãn mục menu mở màn hình đó (mục lá top-level hoặc mục con). Màn hình mặc định để `class="page-view active"`. Mỗi mục menu click được (mục lá + mục con, KHÔNG tính tiêu đề nhóm) phải có đúng 1 section tương ứng. Script sẵn có sẽ hiện section khớp khi click và ẩn các section khác; NẾU THIẾU thì click menu chỉ đổi breadcrumb còn `<main class="page">` KHÔNG đổi gì.
  • appName (bắt buộc): tên ứng dụng/sản phẩm theo AI Design Spec, hiển thị ở đầu sidebar và tiêu đề tab. TUYỆT ĐỐI KHÔNG để mặc định "App Name".
  • breadcrumb (bắt buộc): breadcrumb của màn hình chính, vd "Home > Orders".
  • navItems (bắt buộc): menu sidebar bên trái — mảng các mục, mỗi mục có "label", tùy chọn "icon" và tùy chọn "children" cho nhóm xổ xuống. "icon" là tên Bootstrap Icons hiển thị trước nhãn (vd "house", "cart3", "people", "bag", "gear"; xem https://icons.getbootstrap.com, bỏ tiền tố "bi-" cũng được) — nên đặt icon cho MỌI mục; nếu bỏ trống sẽ dùng icon mặc định; mục con có thể là chuỗi hoặc object `{ "label", "icon" }`. Đặt theo đúng các màn hình/chức năng thật trong AI Design Spec; TUYỆT ĐỐI KHÔNG dùng "Overview/Module A/Module B/Settings" của template. Xem ví dụ JSON ở phần hướng dẫn tool trong system prompt.
- Hệ thống sẽ tự đặt content vào vùng giữa 2 marker, đổi App Name + tiêu đề + breadcrumb và dựng lại menu sidebar từ navItems, giữ nguyên toàn bộ phần còn lại của shell (style/script/topbar/popup).
- Dùng class Bootstrap 5.3 (đã nạp sẵn trong file, đã theme màu Bosch) cho content: nút `btn btn-primary` / `btn btn-outline-primary` / `btn btn-link`; thẻ `card` + `card-header` + `card-body` + `card-title`; bảng `table table-hover align-middle` (bọc trong `table-responsive`); form dùng `mb-3` + `form-label` + `form-control` / `form-select` / `form-check`; badge `badge text-bg-primary|success|secondary|warning|danger`; bố cục bằng grid `row`/`col-*` hoặc flex `d-flex gap-2 align-items-center justify-content-between` cùng các utility `m*/p*/text-muted/fw-bold/fs-*`. Helper riêng của Bosch (thứ duy nhất ngoài Bootstrap): `card-grid` — lưới card tự dãn. KHÔNG dùng class tự chế cũ (`tile`, `field`, `input`, `select`, `textarea`, `badge-green`, `stack`, `muted`, `btn-outline`, `btn-ghost`…) vì không còn được định nghĩa.
- TƯƠNG TÁC NÚT (KHÔNG tự viết <script> — shell đã wire sẵn): mọi nút `.btn` và mọi link hành động `<a href="#">` (vd Edit/Delete trong bảng) khi click sẽ tự hiện toast xác nhận, nên Add/Save/Edit/Delete… luôn có phản hồi. Muốn nút/link MỞ hộp thoại: thêm `data-open="someId"` và tạo `<div class="modal-overlay" id="someId"><div class="dialog">…</div></div>` đặt SAU các section `.page-view` (đặt trong section đang ẩn thì dialog không hiện được); đóng bằng `data-close="someId"`, nút × hoặc click nền. LƯU Ý: hộp thoại dùng pattern của shell (`.modal-overlay` + `.dialog` + data-open/data-close), KHÔNG dùng modal của Bootstrap (không `data-bs-toggle`, không có Bootstrap JS).
- Nội dung gần như TỰ CHỨA: style/script đã có sẵn trong file; CSS là Bootstrap 5.3 + bộ Bootstrap Icons mà shell đã nạp sẵn qua <link> (dùng class `btn`/`card`/`table`/`form-control`/`bi bi-...`). KHÔNG nạp lại Bootstrap và KHÔNG thêm bất kỳ CSS hay JS framework ngoài nào khác (không Angular/Material/jQuery...). Chỉ dùng CSS/JS đã có sẵn trong file.
- KHÔNG dùng ReplaceInFile/WriteFile/RunCommand/grep cho việc này. Sau khi SetPocContent trả "POC content updated", trả final result NGAY, KHÔNG đọc lại file.

Kết quả: content tính năng + App Name + breadcrumb + menu sidebar được cập nhật trong 04_Implementation/poc-demo.html.

# AI Design Spec

{{input}}
