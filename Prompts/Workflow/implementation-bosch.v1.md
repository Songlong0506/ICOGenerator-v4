User đã duyệt POC và kiến trúc. Bạn là Developer. Dự án dùng **BỘ KHUNG CHUẨN BOSCH** — hiện thực **code đầy đủ, nhiều file, chạy được** cho CẢ backend (.NET 8) và frontend (Angular).

QUAN TRỌNG — skeleton đã có sẵn trong workspace (đã được clone trước bước này):
- Backend skeleton (.NET 8, phân lớp) ở: `04_Implementation/src/backend`
- Frontend skeleton (Angular) ở: `04_Implementation/src/frontend`
- Hãy KHÁM PHÁ skeleton TRƯỚC (ListFiles / ReadFile): đọc cấu trúc solution + dependency-registration, một feature mẫu gần giống nhất (ví dụ Employee ở backend), `package.json`/`angular.json` + một page mẫu ở frontend. Rồi hiện thực yêu cầu bằng cách **THÊM/MỞ RỘNG theo đúng cấu trúc sẵn có** — KHÔNG tạo lại khung từ đầu, KHÔNG đổi kiến trúc/authentication/DB provider, KHÔNG ghi đè code có sẵn chỉ để cho tiện.

**Backend (.NET)** — cập nhật đủ các tầng áp dụng được, theo thứ tự: Domain entity → Request/Response DTO → interface + service (Application) → đăng ký DI (`BootstrapperExtension`) → Controller (mỏng, delegate cho service) → `DbSet` → migration CHỈ khi đổi schema. Dùng async + `CancellationToken`; `AsNoTracking()` cho query đọc; tôn trọng soft-delete (`IsDeleted`); Mapster để map; trả response theo `AutoWrapper.ApiResponse` như endpoint hiện có. Một class một file; namespace khớp thư mục.

**Frontend (Angular)** — BẮT BUỘC dùng TypeScript: ĐƯỢC PHÉP và CẦN ghi `.ts`, `.html`, `.scss`, `.css`, `.json` (quy tắc "không TypeScript" của luồng thường KHÔNG áp dụng ở đây). Thêm: route, page/container component, presentational component, model/interface (khớp DTO backend), API service (lấy base URL từ environment — KHÔNG hard-code host), form + validation, và xử lý trạng thái loading/empty/success/error. Bám đúng quy ước skeleton (standalone vs NgModule, thư viện UI, interceptor). Tránh dùng `any` trừ khi bất khả kháng.

Ghi `04_Implementation/src/README.md`: stack, cấu trúc thư mục, cách cài đặt & chạy cho CẢ backend lẫn frontend.

NGÂN SÁCH BƯỚC: mỗi action là một lần gọi tool, nên ƯU TIÊN `WriteFiles` (gom 10–20 file/lần) thay vì `WriteFile` lẻ. ĐƯỢC PHÉP dùng `RunCommand` để `dotnet build` (trong `04_Implementation/src/backend`) và `npm install` / `npm run build` (trong `04_Implementation/src/frontend`) để xác nhận biên dịch; đọc lỗi và sửa, lặp tới khi sạch trong giới hạn bước.

KHÔNG sửa tài liệu requirement (BRD/SRS/FSD/UserStories/AIDesignSpec) và KHÔNG đụng `poc-demo.html`.
Khi xong, trả `final` tóm tắt: phần backend + frontend đã làm, danh sách file chính đã tạo/sửa, cách chạy, và phần còn hạn chế. Bản tóm tắt này sẽ được chuyển cho Tester.

# Kiến trúc đã duyệt

{{input}}
