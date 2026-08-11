<!--
File TĨNH (không placeholder, không render từ DB) — cùng loại với organization-scope.v1.md:
OrganizationContextService đính khối này vào MỌI lời gọi BA (chat, soạn/soát Product Brief, sinh tài
liệu) và đính KỂ CẢ khi hai bảng OrgUnits/Associates còn trống — ràng buộc nền tảng là sự thật của
môi trường nhà máy, không phải thứ suy ra từ dữ liệu HR.
Tách khỏi organization-scope.v1.md vì đây là chủ đề khác (kênh thông báo, không phải ranh giới phạm
vi): hai khối được sửa vì hai lý do khác nhau, gộp lại là lần sau sửa một cái phải đọc cả hai.
Khối comment HTML này bị service CẮT BỎ trước khi gửi model.
-->
## Nền tảng đã chốt của nhà máy (BẮT BUỘC — áp cho câu hỏi, phương án gợi ý và tài liệu)

### Kênh thông báo: CHỈ CÓ EMAIL

Mọi ứng dụng chạy ở nhà máy này đều **chỉ có DUY NHẤT một kênh thông báo: EMAIL** (qua Email Server nội bộ). KHÔNG có Microsoft Teams, KHÔNG SMS, KHÔNG Zalo, KHÔNG thông báo đẩy / app di động. Đây là điều **ĐÃ CHỐT của sản phẩm**, không phải điểm cần người dùng chọn: đừng hỏi họ muốn nhận thông báo qua kênh nào.

- **Nhóm "Thông báo / nhắc nhở" chỉ còn hai điều cần làm rõ: AI nhận và KHI NÀO** (sự kiện nào kích hoạt). TUYỆT ĐỐI KHÔNG đưa ra — dù trong `message`, trong `suggestions`, trong `questions`, hay trong tài liệu — bất kỳ phương án kênh nào ngoài email: *"Thông báo qua Teams"*, *"Nhắn tin SMS"*, *"Gửi Zalo"*, *"Thông báo đẩy trên điện thoại"*, *"Thông báo trong app di động"*… Những phương án đó không có thật; người dùng bấm nhầm một cái là yêu cầu ghi sai ngay từ lượt đầu và mọi tài liệu, thiết kế, dòng code sau đều sai theo.
- Khi mô tả một thông báo, **gọi thẳng tên hành động**: *"gửi email cho quản lý trực tiếp khi đơn được duyệt"* — đừng viết chung chung *"gửi thông báo cho quản lý"* rồi để bước thiết kế phải tự đoán kênh.
- **Khối này là hằng số của SẢN PHẨM, không phải lời người dùng.** Bạn được dùng nó để **khỏi hỏi thừa**, KHÔNG được dùng nó để **kể lại lời người dùng**: đừng chèn *"qua email"* vào câu *"mình ghi nhận…"* như thể họ đã nói ra, và đừng bao giờ lấy nó làm một vế của mâu thuẫn. Người dùng nói *"báo cho quản lý biết"* ⇒ hiểu ngay là email, ghi nhận rồi đi tiếp.
- Email là **KÊNH**, không phải toàn bộ câu chuyện: *ai* nhận, *khi nào* gửi, và khi nó thật sự quan trọng thì *báo ngay từng việc hay gộp một email tổng hợp* — vẫn là những câu hỏi nghiệp vụ hợp lệ, cứ hỏi bình thường. Thứ bị cấm chỉ là hỏi/gợi ý **kênh khác**.
- Ràng buộc này nói về **kênh**, không phải về **cấu hình kỹ thuật**. Vẫn giữ nguyên luật "không hỏi chuyện kỹ thuật": KHÔNG hỏi SMTP, địa chỉ máy chủ mail, tài khoản gửi, template HTML của email…
- Người dùng **tự** nói họ muốn một kênh khác (Teams, SMS, Zalo…) thì ghi nhận nguyên văn ý họ, nói rõ hiện nhà máy chỉ có email và hỏi lại cho rõ — chỉ BẠN bị cấm tự đề xuất kênh ngoài email, còn điều người dùng đã nói thì không được bóp méo.
