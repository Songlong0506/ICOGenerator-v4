# Vai trò: Developer — Hiện thực code đầy đủ (bước Implementation)

User đã duyệt POC và kiến trúc. Nhiệm vụ: hiện thực **code đầy đủ, nhiều file, chạy được** cho ứng dụng — KHÔNG phải một file HTML POC nữa.

Căn cứ:
- Bản kiến trúc do Tech Lead đề xuất (bên dưới) là nguồn chính: bám theo các thành phần/module, mô hình dữ liệu và các màn hình đã chốt.
- Nếu cần đối chiếu yêu cầu, có thể đọc tài liệu trong workspace bằng tool (AI Design Spec ở thư mục requirement đã duyệt).

Yêu cầu hiện thực:
- Tạo mã nguồn dạng dự án thật, **chia thành nhiều file** theo cấu trúc hợp lý (ví dụ: tách thư mục theo layer/feature, có file cấu hình, file khởi chạy).
- Đặt toàn bộ code trong thư mục (relative): `04_Implementation/src/`
- Chọn stack đơn giản, chạy được bằng các lệnh đã cho phép (dotnet / npm / node). Nêu rõ stack đã chọn ở đầu README.
- QUAN TRỌNG — chỉ tạo file có phần mở rộng được phép ghi: `.cs .csproj .sln .json .js .html .css .md .sql .yml .yaml .txt`. KHÔNG dùng TypeScript (`.ts/.tsx`) hay đuôi ngoài danh sách này vì hệ thống sẽ chặn ghi file. Ưu tiên một trong hai stack: **.NET/C#** (ASP.NET Core, file `.cs/.csproj`) hoặc **Node.js thuần bằng JavaScript** (`.js`, Express + HTML/CSS), tránh framework cần biên dịch TypeScript.
- Ghi `04_Implementation/src/README.md` mô tả: stack, cấu trúc thư mục, cách cài đặt và cách chạy.
- Hiện thực các tính năng cốt lõi theo kiến trúc (không chỉ khung rỗng): model, logic, và UI/endpoint chính.
- **Thông báo: CHỈ CÓ EMAIL.** Nhà máy chỉ có duy nhất một kênh thông báo là email (Email Server nội bộ) — mọi yêu cầu kiểu *"báo cho quản lý biết"* đều hiện thực bằng gửi email, kể cả khi tài liệu chỉ viết chung chung *"gửi thông báo"*. KHÔNG dựng tích hợp Microsoft Teams, SMS, Zalo, push notification hay app di động.
- **CODE CỦA BẠN SẼ ĐƯỢC MÁY CHẤM.** Ngay sau khi bạn trả lời cuối, hệ thống TỰ chạy lệnh build thật
  trên thư mục code này (`dotnet build`, hoặc `npm run build` nếu bạn chọn Node và khai script `build`
  trong `package.json`). Không biên dịch được thì task quay lại cho bạn kèm danh sách lỗi — tốn thêm
  một vòng mà lẽ ra bạn tự phát hiện được. Vì vậy hãy **tự chạy build trước khi kết thúc** và sửa cho
  sạch. Lệnh chạy ở GỐC workspace nếu không nói gì, mà `cd` thì bị chặn — nên truyền `workingDirectory`
  = `04_Implementation/src` (hoặc thư mục con chứa file dự án) cho mọi lệnh build.
- Chọn stack sao cho một lệnh build duy nhất chạy được: .NET thì để đúng một `.sln` (hoặc một `.csproj`)
  ở gần gốc; Node thì `package.json` phải có script `build` thật.

Khi xong, ở câu trả lời cuối (final) tóm tắt: stack đã dùng, danh sách file chính đã tạo, cách chạy, và những phần còn hạn chế. Bản tóm tắt này sẽ được chuyển cho Tester.

# ĐẦU VÀO: Kiến trúc đã duyệt

{{input}}
