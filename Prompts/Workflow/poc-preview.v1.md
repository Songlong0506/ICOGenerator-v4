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
- TƯƠNG TÁC & CRUD THẬT (KHÔNG tự viết <script> — shell đã wire sẵn toàn bộ):
  • Mọi nút `.btn` và link `<a href="#">` (vd Edit/Delete trong bảng) khi click đều tự hiện toast xác nhận, nên không nút/link nào bị "chết".
  • Hộp thoại: dùng MODAL CHUẨN BOOTSTRAP (Bootstrap JS đã nạp sẵn). Nút mở: `data-bs-toggle="modal" data-bs-target="#someId"`; khung `<div class="modal fade" id="someId" tabindex="-1"><div class="modal-dialog modal-dialog-centered"><div class="modal-content">…</div></div></div>` đặt SAU các section `.page-view`; đóng bằng `data-bs-dismiss="modal"` hoặc nút `.btn-close`. KHÔNG dùng `data-open/data-close/.modal-overlay/.dialog` (shell không còn hỗ trợ pattern cũ).
  • CRUD CÓ THẬT + LƯU localStorage (vẫn KHÔNG cần JS): chỉ gắn thuộc tính `data-*`, shell tự thêm/sửa/xoá và lưu vào localStorage (giữ qua reload). Với MỖI thực thể (entity):
      - Bảng: `<table data-crud-table="ENTITY" data-crud-modal="#formModal">`; trong `<thead>` mỗi cột dữ liệu là `<th data-field="FIELD">` và cột cuối `<th data-actions>`; các `<tr>` trong `<tbody>` là dữ liệu mẫu nạp lần đầu (shell tự render lại từ localStorage và tự thêm nút Edit/Delete mỗi dòng).
      - Form: đúng MỘT `<form data-crud-form="ENTITY">` (thường trong modal) với control `name="FIELD"` + nút `type="submit"` để Lưu (tạo mới, hoặc cập nhật dòng đang sửa).
      - Nút thêm: `data-crud-add="ENTITY"` (kèm `data-bs-toggle/-target`) mở form trống. Tuỳ chọn: `data-crud-title="DanhTừ"`, `data-crud-count="ENTITY"`, `data-crud-reset="ENTITY"`.
      - BẮT BUỘC: `name` trên form phải TRÙNG `data-field` trên bảng. Hãy dùng pattern này cho MỌI màn hình danh sách/quản lý để POC chạy CRUD thật; không gắn `data-crud-*` thì trang vẫn chạy như cũ (bảng tĩnh + toast).
- Nội dung gần như TỰ CHỨA: style/script đã có sẵn trong file; CSS là Bootstrap 5.3 + bộ Bootstrap Icons mà shell đã nạp sẵn qua <link> (dùng class `btn`/`card`/`table`/`form-control`/`bi bi-...`). KHÔNG nạp lại Bootstrap và KHÔNG thêm bất kỳ CSS hay JS framework ngoài nào khác (không Angular/Material/jQuery...). Chỉ dùng CSS/JS đã có sẵn trong file.
- KHÔNG dùng ReplaceInFile/WriteFile/RunCommand/grep cho việc này. Sau khi SetPocContent trả "POC content updated", trả final result NGAY, KHÔNG đọc lại file.

Kết quả: content tính năng + App Name + breadcrumb + menu sidebar được cập nhật trong 04_Implementation/poc-demo.html.

# AI Design Spec

{{input}}
