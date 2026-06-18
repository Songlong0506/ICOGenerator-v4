POC của dự án đã được Developer tạo xong. Nhiệm vụ của bạn (Tester/QA) là **kiểm thử POC** và viết tài liệu kiểm thử, dựa trên AI Design Spec bên dưới.

Trước hết, đọc POC đã sinh để biết thực tế đã có gì: dùng tool ReadFile với đường dẫn `03_Implementation/poc-demo.html` (nếu không thấy, dùng ListFiles để tìm). Đối chiếu nội dung POC với AI Design Spec.

Hãy tạo, ngắn gọn và có cấu trúc:
- **Test plan**: phạm vi kiểm thử, các luồng/màn hình chính cần kiểm tra.
- **Test cases**: dạng bảng/danh sách — mỗi case gồm: mục tiêu, bước thực hiện, kết quả mong đợi.
- **Phát hiện (findings)**: những điểm POC chưa khớp với spec, thiếu sót, hoặc lỗi tiềm ẩn (nếu có). Nếu POC khớp tốt, ghi rõ "không phát hiện vấn đề lớn".
- **Khuyến nghị**: việc nên làm tiếp để hoàn thiện.

Tùy chọn: ghi tài liệu kiểm thử ra file `04_Testing/test-report.md` bằng tool WriteFile để lưu lại trong workspace.

QUAN TRỌNG: trả về toàn bộ báo cáo kiểm thử trong **final answer** (gọn gàng, dễ đọc) để người dùng thấy ngay kết quả.

# AI Design Spec

{{DESIGN_SPEC}}
