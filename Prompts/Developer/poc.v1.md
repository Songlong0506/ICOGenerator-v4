User đã approve requirement.

Chỉ sử dụng AI Design Spec bên dưới để generate code.
Không đọc BRD/SRS/FSD/UserStories.
Không sửa requirement document.

YÊU CẦU GIAO DIỆN (bắt buộc — để POC đồng bộ với template có sẵn):
- File '03_Implementation/poc-demo.html' ĐÃ TỒN TẠI sẵn (là bản sao của shell template: <head> + <style>, <script>, sidebar/topbar, 2 popup User/Imprint đều đã hoàn chỉnh). KHÔNG đọc lại toàn bộ file và KHÔNG ghi đè cả file.
- Dùng tool SetPocContent ĐÚNG MỘT LẦN với các tham số:
  • 'content' = HTML giao diện của tính năng theo AI Design Spec (chỉ phần nội dung bên trong, KHÔNG kèm <html>/<head>/<body>/sidebar/topbar).
  • 'appName' = TÊN ỨNG DỤNG/tính năng (thay 'App Name' mặc định — BẮT BUỘC, KHÔNG để nguyên).
  • 'nav' = HTML các mục menu trái khớp tính năng (dùng đúng markup nav-item/nav-group/nav-sub như template), thay menu mẫu 'Overview/Module A/Module B/Settings'.
  • 'breadcrumb' = breadcrumb top bar cho khớp tính năng.
  Hệ thống tự đặt từng phần vào đúng vùng marker (POC_CONTENT, POC_APPNAME, POC_NAV, POC_BREADCRUMB) và giữ nguyên phần shell còn lại.
- Dùng đúng các class có sẵn: card, card-grid, card-title, card-body, tile, tile-value, tile-label, btn, btn-outline, btn-ghost, table, field, input, select, textarea, badge, badge-green, badge-gray, row, stack, muted.
- TUYỆT ĐỐI KHÔNG sửa <head>/<style>, <script>, cấu trúc shell (.supergraphic, .sidebar, .topbar) hay 2 popup User/Imprint.
- File phải TỰ CHỨA (self-contained): KHÔNG link/nhúng CSS hay JS framework bên ngoài (không Angular/Material/Bootstrap...). Chỉ dùng CSS/JS đã có sẵn trong file.

Kết quả: content + appName + nav + breadcrumb được đặt vào file 03_Implementation/poc-demo.html theo đúng các vùng marker.

# AI Design Spec

{{aiDesignSpec}}
