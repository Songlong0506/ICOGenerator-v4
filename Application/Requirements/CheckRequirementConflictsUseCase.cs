using ICOGenerator.Contracts.Requirements;
using ICOGenerator.Data;
using ICOGenerator.Services.Requirements;
using Microsoft.EntityFrameworkCore;

namespace ICOGenerator.Application.Requirements;

/// <summary>
/// Cổng soát MÂU THUẪN chạy ngay trước "Write Requirement": trả về các cặp điều đã chốt không thể cùng
/// đúng để người dùng chốt lại bằng một cú bấm, trước khi chúng đóng băng vào tài liệu.
///
/// Fail-open có chủ đích: chưa cấu hình BA / lời gọi lỗi ⇒ danh sách rỗng và người dùng đi tiếp như cũ —
/// cổng này chỉ được phép dừng đường đi khi nó THẬT SỰ tìm thấy hai câu chọi nhau.
/// </summary>
public class CheckRequirementConflictsUseCase
{
    private readonly AppDbContext _db;
    private readonly RequirementConflictService _conflicts;
    private readonly BAAgentResolver _agentResolver;

    public CheckRequirementConflictsUseCase(AppDbContext db, RequirementConflictService conflicts, BAAgentResolver agentResolver)
    {
        _db = db;
        _conflicts = conflicts;
        _agentResolver = agentResolver;
    }

    public async Task<IReadOnlyList<RequirementConflict>> ExecuteAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);
        if (project == null)
            return Array.Empty<RequirementConflict>();

        var ba = await _agentResolver.FindConfiguredAsync(cancellationToken);
        if (ba == null)
            return Array.Empty<RequirementConflict>();

        return await _conflicts.DetectAndStoreAsync(project, ba, ba.AiModel!, cancellationToken);
    }
}
