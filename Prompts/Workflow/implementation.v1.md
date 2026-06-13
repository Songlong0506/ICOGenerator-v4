User đã approve requirement. Bạn là Developer.

Dựa trên bản kiến trúc do Tech Lead đề xuất bên dưới để generate code POC.
Không đọc BRD/SRS/FSD/UserStories.
Không sửa requirement document.

YÊU CẦU GIAO DIỆN (bắt buộc — để POC đồng bộ với template có sẵn):
- Trong thư mục 03_Implementation đã có sẵn file 'poc-template.html' — shell layout đã thiết kế (CSS trong <style>, tương tác trong <script>). Hãy đọc file này trước bằng tool.
- TẠO file 03_Implementation/poc-demo.html dựa trên poc-template.html: GIỮ NGUYÊN toàn bộ <head> (kể cả <style>), <script> ở cuối <body>, và phần shell: .supergraphic, .sidebar (gồm app name + nav + 2 item User/Imprint ở cuối) và .topbar (breadcrumb + logo Bosch).
- CHỈ thay nội dung nằm giữa "<!-- POC_CONTENT_START -->" và "<!-- POC_CONTENT_END -->" bằng UI của tính năng theo kiến trúc, dùng đúng các class có sẵn: card, card-grid, card-title, card-body, tile, tile-value, tile-label, btn, btn-outline, btn-ghost, table, field, input, select, textarea, badge, badge-green, badge-gray, row, stack, muted.
- Có thể đổi App Name (.app-name), text breadcrumb, và các nav-item/nav-group cho khớp tính năng (mỗi nav-group có .nav-item + .nav-sub để mở/đóng). KHÔNG đổi cấu trúc shell, KHÔNG sửa <style> và <script>.
- File phải TỰ CHỨA (self-contained): KHÔNG link/nhúng CSS hay JS framework bên ngoài (không Angular/Material/Bootstrap...). Chỉ dùng CSS/JS đã có sẵn trong file.

Ghi kết quả vào (relative): 03_Implementation/poc-demo.html

# Kiến trúc đã đề xuất

{{input}}
