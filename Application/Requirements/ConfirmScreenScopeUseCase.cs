using System.Text.Json;
using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

/// <summary>
/// CHỐT bảng màn hình: người dùng bỏ tích màn hình không cần, sửa việc của từng màn rồi gửi. Lưu vào
/// <see cref="ICOGenerator.Domain.Project.ScreenScopeMap"/>.
///
/// <para>
/// Đây là bảng mà bảng phân quyền sẽ đứng lên: <see cref="PermissionMatrixGate.EffectiveScreens"/> đọc nó
/// làm nguồn DÒNG thay cho <c>Project.PlannedScope</c> thô. Trước khi có bước này, toàn bộ phần phân quyền
/// — thứ đã được dựng cẩn thận để có bằng chứng trên từng ô — lại đứng trên một danh sách màn hình do LLM
/// chắt mà người dùng chưa bao giờ nhìn thấy để phản đối.
/// </para>
///
/// <para>
/// Cùng khuôn HAI BƯỚC với <see cref="ConfirmPermissionMatrixUseCase"/>: chỉ lưu, không gọi LLM; tin nhắn
/// kể lại do server soạn từ bảng đã chuẩn hoá rồi trình duyệt gửi qua đúng đường chat thường.
/// </para>
/// </summary>
public class ConfirmScreenScopeUseCase
{
    private readonly AppDbContext _db;

    public ConfirmScreenScopeUseCase(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Số màn hình đã lưu + tin nhắn mà trình duyệt phải gửi tiếp vào khung chat.</summary>
    public sealed record Result(int Rows, string Message);

    public async Task<Result> ExecuteAsync(Guid projectId, string? screensJson, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project == null)
            return new Result(0, string.Empty);

        // Tên màn hình phải khớp lại phạm vi đã chắt của dự án — server không tin payload nó vừa render ra.
        var plannedScope = InterviewOutlookService.ParseItems(project.PlannedScope);
        var rows = ScreenScopeMapBuilder.Sanitize(ScreenScopeMapBuilder.Parse(screensJson), plannedScope);
        if (rows.Count == 0)
            return new Result(0, string.Empty);

        project.ScreenScopeMap = JsonSerializer.Serialize(rows);
        await _db.SaveChangesAsync(cancellationToken);

        return new Result(rows.Count, ScreenScopeMapBuilder.RenderUserMessage(rows));
    }
}
