User đã approve requirement.

Chỉ sử dụng AI Design Spec bên dưới để generate code.
Không đọc BRD/SRS/FSD/UserStories.
Không sửa requirement document.

YÊU CẦU GIAO DIỆN (bắt buộc — để POC đồng bộ với template có sẵn):
- File '03_Implementation/poc-demo.html' ĐÃ TỒN TẠI sẵn (là bản sao của shell template: <head> + <style>, <script>, sidebar/topbar, 2 popup User/Imprint đều đã hoàn chỉnh). KHÔNG đọc lại toàn bộ file và KHÔNG ghi đè cả file.
- CHỈ chỉnh sửa TẠI CHỖ bằng tool Edit: thay phần nằm GIỮA "<!-- POC_CONTENT_START -->" và "<!-- POC_CONTENT_END -->" bằng UI của tính năng theo AI Design Spec. PHẢI giữ nguyên 2 dòng marker đó. Để xem cú pháp/khoảng trắng quanh marker, chỉ cần đọc đoạn nhỏ quanh chúng (vd Grep "POC_CONTENT"), KHÔNG đọc cả file.
- Dùng đúng các class có sẵn: card, card-grid, card-title, card-body, tile, tile-value, tile-label, btn, btn-outline, btn-ghost, table, field, input, select, textarea, badge, badge-green, badge-gray, row, stack, muted.
- Có thể đổi text cho khớp tính năng bằng các Edit nhỏ, riêng lẻ: App Name (.app-name), breadcrumb, và các nav-item/nav-group (mỗi nav-group có .nav-item + .nav-sub). TUYỆT ĐỐI KHÔNG sửa <head>/<style>, <script>, cấu trúc shell (.supergraphic, .sidebar, .topbar) hay 2 popup User/Imprint.
- File phải TỰ CHỨA (self-contained): KHÔNG link/nhúng CSS hay JS framework bên ngoài (không Angular/Material/Bootstrap...). Chỉ dùng CSS/JS đã có sẵn trong file.

Kết quả: chỉnh sửa tại chỗ file 03_Implementation/poc-demo.html (chỉ vùng giữa 2 marker).

# AI Design Spec

{{aiDesignSpec}}
