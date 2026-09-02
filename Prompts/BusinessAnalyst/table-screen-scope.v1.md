# shape:first
## LƯỢT NÀY: BÀY BẢNG MÀN HÌNH (bắt buộc)
Lượt này chốt PHẠM VI MÀN HÌNH của ứng dụng. Danh sách dưới đây được chắt ra từ hội thoại nhưng người dùng chưa bao giờ nhìn thấy nó — mà mọi thứ phía sau (bảng phân quyền, các màn hình của bản demo) đều đứng trên đúng danh sách này.

Phạm vi này đã GỒM CẢ các màn hình do hai bảng người dùng vừa chốt sinh ra: màn hình quản lý từng danh mục mà ứng dụng tự quản lý (từ bảng đối tượng) và mỗi báo cáo còn giữ (từ bảng báo cáo). Chúng là quyết định NGƯỜI DÙNG vừa chốt, không phải mục bạn chắt ra — phải có dòng riêng, và phần `purpose`/`functions` viết đúng như một màn hình quản lý danh mục (xem, thêm, sửa, bỏ) hoặc một màn hình báo cáo (xem, lọc, xuất).

# shape:reshow
## LƯỢT NÀY: BỔ SUNG BẢNG MÀN HÌNH ĐÃ CHỐT (bắt buộc)
Người dùng đã tự tay rà và CHỐT bảng màn hình ở một lượt trước. Sau đó hội thoại lộ thêm phần MỚI, và lượt này bày lại bảng chỉ để lấy phần mới đó. Hệ thống giữ nguyên các dòng người dùng đã duyệt, nên bạn CHỈ mô tả các mục ở phần "MỚI" cuối khối này — mô tả lại màn hình đã có là công bỏ đi, và câu dẫn của lượt do hệ thống soạn nên đừng nhắc tới chúng.

# rules
Trả về trường `screenScopeMap`: mỗi phần tử là MỘT MÀN HÌNH, hình dạng `{ "screen": "…", "purpose": "…", "functions": [ { "name": "…", "flowSteps": ["…"] } ], "covers": ["…"] }`. Ràng buộc:

- `screen` phải **chép đúng một mục** trong danh sách phạm vi cuối khối này — không thêm màn hình mới, không tự đặt tên khác, **không dịch tên tiếng Anh sang tiếng Việt**, không thêm chữ dẫn kiểu "Màn hình …" / "… Screen" (tên màn hình là nhãn menu của bản demo, và tên ngắn thì phép so khớp bù chỉ chạy khi bạn chép đúng). Mục nào bạn không nêu, hệ thống tự bổ sung vào bảng.
- **MỘT DÒNG = MỘT MÀN HÌNH**, không phải một tính năng và không phải một luồng. Danh sách phạm vi được chắt theo lượt nên hay lẫn cả ba loại: mục nào đọc lên là một CHỨC NĂNG (*"Tính năng Generate Training Implement từ Training Plan Detail"*, *"Chỉnh sửa số lượng lớp"*) hay một LUỒNG (*"Luồng đăng ký khóa học với trạng thái pending, enroll, waitlist"*) thì ĐỪNG dựng thành dòng riêng: đưa nó vào `functions` của màn hình thật sự chứa nó, và ghi **nguyên văn** mục đó vào `covers` của dòng ấy. Không ghi vào `covers` thì hệ thống tưởng bạn bỏ quên và bổ sung nó lại thành một dòng trắng.
- `purpose`: MỘT câu nói màn hình này để làm gì, theo góc nhìn người dùng nghiệp vụ. Cùng luật với `description` của bảng đối tượng: nói màn hình LÀ GÌ, không kể AI LÀM GÌ với nó — chuỗi bước đã có bảng luồng.
- `functions`: các chức năng trên màn, MỖI CHỨC NĂNG MỘT PHẦN TỬ — người dùng tích / bỏ tích từng chức năng một, nên đừng gói nhiều việc vào một `name` ("Xem, Sửa và Gửi duyệt" là ba chức năng, không phải một).
- `flowSteps` của TỪNG chức năng: các BƯỚC của bảng luồng đã chốt mà CHỨC NĂNG ĐÓ phụ trách — chép phần `action` của bước. Đây là phần quan trọng nhất của bảng: MỌI bước trong danh sách cuối khối này phải được ÍT NHẤT MỘT chức năng nhận, và hệ thống đối chiếu tất định chỗ này rồi nói thẳng cho người dùng biết bước nào chưa có ai phụ trách. Gắn theo TỪNG chức năng chứ không theo cả trang, vì một bước do một chức năng thực hiện. Chức năng tra cứu không nằm trong luồng nào thì để mảng rỗng.
- **Bảng này KHÔNG có trường `evidence`** — và đừng tự thêm: mọi dòng và mọi chức năng đều ra tích sẵn, nên một trích dẫn ở đây không đổi được trạng thái ô nào; nó chỉ thành một dấu ✓ có tooltip mà người dùng phải rời bảng lăn ngược hội thoại mới kiểm được.

`message` chỉ là MỘT câu ngắn mời người dùng rà bảng rồi bấm **"Gửi bảng màn hình"**. `suggestions` và `questions` đều PHẢI rỗng, và đừng kết bằng câu hỏi: lượt này không có chip, nên một câu hỏi ở đây là câu hỏi không có nút trả lời. Bảng là chỗ trả lời DUY NHẤT của lượt này.
