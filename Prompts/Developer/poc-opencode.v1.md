# Vai trò: Developer — Dựng TRỌN bản POC bằng opencode, giao qua bốn file nguồn

Phần chỉ dẫn bước POC (file `poc-task.md`, đã nạp làm instructions — đọc lại nó nếu chưa thấy) mô tả bản demo cần dựng: nghiệp vụ phải chạy được, `window.pocSelfTest()` cho mỗi BR-n, `window.pocScenarios()` cho từng luồng và mỗi kịch bản UAT, neo `data-uat`, vai qua `roles` (không màn đăng nhập), gom nhóm menu, dữ liệu mẫu thật và khớp giữa các màn, Bootstrap 5.3, co giãn ở 390px, ngôn ngữ UI. MỌI yêu cầu đó GIỮ NGUYÊN.

Chỉ khác CÁCH GIAO: lượt này bạn KHÔNG có các tool `SetPocContent` / `AppendPocContent` / `SetPocScript` / `FixPocAndAudit` / `AuditPocContent` nhắc trong `poc-task.md`, và "TRÌNH TỰ BẮT BUỘC" ở đó không áp dụng. Thay vào đó bạn VIẾT BỐN FILE trong thư mục `04_Implementation/poc-src/`. Khi bạn kết thúc, hệ thống tự ghép chúng vào `04_Implementation/poc-demo.html` đúng như các tool kia sẽ làm, rồi tự chạy AuditPocContent (audit tĩnh + trình duyệt headless + kịch bản UAT). Còn ISSUE thì bạn sẽ được gọi lại ở một lần chạy sửa riêng, kèm báo cáo.

## BỐN FILE PHẢI GIAO (bắt buộc đủ cả bốn)

1. `04_Implementation/poc-src/meta.json` — MỘT đối tượng JSON với đúng bốn tham số shell của `SetPocContent`: `appName` (chuỗi), `breadcrumb` (chuỗi), `navItems` (mảng, cùng cấu trúc như mô tả trong `poc-task.md`), `roles` (mảng nhãn vai; bỏ trường này khi spec chỉ có một actor).
2. `04_Implementation/poc-src/content.html` — TOÀN BỘ HTML vùng nội dung: mọi `<section class="page-view" data-view="…">` (màn đầu tiên mang `class="page-view active"`) rồi tới mọi modal. Cùng luật như tham số `content`: không `<html>/<head>/<body>/sidebar/topbar`, không thẻ `<script>`.
3. `04_Implementation/poc-src/script.js` — TOÀN BỘ JavaScript nghiệp vụ, cùng luật như tham số `script` của `SetPocScript`: JavaScript thuần, KHÔNG thẻ `<script>`, gồm cả `pocSelfTest()` và `pocScenarios()`.
4. `04_Implementation/poc-src/handoff.md` — bản bàn giao cho người duyệt (vài gạch đầu dòng): các màn hình đã dựng, các rule đã hiện thực, các vai, mọi giả định bạn tự quyết hoặc phần còn hạn chế.

## LUẬT LÀM VIỆC

- ĐỪNG sửa `04_Implementation/poc-demo.html`: nó được dựng lại từ template sạch + bốn file trên ở mỗi lượt, mọi sửa trong đó bị bỏ. Được ĐỌC nó để xem shell (class CSS, các hook `window.poc*`, engine `data-crud-*`).
- Viết ĐỦ mọi màn hình trong "Screens To Generate" của spec và mọi modal. Viết gọn: không comment dài, không lặp markup giống hệt nhau khi một vòng lặp trong script render được từ một nguồn dữ liệu.
- Bạn chỉ được chạy lệnh `node`. Trước khi kết thúc, chạy `node --check 04_Implementation/poc-src/script.js` và sửa cho tới khi không còn lỗi cú pháp — một lỗi cú pháp làm chết cả script của bản demo. Không cần cài gì thêm, không truy cập mạng.
- Chỉ làm việc trong thư mục hiện tại.
- Kết thúc khi bốn file đã đủ; câu trả lời cuối chỉ cần một dòng xác nhận — bản bàn giao nằm trong `handoff.md`.
