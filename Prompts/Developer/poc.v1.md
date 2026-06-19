User đã approve requirement.

Chỉ sử dụng AI Design Spec bên dưới để generate code.
Không đọc BRD/SRS/FSD/UserStories.
Không sửa requirement document.

YÊU CẦU GIAO DIỆN (bắt buộc — để POC đồng bộ với template có sẵn):
- File '03_Implementation/poc-demo.html' ĐÃ TỒN TẠI sẵn (là bản sao của shell template: <head> + <style>, <script>, sidebar/topbar, 2 popup User/Imprint đều đã hoàn chỉnh). KHÔNG đọc lại toàn bộ file và KHÔNG ghi đè cả file.
- Dùng tool SetPocContent ĐÚNG MỘT LẦN với đủ 4 tham số (KHÔNG để nguyên mặc định của template):
  - content (bắt buộc): UI của tính năng theo AI Design Spec — CHỈ phần nội dung bên trong, KHÔNG kèm <html>/<head>/<body>/sidebar/topbar.
  - appName (bắt buộc): tên ứng dụng/sản phẩm — TUYỆT ĐỐI KHÔNG để "App Name".
  - breadcrumb (bắt buộc): breadcrumb màn hình chính, vd "Home > Orders".
  - navItems (bắt buộc): menu sidebar bên trái — mảng các mục `{ "label": "...", "children": ["...", "..."] }`, "children" là tùy chọn cho nhóm xổ xuống. Đặt theo màn hình thật; KHÔNG dùng "Overview/Module A/Module B/Settings".
  Hệ thống tự đặt content vào vùng giữa 2 marker, đổi App Name + tiêu đề + breadcrumb và dựng lại menu sidebar từ navItems; phần còn lại của shell (.supergraphic, .sidebar, .topbar, <head>/<style>, <script>, 2 popup User/Imprint) giữ nguyên.
- Dùng đúng các class có sẵn: card, card-grid, card-title, card-body, tile, tile-value, tile-label, btn, btn-outline, btn-ghost, table, field, input, select, textarea, badge, badge-green, badge-gray, row, stack, muted.
- File phải TỰ CHỨA (self-contained): KHÔNG link/nhúng CSS hay JS framework bên ngoài (không Angular/Material/Bootstrap...). Chỉ dùng CSS/JS đã có sẵn trong file.
- KHÔNG dùng ReplaceInFile/WriteFile/RunCommand/grep cho việc này. Sau khi SetPocContent trả "POC content updated", trả final result NGAY, KHÔNG đọc lại file.

Ví dụ action:
{"type":"tool","tool":"SetPocContent","args":{"content":"<div class=\"card-grid\">...</div>","appName":"Order Management","breadcrumb":"Home > Orders","navItems":[{"label":"Dashboard"},{"label":"Orders","children":["All Orders","Create Order"]},{"label":"Settings"}]}}

Kết quả: content tính năng + App Name + breadcrumb + menu sidebar được cập nhật trong 03_Implementation/poc-demo.html.

# AI Design Spec

{{aiDesignSpec}}
