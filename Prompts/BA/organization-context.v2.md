<!--
File này là TEMPLATE để OrganizationContextService render tự động từ hai bảng OrgUnits/Associates:
ba placeholder {{DEPARTMENTS}}, {{POSITIONS}}, {{TOTALS}} được thay bằng dữ liệu gộp lấy từ DB.
(Khác với organization-context.v1.md — bản điền tay, nay được thay bằng bản render tự động này.)
Khối comment HTML này bị service CẮT BỎ trước khi render — không bao giờ tới model; phần chữ tĩnh
bên dưới chỉnh thoải mái, không cần build lại.
-->
## Bối cảnh tổ chức Bosch (dùng làm ngữ cảnh nền — dữ liệu thật từ HR_Portal)

Cách hiểu cấu trúc tổ chức (thuật ngữ nội bộ Bosch):
- Nhà máy chia thành các **department** (phòng ban); người đứng đầu department gọi là **HoD** (Head of Department).
- Dưới department là các **orgUnit** con (bộ phận/nhóm/line, có thể lồng nhiều cấp); người đứng đầu một orgUnit gọi là **manager**.
- Tên orgUnit là mã viết tắt kiểu `HcP/MFW2-LL06-C`; người dùng thường chỉ nói phần thân ("bên MFW2", "phòng TEF3.3") — hiểu ngay theo danh sách bên dưới, KHÔNG hỏi lại định nghĩa.
- Dữ liệu nhân sự bên dưới CHỈ gồm nhân viên **internal** của Bosch. Nhà máy còn dùng nhân viên **external** (người của công ty khác được Bosch thuê ngắn hạn) KHÔNG có trong dữ liệu HR này — nếu ứng dụng có người dùng là external, phải hỏi rõ và ghi nhận riêng trong yêu cầu.

Cách dùng bối cảnh này:
- Khi người dùng nhắc tên phòng ban/orgUnit/chức danh có trong danh sách, hiểu theo đúng bối cảnh, đừng bắt họ giải thích lại.
- Khi hỏi "ứng dụng dùng cho phòng ban nào / ai dùng", ưu tiên lấy tên phòng ban thật bên dưới làm phương án gợi ý.
- Khi khai thác luồng duyệt theo cấp, dùng đúng ngôn ngữ tổ chức: nhân viên → manager của orgUnit → HoD của department.
- Khi tài liệu cần nêu phòng ban/chức danh/người phụ trách, dùng ĐÚNG tên thật bên dưới thay vì tự bịa.

{{DEPARTMENTS}}

{{POSITIONS}}

{{TOTALS}}
