

---

# YÊU CẦU CHỈNH SỬA TỪ NGƯỜI DUYỆT (revision)

Bạn ĐÃ hoàn thành bước này trước đó và người duyệt đã xem kết quả. Đây KHÔNG phải lần làm đầu tiên: toàn bộ sản phẩm của lần trước vẫn còn nguyên trong workspace.

Bản bàn giao của lần trước:
{{previous_output}}

Nhận xét của người duyệt — đây là yêu cầu quan trọng nhất, phải xử lý TRỌN VẸN từng ý:
{{feedback}}

Cách làm:
- ĐỌC lại sản phẩm hiện có trong workspace (ListFiles/ReadFile) trước khi sửa để biết chính xác hiện trạng.
- CHỈNH SỬA trên sản phẩm hiện có theo đúng nhận xét, dùng đúng các tool mà nhiệm vụ gốc ở trên mô tả. KHÔNG làm lại từ đầu, KHÔNG phá những phần đã đúng, trừ khi nhận xét yêu cầu rõ như vậy.
- Mọi yêu cầu của nhiệm vụ gốc (ghi file đúng đường dẫn, định dạng bàn giao…) vẫn giữ nguyên hiệu lực.
- Kết thúc, trả lời cuối (text, không gọi tool) nêu rõ đã thay đổi những gì ứng với TỪNG ý trong nhận xét, theo dạng danh sách đối chiếu — mỗi ý một dòng `- Ghi chú #n / <tóm tắt ý>: đã <việc đã làm>` (ý nào quyết định KHÔNG sửa thì ghi rõ lý do). Bàn giao này được hiển thị nguyên văn cho người duyệt ở mục "Nhật ký vòng sửa", nên viết bằng ngôn ngữ của nhận xét, dễ hiểu với người không rành kỹ thuật.
