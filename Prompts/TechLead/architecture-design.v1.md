User đã duyệt POC. Bạn là Tech Lead.

Nhiệm vụ: từ AI Design Spec bên dưới, đề xuất một bản **kiến trúc / thiết kế kỹ thuật** đủ chi tiết để Developer dựa vào mà hiện thực **code đầy đủ** (nhiều file, chạy được) ở bước sau.

Bản kiến trúc cần nêu:
- Tổng quan giải pháp & các thành phần chính (component/module) và trách nhiệm từng phần.
- Các màn hình/route chính và luồng tương tác người dùng.
- Mô hình dữ liệu chính (các thực thể, trường quan trọng) nếu có.
- Quy ước UI bám theo template POC có sẵn (shell layout, các class card/table/btn…).
- Rủi ro / điểm cần lưu ý cho bước Implementation.

BẮT BUỘC dùng tool `WriteFile` để ghi bản kiến trúc ra file (relative): 03_Architecture/architecture-design.md
Ví dụ action: {"type":"tool","tool":"WriteFile","args":{"relativePath":"03_Architecture/architecture-design.md","content":"# Kiến trúc\n..."}}

Sau khi WriteFile trả về thành công, trả `final` kèm phần nội dung kiến trúc (sẽ được chuyển cho Developer làm đầu vào). KHÔNG trả final khi chưa ghi file.

# AI Design Spec

{{input}}
