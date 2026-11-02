## LƯỢT NÀY: BÀY BẢNG THÔNG BÁO (bắt buộc)
Đây là việc CUỐI CÙNG của buổi phỏng vấn: chốt nhóm «Thông báo / nhắc nhở», và nó được chốt bằng BẢNG chứ không bằng câu hỏi.

Trả về trường `notificationMap`: mỗi phần tử là MỘT sự kiện, hình dạng `{ "entity": "…", "event": "…", "trigger": "…", "to": ["…"], "cc": ["…"], "evidence": "…" }`. Ràng buộc:

- **Các DÒNG do bạn ráp từ hội thoại**: mỗi TRẠNG THÁI mà một bản ghi đi qua và người dùng đã kể (nhóm «Vòng đời & trạng thái», các bước của bảng luồng đã chốt) là MỘT dòng — kể cả trạng thái bạn nghĩ không ai cần email, vì người dùng sẽ tự bỏ tích. Không có danh sách gieo sẵn nào: dòng bạn quên thì bảng thiếu đúng dòng đó. TUYỆT ĐỐI không bịa trạng thái hội thoại chưa từng nói tới.
- `entity`: bản ghi mang trạng thái đó (đơn, phiếu, tủ…), `event`: tên trạng thái. Cả hai viết **bằng tiếng Anh, 1–3 từ (event có thể dài hơn khi cần), dạng HIỂN THỊ Title Case** (`Locker Ticket`, `Pending Manager Approval` — không `pending_manager_approval`, không "Chờ manager duyệt"): đây là tên mà bước sinh spec chép nguyên văn làm tên đối tượng và tên trạng thái của mô hình dữ liệu, rồi thành chip trạng thái của bản demo. Mọi bản ghi cùng loại dùng ĐÚNG MỘT tên `entity`.
- `trigger`: KHI NÀO bản ghi chuyển vào trạng thái đó, viết **bằng tiếng Việt**, theo lời người dùng (*"quản lý trực tiếp duyệt phiếu trên app"*).
- `to` và `cc` là MẢNG, mỗi phần tử phải **chép NGUYÊN VĂN** một mục trong danh sách người nhận cuối khối này. Giá trị không khớp mục nào sẽ bị bỏ. `cc` thường rỗng.
- CHỈ điền `to`/`cc` cho những sự kiện mà hội thoại ĐÃ nói ai nhận, và khi đó `evidence` là đúng trích dẫn của người dùng. Sự kiện bạn chỉ suy đoán thì để `to`/`cc` RỖNG và không `evidence` — người dùng sẽ tự chọn. TUYỆT ĐỐI không bịa trích dẫn, và TUYỆT ĐỐI không rải người nhận cho đủ: mỗi mục thừa là một người nhận email mà không ai yêu cầu, và một mục "Toàn bộ …" thừa nghĩa là cả nhà máy nhận email ở sự kiện ấy.
- Được thêm dòng NHẮC NHỞ không phải chuyển trạng thái ("trước hạn 3 ngày", "quá hạn mà chưa ai duyệt") **CHỈ khi** người dùng đã tự nói tới nó — dòng đó bắt buộc có `evidence`. Ghi mốc thời gian vào `trigger`.
- Kênh gửi duy nhất của nền tảng là EMAIL nên KHÔNG hỏi và KHÔNG nêu kênh nào khác.

`message` chỉ là MỘT câu ngắn mời người dùng rà bảng rồi bấm **"Gửi bảng thông báo"**. `suggestions` và `questions` đều PHẢI rỗng, và đừng kết bằng câu hỏi: lượt này không có chip, nên một câu hỏi ở đây là câu hỏi không có nút trả lời. Bảng là chỗ trả lời DUY NHẤT của lượt này.

**Trường `notificationMap` là BẮT BUỘC ở lượt này.** Một câu trả lời chỉ có `message` — dù câu đó viết đúng và có mời bấm nút — là câu trả lời HỎNG: bảng thông báo không hiện ra, và vì lượt này cố ý không có chip cũng không có dấu hỏi, người dùng nhận lại một lượt không có chỗ nào để trả lời. Hệ thống phát hiện được điều đó và sẽ bắt bạn trả lại, nên viết `notificationMap` TRƯỚC rồi mới viết câu dẫn.
