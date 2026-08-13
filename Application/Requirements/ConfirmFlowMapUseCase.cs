using System.Text.Json;
using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

/// <summary>
/// CHỐT bảng luồng nghiệp vụ: người dùng sửa/bỏ tích từng bước rồi gửi. Lưu vào
/// <see cref="ICOGenerator.Domain.Project.FlowMap"/> — từ đó <see cref="BAChatService"/>,
/// <see cref="RequirementCoverageService"/> và prompt sinh AI Design Spec đọc ra.
///
/// <para>
/// Cùng khuôn HAI BƯỚC với <see cref="ConfirmPermissionMatrixUseCase"/>: use case này CHỈ lưu, không gọi
/// LLM và không ghi lượt hội thoại nào. Phần "kể lại cho BA nghe" đi đúng đường chat thường — trình duyệt
/// gửi tiếp tin nhắn mà SERVER soạn từ bảng đã chuẩn hoá. Nhờ vậy hội thoại vẫn chỉ có MỘT đường ghi, và
/// mọi thứ đã đúng ở lượt chat (cổng readiness, chắt lọc bản đồ bao phủ, nhật ký điều đã chốt) tự khắc
/// đúng ở đây.
/// </para>
/// </summary>
public class ConfirmFlowMapUseCase
{
    private readonly AppDbContext _db;

    public ConfirmFlowMapUseCase(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Số luồng đã lưu + tin nhắn mà trình duyệt phải gửi tiếp vào khung chat.</summary>
    public sealed record Result(int Rows, string Message);

    public async Task<Result> ExecuteAsync(Guid projectId, string? flowJson, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        if (project == null)
            return new Result(0, string.Empty);

        // Server không tin bảng client gửi, kể cả khi chính nó vừa render ra vài giây trước.
        var rows = FlowMapBuilder.Sanitize(FlowMapBuilder.Parse(flowJson));
        if (rows.Count == 0)
            return new Result(0, string.Empty);

        project.FlowMap = JsonSerializer.Serialize(rows);
        await _db.SaveChangesAsync(cancellationToken);

        return new Result(rows.Count, FlowMapBuilder.RenderUserMessage(rows));
    }
}
