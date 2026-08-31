using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

/// <summary>
/// CHỐT bảng đối tượng nghiệp vụ: người dùng rà thông tin cần lưu, vòng đời trạng thái và người nhận thông
/// báo ở mỗi chuyển trạng thái rồi gửi. Lưu vào <see cref="ICOGenerator.Domain.Project.EntityMap"/> — từ đó
/// mục <c>## 8. Data Model Summary</c> của AI Design Spec được dựng, thay vì để bước sinh spec tự nghĩ ra
/// mô hình dữ liệu từ văn xuôi của Product Brief.
///
/// <para>
/// Cùng khuôn HAI BƯỚC với <see cref="ConfirmPermissionMatrixUseCase"/>: chỉ lưu, không gọi LLM; tin nhắn
/// kể lại do server soạn từ bảng đã chuẩn hoá rồi trình duyệt gửi qua đúng đường chat thường.
/// </para>
/// </summary>
public class ConfirmEntityMapUseCase
{
    private readonly AppDbContext _db;

    public ConfirmEntityMapUseCase(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Số đối tượng đã lưu + tin nhắn mà trình duyệt phải gửi tiếp vào khung chat.</summary>
    public sealed record Result(int Rows, string Message);

    public async Task<Result> ExecuteAsync(Guid projectId, string? entitiesJson, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project == null)
            return new Result(0, string.Empty);

        var rows = EntityMapBuilder.Sanitize(EntityMapBuilder.Parse(entitiesJson));
        if (rows.Count == 0)
            return new Result(0, string.Empty);

        project.EntityMap = JsonSerializer.Serialize(rows);

        // GIEO MÀN HÌNH DANH MỤC VÀO BẢNG MÀN HÌNH. Một thông tin có nguồn "ứng dụng tự quản lý" là một
        // danh mục mà ứng dụng phải có màn hình CRUD riêng để quản lý — và đó là lý do bảng này đứng TRƯỚC
        // bảng màn hình trong thứ tự phụ thuộc (luồng → đối tượng → báo cáo → màn hình): các mục gieo ở đây
        // có mặt ngay ở lần bày ĐẦU của bảng màn hình, nên người dùng rà trọn phạm vi đúng MỘT lần. Không
        // gieo thì màn hình ấy không có dòng nào trong bảng phân quyền và không có mục nào ở
        // `## 6. Screens To Generate`: mặc nhiên "không ai được xem" một màn hình người dùng vừa đặt hàng,
        // đúng loại quyết định câm mà cả bộ bảng sinh ra để chặn.
        //
        // Gieo bằng đúng đường mà lượt chắt lọc dùng (ScreenScopeMapBuilder.Merge): mục mới vào bảng ở
        // trạng thái CHỜ DUYỆT, mục trùng một dòng đã có thì bỏ, mục trùng một dòng người dùng đã BỎ TÍCH
        // cũng bỏ. Chỉ THÊM, không đụng tới dòng nào đang có — ở ca bảng màn hình đã chốt TRƯỚC bảng này
        // (cổng đối tượng mở muộn), bảng ấy là thứ người dùng vừa tự tay rà, và đường MỞ LẠI của
        // ScreenScopeGate sẽ đưa các danh mục vừa gieo ra hỏi ở lượt sau.
        var merged = ScreenScopeMapBuilder.Merge(
            project.ScreenScopeMap,
            EntityMapBuilder.ManagedListScreens(rows).Select(s => new ScopeAddition { Screen = s }));
        if (merged != null)
            project.ScreenScopeMap = JsonSerializer.Serialize(merged);

        await _db.SaveChangesAsync(cancellationToken);

        return new Result(rows.Count, EntityMapBuilder.RenderUserMessage(rows));
    }
}
