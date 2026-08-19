using System.Text.Json;
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

        // GIEO MÀN HÌNH DANH MỤC VÀO PHẠM VI. Một thông tin có nguồn "ứng dụng tự quản lý" là một danh mục
        // mà ứng dụng phải có màn hình CRUD riêng để quản lý — quyết định đó vừa được người dùng chốt ở đây,
        // nhưng bảng MÀN HÌNH đứng TRƯỚC bảng này trong thứ tự phụ thuộc nên nó đã chốt xong từ mấy lượt
        // trước. Không ghi vào phạm vi thì màn hình ấy không có dòng nào trong bảng phân quyền và không có
        // mục nào ở `## 6. Screens To Generate`: mặc nhiên "không ai được xem" một màn hình người dùng vừa
        // đặt hàng, đúng loại quyết định câm mà cả bộ bảng sinh ra để chặn.
        //
        // Chỉ cần ghi vào PlannedScope, không cổng nào phải sửa: ScreenScopeGate đã có sẵn đường MỞ LẠI
        // bảng màn hình khi phạm vi trôi sau lúc chốt (ScreenScopeMapBuilder.NewScreens so bảng đã chốt với
        // PlannedScope), và bảng chưa chốt thì các mục này vào thẳng lần bày đầu.
        //
        // GHÉP THÊM chứ không ghi đè, và giữ nguyên thứ tự cũ: PlannedScope là danh sách người dùng đã rà ở
        // bảng màn hình (ConfirmScreenScopeUseCase ghi ngược lên đây), nên thay nó bằng mấy dòng danh mục
        // là xoá sạch phạm vi đã duyệt. Mục trùng bị bỏ vì lần chốt thứ hai của cùng một bảng sẽ gieo lại
        // đúng các danh mục ấy — thêm lần nữa là đẻ ra dòng trùng trong bảng màn hình.
        var scope = InterviewOutlookService.ParseItems(project.PlannedScope).ToList();
        var known = new HashSet<string>(scope.Select(NormalizeScope), StringComparer.Ordinal);
        var added = EntityMapBuilder.ManagedListScreens(rows).Where(s => known.Add(NormalizeScope(s))).ToList();
        if (added.Count > 0)
        {
            scope.AddRange(added);
            project.PlannedScope = InterviewOutlookService.Store(scope);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return new Result(rows.Count, EntityMapBuilder.RenderUserMessage(rows));
    }

    /// <summary>
    /// Khoá so trùng của một mục phạm vi — chép đúng phép chuẩn hoá mà <c>ScreenScopeMapBuilder</c> dùng để
    /// nhận ra "màn hình mới", để một mục vừa gieo không quay lại thành mục mới ở lượt sau chỉ vì khác hoa
    /// thường hay khác một dấu câu ở cuối.
    /// </summary>
    private static string NormalizeScope(string value)
        => string.Join(' ', (value ?? string.Empty).ToLowerInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim(' ', '.', ',', ':', ';', '-', '–');
}
