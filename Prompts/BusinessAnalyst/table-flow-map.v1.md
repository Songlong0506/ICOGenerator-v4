## LƯỢT NÀY: BÀY BẢNG LUỒNG NGHIỆP VỤ (bắt buộc)
Các luồng chính đã rõ trong hội thoại. Lượt này bạn ráp chúng lại thành BẢNG để người dùng rà từng bước — họ chưa bao giờ nhìn thấy bản bạn ráp, mà chính bản đó mới là thứ đi vào tài liệu.

Trả về trường `flowMap`: mỗi phần tử là MỘT luồng, hình dạng `{ "name": "…", "kind": "luồng chính" | "ngoại lệ", "role": "…", "trigger": "…", "steps": [ { "actor": "…", "action": "…", "outcome": "…" } ] }`. Ràng buộc:

- `name`: tên luồng theo ngôn ngữ nghiệp vụ ("Đăng ký khóa học", "Duyệt kế hoạch quý").
- `kind`: `"luồng chính"` hoặc `"ngoại lệ"`. PHẢI có ít nhất MỘT ngoại lệ nếu hội thoại có nhắc tới bất kỳ đường hỏng nào (từ chối, quá hạn, trùng, thiếu điều kiện). Ngoại lệ là phần người dùng không bao giờ tự kể — họ coi nó là hiển nhiên — nên đây là chỗ rẻ nhất để hỏi.
- `role`: vai trò khởi xướng luồng. `trigger`: CHỈ với ngoại lệ — điều kiện làm nó xảy ra.
- `steps`: từ 2 tới 10 bước theo đúng thứ tự, mỗi bước `{actor, action, outcome}`. `actor` là vai làm bước đó; `outcome` là trạng thái/kết quả sau bước (để rỗng nếu bước không đổi trạng thái). Luồng một bước KHÔNG phải luồng — hệ thống sẽ loại nó.
- CHỈ mô tả điều người dùng ĐÃ nói / đã chốt. Không thêm bước "cho đủ quy trình".
- **Bảng này KHÔNG có trường `evidence`** — và đừng tự thêm: mọi bước đều ra ở trạng thái ĐƯỢC GIỮ, nên một trích dẫn ở đây không đổi được trạng thái nào; nó chỉ khóa cứng dòng lại đúng ở chiều người dùng cần bác.

`message` chỉ là MỘT câu ngắn mời người dùng rà bảng rồi bấm **"Gửi bảng luồng"**. `suggestions` và `questions` đều PHẢI rỗng, và đừng kết bằng câu hỏi: lượt này không có chip, nên một câu hỏi ở đây là câu hỏi không có nút trả lời. Bảng là chỗ trả lời DUY NHẤT của lượt này.
