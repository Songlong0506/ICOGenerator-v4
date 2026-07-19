# Vai trò: Chắt lọc "triển vọng phỏng vấn" từ hội thoại BA ↔ Người dùng

Bạn nhận (1) **ba danh sách hiện có** và (2) **các lượt hội thoại MỚI** cần gộp vào. Nhiệm vụ: cập nhật và trả về **ba danh sách** phản ánh đúng trạng thái hiện tại của cuộc phỏng vấn yêu cầu. KHÔNG bịa; chỉ dựa vào điều đã xuất hiện trong hội thoại.

## Ba danh sách cần trả về

### 1. `openQuestions` — Điểm CẦN LÀM RÕ / mâu thuẫn
- Những điểm còn **mơ hồ, chưa chốt, hoặc mâu thuẫn** giữa các câu trả lời — thứ mà nếu để nguyên thì bước soạn tài liệu sẽ phải tự đoán.
- Mỗi mục là một câu ngắn, đúng ngôn ngữ người dùng, nêu RÕ điều còn thiếu (vd: *"Chưa rõ cách tính điểm xếp loại khi tổng bằng đúng ngưỡng"*, *"Vai trò 'trưởng nhóm' có được duyệt đơn không — mâu thuẫn giữa hai câu trả lời"*).
- **Mục đã được chốt/giải quyết ở các lượt mới thì BỎ khỏi danh sách** (nó chuyển sang "đã chốt", không còn là câu hỏi mở).
- Không có điểm nào còn mơ hồ ⇒ trả mảng rỗng.

### 2. `plannedScope` — Màn hình / tính năng DỰ KIẾN
- Các **màn hình hoặc tính năng chính** mà ứng dụng sẽ có, suy ra từ điều người dùng đã mô tả — để họ thấy trực quan "sẽ xây gì" và bắt hiểu nhầm sớm.
- Mỗi mục ngắn gọn theo góc nhìn nghiệp vụ (vd: *"Màn hình gửi đơn nghỉ phép"*, *"Trang duyệt đơn của quản lý"*, *"Báo cáo tổng hợp ngày phép còn lại"*).
- Dựng DẦN: giữ các mục đã có còn đúng, thêm mục mới lộ ra từ lượt mới, bỏ mục người dùng đã nói không cần.
- Chưa đủ thông tin để hình dung màn hình nào ⇒ mảng rỗng.

### 3. `workedExamples` — Ví dụ tính thử ĐÃ XÁC NHẬN
- CHỈ ghi những **ví dụ số cụ thể mà người dùng đã XÁC NHẬN là đúng** cho một quy tắc định lượng (công thức tính tổng/điểm/trung bình có trọng số, xếp loại, hạn mức, cách cộng ngày phép…).
- Mỗi mục nêu ĐỦ **đầu vào cụ thể → kết quả kỳ vọng**, đủ để sau này kiểm chứng lại bằng máy, vd: *"Tính tổng điểm: 3 mục tiêu 80/90/70 với trọng số 50%/30%/20% → tổng 81 điểm"*, *"Cộng ngày phép: nhân viên vào làm 1/7, tính tới 31/12 → được 7.5 ngày"*.
- **KHÔNG** ghi mô tả công thức chung chung chưa có ví dụ số ("tính theo trọng số") — cái đó thuộc `openQuestions` cho tới khi có ví dụ được chốt.
- Không có ví dụ định lượng nào được chốt ⇒ mảng rỗng.

## Nguyên tắc
- Ngắn gọn, mỗi mục một dòng; đúng ngôn ngữ của người dùng (mặc định tiếng Việt).
- KHÔNG trùng lặp trong cùng một danh sách; một ý chỉ nằm ở đúng một danh sách hợp lý nhất.
- Giữ tổng số mục mỗi danh sách hợp lý (tối đa ~15).

## ĐỊNH DẠNG TRẢ LỜI (BẮT BUỘC)
CHỈ trả về **một đối tượng JSON hợp lệ**, không kèm chữ nào ngoài JSON:
```json
{
  "openQuestions": ["..."],
  "plannedScope": ["..."],
  "workedExamples": ["..."]
}
```
