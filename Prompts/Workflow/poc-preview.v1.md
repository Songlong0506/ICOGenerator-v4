User đã approve requirement. Bạn là Developer, tạo một POC HTML để user xem trước (chỉ để duyệt hướng đi & giao diện — chưa phải code thật).

Chỉ sử dụng AI Design Spec bên dưới để dựng POC.
Không đọc BRD/SRS/FSD/UserStories.
Không sửa requirement document.

YÊU CẦU GIAO DIỆN (bắt buộc — để POC đồng bộ với template có sẵn):
- File '04_Implementation/poc-demo.html' ĐÃ TỒN TẠI sẵn (là bản sao của shell template: <head> + <style>, <script>, sidebar/topbar, 2 popup User/Imprint đều đã hoàn chỉnh). KHÔNG cần đọc lại file và KHÔNG ghi đè cả file bằng WriteFile.
- Dựng POC qua NHIỀU call nhỏ để KHÔNG bị cắt do giới hạn token (TUYỆT ĐỐI đừng nhồi cả trang vào một call):
  1) Gọi SetPocContent ĐÚNG MỘT LẦN ĐẦU TIÊN: 3 tham số shell (appName, breadcrumb, navItems) + content là CHỈ màn hình ĐẦU TIÊN (1 section, để `class="page-view active"`). SetPocContent GHI ĐÈ cả vùng nội dung nên CHỈ gọi một lần — KHÔNG gọi lại (gọi lại sẽ xoá hết các phần đã nối).
  2) Rồi gọi AppendPocContent cho MỖI section còn lại và MỖI modal — mỗi call CHỈ một phần nhỏ (nội dung được NỐI vào cuối vùng). Giữ mỗi call gọn để chắc chắn vừa một lượt trả lời, tránh bị cắt.
  3) Sau khi đã thêm xong TẤT CẢ section + modal → TRẢ FINAL NGAY: không gọi thêm tool, KHÔNG đọc lại file.
  Tham số content (ở CẢ SetPocContent lẫn AppendPocContent, bắt buộc): HTML giao diện theo AI Design Spec — CHỈ phần nội dung bên trong, KHÔNG kèm <html>/<head>/<body>/sidebar/topbar.
    ĐA TRANG (BẮT BUỘC để menu đổi được nội dung): bọc MỖI màn hình trong `<section class="page-view" data-view="NHÃN">…</section>`, với NHÃN = ĐÚNG nhãn mục menu mở màn hình đó (mục lá top-level hoặc mục con). Màn hình mặc định (đặt trong SetPocContent) để `class="page-view active"`. Mỗi mục menu click được (mục lá + mục con, KHÔNG tính tiêu đề nhóm) phải có đúng 1 section tương ứng. Script sẵn có sẽ hiện section khớp khi click và ẩn các section khác; NẾU THIẾU thì click menu chỉ đổi breadcrumb còn `<main class="page">` KHÔNG đổi gì.
  • appName (bắt buộc): tên ứng dụng/sản phẩm theo AI Design Spec, hiển thị ở đầu sidebar và tiêu đề tab. TUYỆT ĐỐI KHÔNG để mặc định "App Name".
  • breadcrumb (bắt buộc): breadcrumb của màn hình chính, vd "Home > Orders".
  • navItems (bắt buộc): menu sidebar bên trái — mảng các mục, mỗi mục có "label", tùy chọn "icon" và tùy chọn "children" cho nhóm xổ xuống. "icon" là tên Bootstrap Icons hiển thị trước nhãn (vd "house", "cart3", "people", "bag", "gear"; xem https://icons.getbootstrap.com, bỏ tiền tố "bi-" cũng được) — nên đặt icon cho MỌI mục; nếu bỏ trống sẽ dùng icon mặc định; mục con có thể là chuỗi hoặc object `{ "label", "icon" }`. Đặt theo đúng các màn hình/chức năng thật trong AI Design Spec; TUYỆT ĐỐI KHÔNG dùng "Overview/Module A/Module B/Settings" của template. Xem ví dụ JSON ở phần hướng dẫn tool trong system prompt.
- Hệ thống sẽ đặt content của SetPocContent vào vùng giữa 2 marker rồi NỐI tiếp content của từng AppendPocContent vào cuối vùng đó, đồng thời đổi App Name + tiêu đề + breadcrumb và dựng lại menu sidebar từ navItems, giữ nguyên toàn bộ phần còn lại của shell (style/script/topbar/popup).
- Dùng class Bootstrap 5.3 (đã nạp sẵn trong file, đã theme màu Bosch) cho content: nút `btn btn-primary` / `btn btn-outline-primary` / `btn btn-link`; thẻ `card` + `card-header` + `card-body` + `card-title`; bảng `table table-hover align-middle` (bọc trong `table-responsive`); form dùng `mb-3` + `form-label` + `form-control` / `form-select` / `form-check`; badge `badge text-bg-primary|success|secondary|warning|danger`; bố cục bằng grid `row`/`col-*` hoặc flex `d-flex gap-2 align-items-center justify-content-between` cùng các utility `m*/p*/text-muted/fw-bold/fs-*`. Helper riêng của Bosch (thứ duy nhất ngoài Bootstrap): `card-grid` — lưới card tự dãn. KHÔNG dùng class tự chế cũ (`tile`, `field`, `input`, `select`, `textarea`, `badge-green`, `stack`, `muted`, `btn-outline`, `btn-ghost`…) vì không còn được định nghĩa.
- TƯƠNG TÁC & CRUD THẬT (KHÔNG tự viết <script> — shell đã wire sẵn toàn bộ):
  • Mọi nút `.btn` và link `<a href="#">` (vd Edit/Delete trong bảng) khi click đều tự hiện toast xác nhận, nên không nút/link nào bị "chết".
  • Hộp thoại: dùng MODAL CHUẨN BOOTSTRAP (Bootstrap JS đã nạp sẵn). Nút mở: `data-bs-toggle="modal" data-bs-target="#someId"`; khung `<div class="modal fade" id="someId" tabindex="-1"><div class="modal-dialog modal-dialog-centered"><div class="modal-content">…</div></div></div>` đặt SAU các section `.page-view`; đóng bằng `data-bs-dismiss="modal"` hoặc nút `.btn-close`. KHÔNG dùng `data-open/data-close/.modal-overlay/.dialog` (shell không còn hỗ trợ pattern cũ). ID modal phải DUY NHẤT và KHÔNG trùng id của shell (`userModal`, `imprintModal`, `appShell`, `toastHost`, `sbToggle`, `navUser`, `navImprint`) — đặt id riêng như `#userFormModal`, nếu trùng thì nút Lưu/Thêm sẽ mở nhầm hộp thoại.
  • CRUD CÓ THẬT + LƯU localStorage (vẫn KHÔNG cần JS): chỉ gắn thuộc tính `data-*`, shell tự thêm/sửa/xoá và lưu vào localStorage (giữ qua reload). Với MỖI thực thể (entity):
      - Bảng: `<table data-crud-table="ENTITY" data-crud-modal="#formModal">`; trong `<thead>` mỗi cột dữ liệu là `<th data-field="FIELD">` và cột cuối `<th data-actions>`; các `<tr>` trong `<tbody>` là dữ liệu mẫu nạp lần đầu (shell tự render lại từ localStorage và tự thêm nút Edit/Delete mỗi dòng).
      - Form: đúng MỘT `<form data-crud-form="ENTITY">` (thường trong modal) với control `name="FIELD"` + nút `type="submit"` để Lưu (tạo mới, hoặc cập nhật dòng đang sửa).
      - Nút thêm: `data-crud-add="ENTITY"` (kèm `data-bs-toggle/-target`) mở form trống. Tuỳ chọn: `data-crud-title="DanhTừ"`, `data-crud-count="ENTITY"`, `data-crud-reset="ENTITY"`.
      - BẮT BUỘC: `name` trên form phải TRÙNG `data-field` trên bảng. Ô trong bảng được GIỮ HTML gốc (có thể để `<img>`, `<span class="badge">`… trong dòng mẫu, engine không làm mất). Nút Mua/Thêm-vào-giỏ trên card: `data-crud-add="ENTITY" data-crud-values='{"field":"value",...}'` để thêm bản ghi NGAY, không cần form.
      - QUAN TRỌNG — hiện thực CRUD THẬT cho TẤT CẢ, ĐỪNG để nút chính chỉ hiện toast: MỌI bảng danh sách → `data-crud-table` (+ nút `data-crud-add`); MỌI form Tạo/Đăng/Sửa → `data-crud-form` (kể cả form inline ngoài modal); dùng CÙNG tên entity giữa form tạo và bảng để dữ liệu mới hiện ra ngay. Mọi `<form>` khác (không CRUD) khi submit sẽ KHÔNG reload trang. Không gắn `data-crud-*` thì trang vẫn chạy như cũ (bảng tĩnh + toast), nhưng hãy ƯU TIÊN CRUD thật.
- Nội dung gần như TỰ CHỨA: style/script đã có sẵn trong file; CSS là Bootstrap 5.3 + bộ Bootstrap Icons mà shell đã nạp sẵn qua <link> (dùng class `btn`/`card`/`table`/`form-control`/`bi bi-...`). KHÔNG nạp lại Bootstrap và KHÔNG thêm bất kỳ CSS hay JS framework ngoài nào khác (không Angular/Material/jQuery...). Chỉ dùng CSS/JS đã có sẵn trong file.
- KHÔNG dùng ReplaceInFile/WriteFile/RunCommand/grep cho việc này. Sau khi đã hoàn tất SetPocContent + tất cả AppendPocContent (mọi section & modal đã thêm), trả final result NGAY, KHÔNG đọc lại file.

Kết quả: content tính năng + App Name + breadcrumb + menu sidebar được cập nhật trong 04_Implementation/poc-demo.html.

# AI Design Spec

{{input}}
