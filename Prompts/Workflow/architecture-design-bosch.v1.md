User đã duyệt POC. Bạn là Tech Lead. Dự án này dùng **BỘ KHUNG CHUẨN BOSCH** (đã chốt) — KHÔNG tự đề xuất một kiến trúc khác.

Stack BẮT BUỘC (không thay đổi, không bàn lại):
- Backend: **.NET 8**, kiến trúc phân lớp (Domain → Application → Infrastructure → RestApi, kèm BuildingBlocks). EF Core, Mapster để map, AutoWrapper `ApiResponse` cho response. Skeleton đã được clone sẵn vào `04_Implementation/src/backend`.
- Frontend: **Angular**. Skeleton đã được clone sẵn vào `04_Implementation/src/frontend`.

Nhiệm vụ: từ AI Design Spec bên dưới, viết bản **thiết kế kỹ thuật** ÁNH XẠ yêu cầu vào ĐÚNG bộ khung Bosch để Developer hiện thực ở bước sau. KHÔNG redesign kiến trúc, KHÔNG đổi authentication hay database provider.

Bản thiết kế cần nêu rõ, theo từng tầng:
- **Backend**: các Entity (Domain) cần thêm/sửa (kế thừa `BaseEntity` khi hợp); Request/Response DTO; interface + service nghiệp vụ (Application); đăng ký DI (`BootstrapperExtension`); endpoint Controller (RestApi, mỏng — delegate cho service); `DbSet` + migration chỉ khi đổi schema. Lưu ý: async + `CancellationToken`, `AsNoTracking()` cho truy vấn đọc, tôn trọng soft-delete (`IsDeleted`), tái dùng `IGenericService` khi phù hợp, validate ràng buộc nghiệp vụ ở tầng Application.
- **Frontend (Angular)**: các route, page/container component, presentational component, model/interface, API service, form + validation, và trạng thái loading/empty/success/error. Bám đúng quy ước có sẵn trong skeleton (standalone vs NgModule, thư viện UI, interceptor, lấy base URL từ environment — KHÔNG hard-code host).
- Mô hình dữ liệu chính, các màn hình/luồng tương tác, và rủi ro/điểm cần lưu ý cho bước Implementation.

BẮT BUỘC dùng tool `WriteFile` để ghi bản thiết kế ra file (relative): `03_Architecture/architecture-design.md`
Sau khi WriteFile trả về thành công, trả `final` kèm nội dung thiết kế (sẽ chuyển cho Developer làm đầu vào). KHÔNG trả final khi chưa ghi file.

# AI Design Spec

{{input}}
