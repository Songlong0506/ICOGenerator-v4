using System.Text.Json;
using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

/// <summary>
/// CHỐT bảng phân quyền: người dùng chọn phạm vi cho từng ô (vai trò × chức năng × màn hình) rồi gửi. Lưu
/// vào <see cref="ICOGenerator.Domain.Project.PermissionMatrix"/> — từ đó <see cref="BAChatService"/>,
/// <see cref="RequirementCoverageService"/> và prompt sinh AI Design Spec đọc ra.
///
/// <para>
/// Use case này CHỈ lưu, không gọi LLM và không ghi lượt hội thoại nào — cùng khuôn với
/// <see cref="ConfirmSourceColumnMapUseCase"/>. Phần "kể lại cho BA nghe" đi đúng đường chat thường: trình
/// duyệt soạn bảng đã chốt thành một tin nhắn của người dùng rồi gửi qua khung chat. Nhờ vậy hội thoại vẫn
/// chỉ có MỘT đường ghi, và mọi thứ đã đúng ở lượt chat (cổng readiness, chắt lọc bản đồ bao phủ, nhật ký
/// điều đã chốt, bản Product Brief đọc transcript) tự khắc đúng ở đây.
/// </para>
/// </summary>
public class ConfirmPermissionMatrixUseCase
{
    private readonly AppDbContext _db;

    public ConfirmPermissionMatrixUseCase(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Kết quả chốt bảng: số dòng đã lưu + tin nhắn mà trình duyệt phải gửi tiếp vào khung chat. Server soạn
    /// tin nhắn (thay vì để JS tự ghép) vì nó phải khớp ĐÚNG bảng đã được chuẩn hoá và lưu — không phải bảng
    /// client vừa gửi lên: hai bản lệch nhau thì hội thoại kể một đằng còn dữ liệu dự án ghi một nẻo.
    /// </summary>
    public sealed record Result(int Rows, string Message);

    public async Task<Result> ExecuteAsync(Guid projectId, string? matrixJson, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project == null)
            return new Result(0, string.Empty);

        // Server KHÔNG tin bảng client gửi, kể cả khi chính server vừa render nó ra: tên màn hình phải khớp
        // lại phạm vi đã chắt của dự án, và mọi dòng phải đủ vai trò (xem PermissionMatrixBuilder).
        var plannedScope = InterviewOutlookService.ParseItems(project.PlannedScope);
        var rows = PermissionMatrixBuilder.Sanitize(PermissionMatrixBuilder.Parse(matrixJson), plannedScope);
        if (rows.Count == 0)
            return new Result(0, string.Empty);

        project.PermissionMatrix = JsonSerializer.Serialize(rows);
        await _db.SaveChangesAsync(cancellationToken);

        return new Result(rows.Count, PermissionMatrixBuilder.RenderUserMessage(rows));
    }
}
