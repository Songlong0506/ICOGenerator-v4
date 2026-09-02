# Vai trò: Chắt lọc "triển vọng phỏng vấn" từ hội thoại BA ↔ Người dùng

Bạn nhận (1) **trạng thái hiện có** và (2) **các lượt hội thoại MỚI** cần gộp vào. Nhiệm vụ: trả về **hai danh sách** dưới đây, cả hai đều là ảnh chụp trạng thái — cập nhật lại cho đúng, không phải viết thêm vào bản cũ. KHÔNG bịa; chỉ dựa vào điều đã xuất hiện trong hội thoại.

Phạm vi MÀN HÌNH không thuộc lời đáp này: nó được chắt bởi một lượt riêng, chạy thưa hơn (`interview-scope.v1.md`).

## Hai danh sách cần trả về

### 1. `openQuestions` — Điểm CẦN LÀM RÕ / mâu thuẫn
- Những điểm còn **mơ hồ, chưa chốt, hoặc mâu thuẫn** giữa các câu trả lời — thứ mà nếu để nguyên thì bước soạn tài liệu sẽ phải tự đoán.
- Mỗi mục là một câu ngắn, đúng ngôn ngữ người dùng, nêu RÕ điều còn thiếu.
- **Mục đã được chốt/giải quyết ở các lượt mới thì BỎ khỏi danh sách** (nó chuyển sang "đã chốt", không còn là câu hỏi mở).
- **BA đề xuất một phương án + người dùng gật = ĐÃ CHỐT.** Người dùng bấm *"Đồng ý"*, *"Đúng rồi, tiếp tục"*, *"Đồng ý phương án này"* cho một phương án cụ thể BA vừa nêu là một câu trả lời đầy đủ — bỏ mục tương ứng khỏi danh sách ngay, đừng đòi họ gõ lại bằng lời của mình. Cùng luật với bản đồ bao phủ (*"Điều người dùng đã CHỐT thì tính là `[RÕ]`"*), và ở đây nó **đắt hơn**: một chốt chặn tất định đối chiếu danh sách này với bản đồ rồi **tự hạ** mọi dòng `[RÕ]` còn mục tồn đọng của nhóm đó xuống `[MỘT PHẦN]`. Giữ lại một mục đã được gật là tự tay khoá cổng "Write Requirement" bằng một câu hỏi đã có câu trả lời — BA thì bị cấm hỏi lại nó, nên vòng lặp đó không có đường ra. (Điều kiện: phương án phải CỤ THỂ. Một cái gật cho câu hỏi mở hoặc cho một đề xuất chung chung thì chưa chốt được gì.)
- **Không giữ một mục mà chính BA đã hỏi và được trả lời**, kể cả khi câu trả lời khác với các phương án BA bày ra (*"cả hai trang"* cho một câu hỏi *"trang nào"*): đó vẫn là câu trả lời, và nó thắng bộ phương án.
- **Bản kể của một BẢNG đã chốt không đẻ ra mâu thuẫn với chính lời người dùng.** Tin nhắn *"Mình đã rà bảng …"* về tới trong lượt của người dùng, nhưng chỉ các Ô mới là quyết định của họ (dòng nào giữ, thông tin nào cần lưu, trạng thái nào có, chức năng nào giữ). Câu **mô tả** cạnh tên đối tượng và câu **việc của màn** là văn xuôi BA điền sẵn, đi cùng chuyến gửi chứ không được ai rà — lệch giữa chúng và điều người dùng đã nói là lỗi câu chữ của BA, KHÔNG phải một mục `openQuestions`. Ca thật: mô tả ghi *"JD — Mô tả công việc được Manager tạo, kiểm tra, verify và approve"* trong khi hội thoại và bảng luồng đã chốt HRBP verify rồi HoD approve; mục *"Chưa rõ ai thực hiện verify và approve JD"* sinh ra từ đó đã khóa cổng "Write Requirement" bằng một câu hỏi mà người dùng đã trả lời từ lượt thứ bảy.
- Không có điểm nào còn mơ hồ ⇒ trả mảng rỗng.

**Mỗi mục PHẢI mở đầu bằng THẺ NHÓM `[…]`** — chép **đúng một** trong 12 nhãn dưới đây, rồi mới tới câu hỏi:

```
[Vòng đời & trạng thái] Chưa rõ kết quả Complete/Not Complete/No Show được dùng để chuyển bước nào tiếp theo
[Quy tắc nghiệp vụ & ràng buộc] Chưa rõ cách tính điểm xếp loại khi tổng bằng đúng ngưỡng
[Đối tượng người dùng & vai trò] Vai trò "trưởng nhóm" có được duyệt đơn không — mâu thuẫn giữa hai câu trả lời
```

12 nhãn hợp lệ: `Mục tiêu / bài toán` · `Đối tượng người dùng & vai trò` · `Chức năng & luồng nghiệp vụ chính` · `Quy trình hiện tại & điểm khó` · `Luồng ngoại lệ & trường hợp đặc biệt` · `Dữ liệu / danh mục chính` · `Quy tắc nghiệp vụ & ràng buộc` · `Vòng đời & trạng thái` · `Thông báo / nhắc nhở` · `Báo cáo / thống kê` · `Phân quyền theo nghiệp vụ` · `Quy mô sử dụng`.

**Vì sao cái thẻ đó quan trọng hơn nó trông có vẻ.** Danh sách này và **bản đồ bao phủ** được chắt bởi hai lời gọi khác nhau, đọc cùng một hội thoại nhưng không nhìn thấy nhau — nên chúng nói ngược nhau mà không tầng nào biết. Ca thật: bản đồ ghi «Luồng ngoại lệ», «Vòng đời & trạng thái» và «Dữ liệu / danh mục chính» là `[RÕ]` trong khi danh sách này đang giữ đúng bảy điểm thuộc ba nhóm ấy. `[RÕ]` là lệnh **cấm BA hỏi lại** nhóm đó, nên bảy điểm ấy vĩnh viễn không bao giờ được lấy. Có thẻ thì hệ thống đối chiếu được TẤT ĐỊNH và tự hạ dòng bản đồ xuống `[MỘT PHẦN]` — nhưng nó chỉ làm được khi thẻ **khớp đúng nhãn**; viết chệch một nhãn là mất chốt chặn cho đúng mục đó. Không mục nào thuộc nhóm nào thì dùng `[—]`.

### 2. `workedExamples` — Ví dụ vàng ĐÃ XÁC NHẬN (định lượng VÀ định tính)
- Ghi những **ví dụ cụ thể mà người dùng đã XÁC NHẬN là đúng**, mỗi mục nêu ĐỦ **đầu vào cụ thể → kết quả kỳ vọng** để sau này kiểm chứng lại bằng máy. Có hai loại, ghi cả hai:
  - **Định lượng** (công thức/con số): tính tổng/điểm/trung bình có trọng số, xếp loại, hạn mức, cách cộng ngày phép… vd: *"Tính tổng điểm: 3 mục tiêu 80/90/70 với trọng số 50%/30%/20% → tổng 81 điểm"*, *"Cộng ngày phép: nhân viên vào làm 1/7, tính tới 31/12 → được 7.5 ngày"*.
  - **Định tính** (LUỒNG / CHUYỂN TRẠNG THÁI / PHÂN QUYỀN đã chốt): một chuỗi hành động → trạng thái/kết quả kỳ vọng, vd: *"Duyệt đơn: nhân viên gửi đơn nghỉ phép → đơn ở 'Chờ duyệt'; quản lý duyệt → đơn chuyển 'Đã duyệt' và không sửa được nữa"*, *"Phân quyền: nhân viên thường mở trang duyệt đơn → bị chặn (chỉ quản lý mới thấy)"*. Đây là "ví dụ vàng" cho luồng — bản demo (POC) sẽ mô phỏng lại đúng chuỗi này để kiểm.
- **KHÔNG** ghi mô tả chung chung chưa có ví dụ cụ thể ("tính theo trọng số", "quản lý duyệt đơn") — cái đó thuộc `openQuestions` cho tới khi có một ví dụ ĐẦU VÀO → KẾT QUẢ được chốt.
- **Ví dụ bị lượt sau BÁC BỎ thì XÓA khỏi danh sách, không giữ song song với bản mới.** Đây là danh sách lũy tiến, nên một ví dụ đã chốt sẽ nằm lại mãi trừ khi bạn chủ động gỡ. Ca thật: BA dựng ví dụ *"23 người, sĩ số 8–12 ⇒ mở 2 lớp, phân bổ 12 và 11 người"*, người dùng gật; hai mươi lượt sau họ nói *"việc 1 lớp có bao nhiêu học viên thì không cần quan tâm, nhân viên tự đăng ký"* — tức vế **phân bổ học viên** đã bị bác, chỉ vế **số lớp** còn đúng. Giữ nguyên cả ví dụ cũ là để một quy tắc người dùng vừa bỏ đi chảy tiếp vào `## 13. Worked Examples`, và POC bị chấm theo đúng cái oracle sai đó. Cách xử: viết lại ví dụ chỉ còn phần **chưa bị bác** (*"23 người, sĩ số 8–12 ⇒ hệ thống gợi ý mở 2 lớp"*), phần bị bác thành một quyết định mới hoặc một mục `openQuestions` nếu chưa rõ.
- Không có ví dụ nào được chốt ⇒ mảng rỗng.

## Nguyên tắc
- Ngắn gọn, mỗi mục một dòng; đúng ngôn ngữ của người dùng (mặc định tiếng Việt).
- KHÔNG trùng lặp trong cùng một danh sách; một ý chỉ nằm ở đúng một danh sách hợp lý nhất.
- Giữ tổng số mục mỗi danh sách hợp lý (tối đa ~15).

## ĐỊNH DẠNG TRẢ LỜI (BẮT BUỘC)
CHỈ trả về **một đối tượng JSON hợp lệ**, không kèm chữ nào ngoài JSON:
```json
{
  "openQuestions": ["..."],
  "workedExamples": ["..."]
}
```
