

---

# VÒNG TỰ SỬA: KỊCH BẢN NGHIỆM THU (UAT) CÒN GÃY

Bạn ĐÃ dựng xong bản demo ở lượt trước; toàn bộ sản phẩm vẫn còn nguyên trong `04_Implementation/poc-demo.html`. Đây KHÔNG phải lần làm đầu tiên và KHÔNG phải nhận xét của người duyệt: máy vừa tự chạy bộ kịch bản ở mục "KỊCH BẢN NGHIỆM THU (UAT)" phía trên, ngay trên bản demo đó (gọi `window.pocScenarios()` trong trình duyệt thật và bấm thử từng bước bằng click), và các kịch bản dưới đây CHƯA ĐẠT. Bản demo chỉ được đưa cho người dùng sau khi mọi kịch bản đều đạt — người dùng sẽ bấm thử đúng các bước này.

Bản bàn giao của lượt trước:
{{previous_output}}

Kịch bản còn gãy (mỗi dòng một kịch bản, đã gộp mọi tầng gãy của nó):
{{failures}}

Cách làm:
- Bạn KHÔNG nhớ nội dung bản demo: tìm đúng đoạn cần sửa bằng SearchInFiles (tên hàm, nhãn nút, `data-view`, tiêu đề kịch bản trong `pocScenarios`) rồi ReadFile đúng phần đó. Chỉ đọc cả file khi thật sự cần.
- Sửa bằng FixPocAndAudit — MỘT call gom mọi chỗ sửa của mọi kịch bản; tool tự audit lại và trả báo cáo mới. Còn gãy thì sửa tiếp theo báo cáo đó.
- Sửa LOGIC và ĐIỀU KHIỂN cho luồng chạy đúng thật: nhãn nút trên UI phải khớp nhãn ghi trong bước, nút được nhắc tới phải làm việc thật. TUYỆT ĐỐI không sửa assertion trong `pocScenarios()` cho pass giả, không hard-code `pass: true`, không đổi tiêu đề kịch bản cho khỏi bị khớp.
- KHÔNG phá những phần đang đạt: báo cáo audit có mục **REGRESSIONS** thì xử lý mục đó trước tiên. Không làm lại bản demo từ đầu, không bỏ màn hình hay neo `data-uat` nào.
- Mọi yêu cầu của nhiệm vụ gốc ở trên vẫn giữ nguyên hiệu lực.

## ĐỊNH DẠNG TRẢ LỜI (BẮT BUỘC)

Khi audit báo các kịch bản trên đã đạt (hoặc bạn không còn cách sửa), trả lời cuối (text, không gọi tool) theo dạng danh sách đối chiếu — mỗi kịch bản một dòng `- <tiêu đề kịch bản>: đã <việc đã sửa>` (kịch bản nào chưa sửa được thì ghi rõ vì sao). Câu trả lời này được nối vào bàn giao mà người duyệt đọc, nên viết bằng tiếng Việt, dễ hiểu với người không rành kỹ thuật.
