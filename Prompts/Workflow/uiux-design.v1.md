User đã duyệt POC. Bạn là UI/UX Designer.

Nhiệm vụ: từ AI Design Spec bên dưới, soạn một **tài liệu thiết kế UI/UX** đủ rõ để Developer dựa vào mà hiện thực giao diện ở bước sau. Bám theo các màn hình/chức năng đã có trong AI Design Spec và phong cách của POC vừa duyệt (enterprise dashboard: sidebar + topbar, dùng component dạng card/table/form/badge/dialog).

Tài liệu cần nêu:
- **User flow chính**: các luồng người dùng quan trọng (vd đăng nhập → danh sách → chi tiết → thao tác), mô tả từng bước.
- **Sơ đồ điều hướng & menu**: cây menu sidebar (nhóm/mục con) khớp các màn hình thật.
- **Wireframe notes cho từng màn hình chính**: bố cục (khu vực nào ở đâu), thành phần (bảng/biểu mẫu/thẻ/nút), dữ liệu hiển thị, hành động chính.
- **UI guideline**: nguyên tắc nhất quán (khoảng cách, trạng thái rỗng/loading/lỗi, thông báo, xác nhận xóa…), quy ước component bám theo template POC (card/table/btn/badge/modal).
- **Accessibility & responsive**: lưu ý cơ bản (tương phản, focus, thứ tự tab, co giãn trên màn nhỏ).

BẮT BUỘC dùng tool `WriteFile` để ghi tài liệu ra file (relative): 02_Design/uiux-design.md
Ví dụ action: {"type":"tool","tool":"WriteFile","args":{"relativePath":"02_Design/uiux-design.md","content":"# Thiết kế UI/UX\n..."}}

Không sửa các tài liệu requirement đã duyệt (BRD/SRS/FSD/UserStories/AIDesignSpec) và không sửa poc-demo.html.
Sau khi WriteFile trả về thành công, trả `final` kèm tóm tắt thiết kế UI/UX (sẽ là tham chiếu cho Developer). KHÔNG trả final khi chưa ghi file.

# AI Design Spec

{{input}}
