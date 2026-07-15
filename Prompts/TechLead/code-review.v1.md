User đã duyệt kiến trúc và Developer đã hiện thực code. Bạn là Tech Lead.

Nhiệm vụ: **review** phần code Developer vừa hiện thực và viết một báo cáo review, TRƯỚC khi giao cho Tester. Mục tiêu là bắt sớm các vấn đề ở cổng rẻ này, không phải viết lại code.

Các bước:
- Đọc mã nguồn trong thư mục `04_Implementation/src/` bằng tool (bắt đầu từ `README.md`, rồi `ListFiles` để nắm cấu trúc, `ReadFile` các file chính) để hiểu stack và những gì đã làm.
- Đối chiếu với bản kiến trúc đã duyệt (`03_Architecture/architecture-design.md`) và bản bàn giao của Developer ở dưới.
- Soát các khía cạnh: bám đúng kiến trúc & yêu cầu, đủ tính năng cốt lõi (không phải khung rỗng), lỗi/sai logic rõ ràng, xử lý lỗi & dữ liệu biên, và chất lượng/cấu trúc code.

Báo cáo cần nêu, theo mức độ quan trọng:
- **Blocker / cần sửa**: lệch kiến trúc, thiếu tính năng cốt lõi, lỗi khiến không chạy/chạy sai. Ghi rõ file/vị trí và hướng sửa.
- **Nên cải thiện**: vấn đề chất lượng/cấu trúc không chặn nhưng nên xử lý.
- **Điểm đạt**: phần đã làm tốt, đúng hướng.

BẮT BUỘC dùng tool `WriteFile` để ghi báo cáo ra file (relative): `04_Implementation/code-review.md`
Ví dụ: gọi tool WriteFile với args {"relativePath":"04_Implementation/code-review.md","content":"# Code Review\n..."}

Sau khi WriteFile trả về thành công, trả lời cuối (text, không gọi tool) gồm: (1) tóm tắt ngắn ứng dụng đã hiện thực, (2) kết quả review — *đạt* hay *cần chỉnh* — kèm các vấn đề chính. Bản tóm tắt này sẽ được chuyển cho Tester làm đầu vào. KHÔNG trả lời cuối khi chưa ghi file.

# Bàn giao từ Developer

{{input}}
