User đã approve requirement. Bạn là Developer, tạo một POC HTML để user xem trước (chỉ để duyệt hướng đi & giao diện — chưa phải code thật).

Chỉ sử dụng AI Design Spec bên dưới để dựng POC.
Không đọc BRD/SRS/FSD/UserStories.
Không sửa requirement document.

YÊU CẦU GIAO DIỆN (bắt buộc — để POC đồng bộ với template có sẵn):
- File '03_Implementation/poc-demo.html' ĐÃ TỒN TẠI sẵn (là bản sao của shell template: <head> + <style>, <script>, sidebar/topbar, 2 popup User/Imprint đều đã hoàn chỉnh). KHÔNG cần đọc lại file và KHÔNG ghi đè cả file bằng WriteFile.
- Dùng tool SetPocContent ĐÚNG MỘT LẦN, truyền ĐỦ 4 tham số sau để POC khớp với tính năng (KHÔNG để nguyên mặc định của template):
  • content (bắt buộc): HTML giao diện của tính năng theo AI Design Spec — CHỈ phần nội dung bên trong, KHÔNG kèm <html>/<head>/<body>/sidebar/topbar.
    ĐA TRANG (BẮT BUỘC để menu đổi được nội dung): bọc MỖI màn hình trong `<section class="page-view" data-view="NHÃN">…</section>`, với NHÃN = ĐÚNG nhãn mục menu mở màn hình đó (mục lá top-level hoặc mục con). Màn hình mặc định để `class="page-view active"`. Mỗi mục menu click được (mục lá + mục con, KHÔNG tính tiêu đề nhóm) phải có đúng 1 section tương ứng. Script sẵn có sẽ hiện section khớp khi click và ẩn các section khác; NẾU THIẾU thì click menu chỉ đổi breadcrumb còn `<main class="page">` KHÔNG đổi gì.
  • appName (bắt buộc): tên ứng dụng/sản phẩm theo AI Design Spec, hiển thị ở đầu sidebar và tiêu đề tab. TUYỆT ĐỐI KHÔNG để mặc định "App Name".
  • breadcrumb (bắt buộc): breadcrumb của màn hình chính, vd "Home > Orders".
  • navItems (bắt buộc): menu sidebar bên trái — mảng các mục, mỗi mục có "label" và tùy chọn "children" (mảng tên mục con) cho nhóm xổ xuống. Đặt theo đúng các màn hình/chức năng thật trong AI Design Spec; TUYỆT ĐỐI KHÔNG dùng "Overview/Module A/Module B/Settings" của template. Xem ví dụ JSON ở phần hướng dẫn tool trong system prompt.
- Hệ thống sẽ tự đặt content vào vùng giữa 2 marker, đổi App Name + tiêu đề + breadcrumb và dựng lại menu sidebar từ navItems, giữ nguyên toàn bộ phần còn lại của shell (style/script/topbar/popup).
- Dùng đúng các class có sẵn cho content: card, card-grid, card-title, card-body, tile, tile-value, tile-label, btn, btn-outline, btn-ghost, table, field, input, select, textarea, badge, badge-green, badge-gray, row, stack, muted.
- TƯƠNG TÁC NÚT (KHÔNG tự viết <script> — shell đã wire sẵn): mọi nút `.btn` và mọi link hành động `<a href="#">` (vd Edit/Delete trong bảng) khi click sẽ tự hiện toast xác nhận, nên Add/Save/Edit/Delete… luôn có phản hồi. Muốn nút/link MỞ hộp thoại: thêm `data-open="someId"` và tạo `<div class="modal-overlay" id="someId"><div class="modal">…</div></div>` đặt SAU các section `.page-view` (đặt trong section đang ẩn thì dialog không hiện được); đóng bằng `data-close="someId"`, nút × hoặc click nền.
- Nội dung phải TỰ CHỨA: KHÔNG link/nhúng CSS hay JS framework bên ngoài (không Angular/Material/Bootstrap...). Chỉ dùng CSS/JS đã có sẵn trong file.
- KHÔNG dùng ReplaceInFile/WriteFile/RunCommand/grep cho việc này. Sau khi SetPocContent trả "POC content updated", trả final result NGAY, KHÔNG đọc lại file.

Kết quả: content tính năng + App Name + breadcrumb + menu sidebar được cập nhật trong 03_Implementation/poc-demo.html.

# AI Design Spec

{{input}}
