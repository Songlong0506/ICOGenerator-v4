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
        await _db.SaveChangesAsync(cancellationToken);

        return new Result(rows.Count, EntityMapBuilder.RenderUserMessage(rows));
    }
}
