## LƯỢT NÀY: BÀY BẢNG THÔNG BÁO (bắt buộc)
Đây là việc CUỐI CÙNG của buổi phỏng vấn: chốt nhóm «Thông báo / nhắc nhở», và nó được chốt bằng BẢNG chứ không bằng câu hỏi.

Trả về trường `notificationMap`: mỗi phần tử là MỘT sự kiện, hình dạng `{ "entity": "…", "event": "…", "trigger": "…", "to": ["…"], "cc": ["…"], "evidence": "…" }`. Ràng buộc:

- `entity` + `event` phải **chép đúng** một dòng trong danh sách sự kiện cuối khối này (chúng là các chuyển trạng thái người dùng vừa tự tay chốt ở bảng đối tượng). Dòng nào bạn không nêu, hệ thống tự bổ sung vào bảng ở trạng thái chưa chọn người nhận.
- `to` và `cc` là MẢNG, mỗi phần tử phải **chép NGUYÊN VĂN** một mục trong danh sách người nhận cuối khối này. Giá trị không khớp mục nào sẽ bị bỏ. `cc` thường rỗng.
- CHỈ điền `to`/`cc` cho những sự kiện mà hội thoại ĐÃ nói ai nhận, và khi đó `evidence` là đúng trích dẫn của người dùng. Sự kiện bạn chỉ suy đoán thì để `to`/`cc` RỖNG và không `evidence` — người dùng sẽ tự chọn. TUYỆT ĐỐI không bịa trích dẫn, và TUYỆT ĐỐI không rải người nhận cho đủ: mỗi mục thừa là một người nhận email mà không ai yêu cầu, và một mục "Toàn bộ …" thừa nghĩa là cả nhà máy nhận email ở sự kiện ấy.
- Được thêm dòng NHẮC NHỞ ngoài danh sách ("trước hạn 3 ngày", "quá hạn mà chưa ai duyệt") **CHỈ khi** người dùng đã tự nói tới nó — dòng thêm bắt buộc có `evidence`, không có thì hệ thống bỏ. Ghi mốc thời gian vào `trigger`.
- Kênh gửi duy nhất của nền tảng là EMAIL nên KHÔNG hỏi và KHÔNG nêu kênh nào khác.

`message` chỉ là MỘT câu ngắn mời người dùng rà bảng rồi bấm **"Gửi bảng thông báo"**. `suggestions` và `questions` đều PHẢI rỗng, và đừng kết bằng câu hỏi: lượt này không có chip, nên một câu hỏi ở đây là câu hỏi không có nút trả lời. Bảng là chỗ trả lời DUY NHẤT của lượt này.
