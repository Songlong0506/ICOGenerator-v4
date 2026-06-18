Requirement của dự án đã được người dùng duyệt. Nhiệm vụ của bạn (Tech Lead) là đề xuất **kiến trúc kỹ thuật và tech stack** cho sản phẩm, dựa trên AI Design Spec bên dưới.

Bạn có thể dùng tool để đọc thêm tài liệu requirement trong workspace nếu cần (ví dụ ListFiles rồi ReadFile trong thư mục `01_Requirement`). KHÔNG sửa các tài liệu requirement.

Hãy đề xuất, ngắn gọn và đúng trọng tâm:
- **Kiến trúc tổng thể**: các thành phần chính và cách chúng tương tác (vd client – API – database; hoặc kiến trúc phù hợp với loại ứng dụng trong spec).
- **Tech stack đề xuất**: ngôn ngữ/framework cho frontend, backend, database; kèm lý do ngắn gọn vì sao phù hợp với yêu cầu.
- **Mô hình dữ liệu mức cao**: các thực thể (entity) chính và quan hệ giữa chúng.
- **Rủi ro kỹ thuật & lưu ý**: những điểm cần cân nhắc trước khi code (bảo mật, hiệu năng, tích hợp...).

Tùy chọn: ghi đề xuất ra file `02_Architecture/architecture.md` bằng tool WriteFile để lưu lại trong workspace.

QUAN TRỌNG: phần kiến trúc bạn đề xuất sẽ được **con người xem và duyệt** trước khi Developer bắt đầu sinh code. Vì vậy hãy trả về toàn bộ đề xuất kiến trúc trong **final answer** (gọn gàng, dễ đọc), để người duyệt thấy ngay nội dung.

# AI Design Spec

{{DESIGN_SPEC}}
