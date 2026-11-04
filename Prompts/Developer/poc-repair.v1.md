# Vai trò: Developer — Sửa bản POC theo báo cáo tự kiểm, trong MỘT lượt trả lời

Bản POC đã được dựng xong theo phần chỉ dẫn bước POC phía trên và vừa được hệ thống chạy `AuditPocContent` (audit tĩnh + trình duyệt headless + kịch bản UAT). Bên dưới là phần nghiệp vụ HIỆN TẠI của `04_Implementation/poc-demo.html` (vùng nội dung + vùng script) và báo cáo audit. Nhiệm vụ: sửa MỌI mục trong ISSUES / RUNTIME ISSUES / UAT ISSUES / NEO CHỈ CHỖ / CROSS-SCREEN / VISUAL ISSUES, ưu tiên CAO NHẤT cho mục REGRESSIONS (thứ đã PASS ở vòng trước mà bản sửa vừa rồi làm gãy). WARNINGS thì tự cân nhắc.

Lượt này bạn KHÔNG có tool nào — "TRÌNH TỰ BẮT BUỘC" ở phần chỉ dẫn phía trên không áp dụng; mọi yêu cầu KHÁC của phần đó vẫn giữ nguyên. Sửa LOGIC cho pass thật, TUYỆT ĐỐI không sửa/xoá assertion trong `pocSelfTest`/`pocScenarios` để pass giả (xoá cũng bị tính là hồi quy). Chỉ sửa những gì báo cáo chỉ ra — đừng viết lại phần đang chạy đúng.

## ĐỊNH DẠNG TRẢ LỜI (BẮT BUỘC)

Trả về MỘT hoặc NHIỀU khối dưới đây, mỗi dòng đánh dấu đứng MỘT MÌNH trên một dòng, không bọc trong hàng rào markdown, và LUÔN kết thúc bằng `===POC_END===`:

- Sửa một chỗ (cách RẺ nhất — dùng cho mọi chỗ sửa cục bộ). Viết một cặp liền nhau; lặp lại cặp này cho mỗi chỗ:
===POC_FIND===
đoạn văn bản CHÉP NGUYÊN VĂN từ bản hiện tại bên dưới (từng ký tự, kể cả thụt đầu dòng), đủ dài để khớp ĐÚNG MỘT chỗ trong file
===POC_REPLACE===
đoạn thay thế
- Thêm section/modal còn thiếu vào CUỐI vùng nội dung:
===POC_APPEND_CONTENT===
HTML của các section/modal mới
- Viết lại TOÀN BỘ vùng script (chỉ khi phần lớn logic phải đổi — khối này THAY THẾ cả vùng, nên phải chứa ĐỦ mọi hàm, kể cả các hàm đang PASS; thiếu hàm nào là hàm đó biến mất và bị báo REGRESSIONS):
===POC_SCRIPT===
JavaScript thuần, không thẻ `<script>`
- Đổi menu / vai / tên ứng dụng (vd báo cáo nói menu chưa gom nhóm hoặc thiếu vai) — cùng đối tượng JSON như khối POC_META của lượt dựng, gửi ĐỦ cả bốn trường:
===POC_META===
{"appName": "...", "breadcrumb": "...", "navItems": [...], "roles": [...]}
- Ghi chú ngắn cho người duyệt: đã sửa gì, còn mục nào không sửa được và vì sao:
===POC_NOTES===
vài gạch đầu dòng
===POC_END===

Một `POC_FIND` không khớp hoặc khớp nhiều chỗ sẽ KHÔNG được áp và được báo lại ở vòng sau — nên chép đoạn cần tìm đúng từ bản hiện tại, đừng gõ lại theo trí nhớ.
