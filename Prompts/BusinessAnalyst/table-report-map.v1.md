## LƯỢT NÀY: BÀY BẢNG BÁO CÁO / THỐNG KÊ (bắt buộc)
Lượt này chốt nhóm «Báo cáo / thống kê»: người dùng đã kể họ cần xem những con số / danh sách tổng hợp nào, việc của bạn là ráp lại thành một danh sách có ranh giới để họ rà.

Trả về trường `reportMap`: mỗi phần tử là MỘT báo cáo, hình dạng `{ "report": "…", "question": "…", "source": "…", "breakdown": "…" }`. Ràng buộc:

- `report`: tên đọc được như MỘT MÀN HÌNH, viết **bằng tiếng Anh, 2–4 từ**, thường có hậu tố `Report`/`Dashboard` (*"Remaining Leave Report"*) — mỗi dòng người dùng giữ lại sẽ thành một màn hình thật của ứng dụng rồi thành một nhãn mục menu của bản demo (cùng luật đặt tên với danh sách phạm vi màn hình), nên một cái tên trống nghĩa (*"Thống kê"*, *"Báo cáo tổng hợp"*) là một màn hình không ai rà nổi.
- `question`: báo cáo này TRẢ LỜI CÂU HỎI GÌ, viết bằng **lời người dùng** và **giữ tiếng Việt** — chỉ cột tên là tiếng Anh (*"để biết tháng này ai chưa đi học"*). KHÔNG viết mô tả chức năng kiểu tài liệu (*"hiển thị danh sách có phân trang"*): phần đó là việc của bước sinh spec, còn ô này là thứ chỉ người dùng mới biết.
- `source`: số liệu lấy từ ĐỐI TƯỢNG nào — **chép đúng** tên một đối tượng trong danh sách cuối khối này. Tên không khớp đối tượng nào sẽ bị hệ thống xoá khỏi ô, nên đừng bịa một nguồn mới.
- `breakdown`: các chiều gộp / lọc (kỳ báo cáo, đơn vị, trạng thái, người phụ trách…), ngăn bằng dấu chấm phẩy. Đây là cột phân biệt một báo cáo thật với một bảng đổ dữ liệu ra màn hình — chưa rõ thì để rỗng, đừng điền *"theo thời gian"* cho có.
- CHỈ nêu báo cáo mà hội thoại (hoặc tài liệu nguồn) đã nói tới. TUYỆT ĐỐI không rải thêm cho đủ bộ: mỗi dòng thừa là một MÀN HÌNH mà người dùng chưa từng đặt hàng, và nó đi thẳng vào phạm vi rồi vào bản demo. Cùng một câu hỏi nghiệp vụ xem theo tháng / quý / năm là MỘT dòng, kỳ báo cáo ghi ở `breakdown`.
- KHÔNG có cột "ai xem": mỗi báo cáo là một màn hình nên quyền xem của nó — kèm cả phạm vi dữ liệu — sẽ được chốt ở bảng phân quyền ngay sau đây. Đừng nhét vai trò vào `question`.
- **Bảng này KHÔNG có trường `evidence`**, cùng lý do với `flowMap` và `screenScopeMap`: mọi dòng đều ra tích sẵn nên một trích dẫn ở đây chỉ khóa cứng dòng lại đúng ở chiều người dùng cần bác.

`message` chỉ là MỘT câu ngắn mời người dùng rà bảng rồi bấm **"Gửi bảng báo cáo"**. `suggestions` và `questions` đều PHẢI rỗng, và đừng kết bằng câu hỏi: lượt này không có chip, nên một câu hỏi ở đây là câu hỏi không có nút trả lời. Bảng là chỗ trả lời DUY NHẤT của lượt này.
